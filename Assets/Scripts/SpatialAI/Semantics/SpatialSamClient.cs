using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialAI
{
    // SAM 3.1 client (Meta Model API, Responses endpoint, model sam-3.1).
    // Implements the documented contract from dev.meta.ai/docs/sam:
    // one noun-phrase concept per request ("chair"), input item
    // {"type":"message","role":"user"}, streamed SSE response
    // (response.output_text.delta lanes, [DONE]/response.completed end),
    // metadata {"include_confidence":"true"} (string, not boolean), default
    // lossless mask encoding (metadata.mask_encoding omitted).
    // A rotating round-robin schedule evaluates exactly 1 concept per
    // request, with up to DefaultMaxConcurrentRequests requests overlapping:
    // a free slot evaluates the oldest queued frame immediately (each
    // payload carries its own capture-time pose, so queued frames still
    // anchor where the camera first saw the object). Only when every slot
    // is busy is the backlog collapsed to counted stale drops, so a slow
    // network never evaluates old frames late.
    // Detections lift to 3D ONLY from triangulated landmark depth inside
    // the decoded mask; masks without landmarks are skipped loudly with
    // [SAM Depth Warning] telemetry. No estimated or fallback depth exists
    // anywhere on this path. All network I/O is coroutine-based
    // UnityWebRequest: the game thread never blocks on I/O.
    public class SpatialSamClient : MonoBehaviour
    {
        public static string Endpoint { get { return SpatialApiConfig.Instance.ResponsesEndpoint; } }
        public const string DefaultModel = SpatialApiConfig.DefaultSamModelName;

        // Exact high-impact indoor targets, evaluated round-robin.
        static readonly string[] ConceptSchedule =
        {
            "chair", "door", "table", "couch", "stairs", "elevators"
        };

        [Header("References")]
        [SerializeField] SpatialMap spatialMap;
        [Tooltip("Optional Phase 4 reconciler. When present, detections are reconciled against the persistent map (Moved / NewObstruction / Static); otherwise entities register directly.")]
        [SerializeField] SpatialMapReconciler reconciler;

        [Header("Meta SAM 3.1")]
        [Tooltip("Leave empty to use the META_API_KEY environment variable. Never commit a real key.")]
        [SerializeField] string apiKey;
        [SerializeField] string model = DefaultModel;
        [SerializeField] int requestTimeoutSeconds = 30;
        [SerializeField] float pollIntervalSeconds = 0.25f;
        [Tooltip("Overlapping SAM requests in flight. One concept is evaluated per request, so overlap - not a shorter poll - is what raises concept coverage during a scan.")]
        [SerializeField] int maxConcurrentRequests = DefaultMaxConcurrentRequests;
        [Tooltip("When enabled, writes every JPEG actually sent to SAM under persistentDataPath/sam-debug for adb-pull inspection.")]
        [SerializeField] bool saveDebugFrame;

        [Header("Semantic Lifting")]
        [SerializeField] float clusterRadiusMeters = 1.0f;
        [SerializeField] float minimumConfidence = 0.35f;

        public int RequestsSent { get; private set; }
        public int RequestsFailed { get; private set; }
        public int PayloadsSkippedNoKey { get; private set; }
        public int EntitiesDiscovered { get; private set; }
        // Landmark-depth telemetry: valid SAM masks dropped because too few
        // triangulated SLAM landmarks fell inside them, or the ones inside
        // disagreed (sparse/noisy tracking bottleneck signal). Consensus is
        // at least 3 landmarks within 30% of the median: a lone landmark
        // (often background through a sloppy mask) never places a box.
        // Never a depth estimate: drops are the design.
        public int MasksRejectedNoLandmarks { get; private set; }
        public int MasksRejectedBadMask { get; private set; }
        public int MasksFailedDecode { get; private set; }
        public int EmptyResponses { get; private set; }
        public int StalePayloadsDrained { get; private set; }
        public int ConceptsEvaluated { get; private set; }
        public string LastError { get; private set; }
        public bool Busy { get { return inFlight > 0; } }

        // Overlap policy, kept static and pure so regression checks can
        // pin it: a free slot always starts a send; a saturated client
        // collapses the backlog to counted stale drops instead of
        // evaluating old frames late.
        public const int DefaultMaxConcurrentRequests = 4;
        public static bool ShouldStartSend(int inFlight, int maxConcurrent)
        {
            return inFlight < Mathf.Max(1, maxConcurrent);
        }

        Coroutine polling;
        int inFlight;
        int conceptIndex;
        bool warnedNoKey;
        string lastWorkerError;
        ARRaycastManager planeRaycaster;
        readonly List<ARRaycastHit> planeHits = new List<ARRaycastHit>();

        string ResolvedApiKey
        {
            get
            {
                // Centralized configuration first; serialized field and
                // environment remain as fallbacks for editor/headless use.
                string configured = SpatialApiConfig.Instance.ApiKey;
                if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
                if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();
                string env = Environment.GetEnvironmentVariable("META_API_KEY");
                if (string.IsNullOrWhiteSpace(env)) env = Environment.GetEnvironmentVariable("SAM_API_KEY");
                return env != null ? env.Trim() : null;
            }
        }

        string ResolvedModel
        {
            get
            {
                string configured = SpatialApiConfig.Instance.SamModelName;
                if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
                return string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
            }
        }

        void Awake()
        {
            if (spatialMap == null) spatialMap = FindAnyObjectByType<SpatialMap>();
            if (reconciler == null) reconciler = FindAnyObjectByType<SpatialMapReconciler>();
        }

        void OnEnable()
        {
            if (polling == null) polling = StartCoroutine(PollLoop());
        }

        void OnDisable()
        {
            if (polling != null) { StopCoroutine(polling); polling = null; }
        }

        IEnumerator PollLoop()
        {
            var wait = new WaitForSeconds(Mathf.Max(0.05f, pollIntervalSeconds));
            while (true)
            {
                yield return wait;
                if (spatialMap == null) continue;
                var worker = spatialMap.SamPreprocessor;
                if (worker == null) continue;
                ReportWorkerError(worker);
                if (!ShouldStartSend(inFlight, maxConcurrentRequests))
                {
                    // Every request slot is busy: collapse the backlog so
                    // a slow network never evaluates stale frames late.
                    // Drained payloads are counted, never silent.
                    int stale = 0;
                    SpatialKeyframePayload dropped;
                    while (worker.TryTakeProcessed(out dropped)) stale++;
                    if (stale > 0)
                    {
                        StalePayloadsDrained += stale;
                        Debug.Log("[SpatialAI SAM] Queue: network saturated, drained " +
                            stale + " stale payload(s), inFlight=" + inFlight);
                    }
                    continue;
                }
                // A slot is free: evaluate the oldest queued frame now and
                // leave the rest for the next ticks. Each payload carries
                // its own capture-time pose, so a queued frame still lifts
                // where the camera first saw the object - never where the
                // camera happens to point when the response lands.
                SpatialKeyframePayload payload;
                if (!worker.TryTakeProcessed(out payload) || payload == null) continue;
                if (!string.IsNullOrEmpty(payload.Error))
                {
                    Debug.LogWarning("[SpatialAI SAM] Preprocessor failed keyframe " +
                        payload.KeyframeId + ": " + payload.Error);
                    continue;
                }
                string concept = ConceptSchedule[conceptIndex % ConceptSchedule.Length];
                StartCoroutine(SendPayload(payload, concept));
            }
        }

        // Controlled-test hook: persists the exact JPEG bytes sent to SAM so
        // the submitted image can be pulled and eyeballed (adb pull .../sam-debug).
        void MaybeSaveDebugFrame(SpatialKeyframePayload payload, string concept)
        {
            if (!saveDebugFrame || payload == null || payload.JpegBytes == null) return;
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "sam-debug");
                System.IO.Directory.CreateDirectory(dir);
                string name = payload.KeyframeId + "_" + concept + "_" +
                    payload.DownscaledWidth + "x" + payload.DownscaledHeight + ".jpg";
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                string path = System.IO.Path.Combine(dir, name);
                System.IO.File.WriteAllBytes(path, payload.JpegBytes);
                Debug.Log("[SpatialAI SAM] DebugFrame saved: " + path + " bytes=" + payload.JpegBytes.Length);
            }
            catch (Exception e) { Debug.LogWarning("[SpatialAI SAM] DebugFrame save failed: " + e.Message); }
        }

        // Debug census of SSE event types in one response, kept pure for
        // checks. Distinguishes "model sent zero deltas" from "parser
        // ignored event types it should have read".
        public static Dictionary<string, int> CensusEvents(string responseText)
        {
            var census = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(responseText)) return census;
            var lines = responseText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line.Substring(5).Trim();
                if (data == "[DONE]") { BumpCensus(census, "[DONE]"); continue; }
                SseEvent evt = null;
                try { evt = JsonUtility.FromJson<SseEvent>(data); }
                catch { BumpCensus(census, "<unparsable>"); continue; }
                if (evt == null || string.IsNullOrEmpty(evt.type)) { BumpCensus(census, "<untyped>"); continue; }
                BumpCensus(census, evt.type);
            }
            return census;
        }

        static void BumpCensus(Dictionary<string, int> census, string key)
        {
            int n;
            census.TryGetValue(key, out n);
            census[key] = n + 1;
        }

        // Short identity of the exact bytes sent, kept pure for checks.
        // With overlapping requests in flight, req+sha chain one response
        // to one image without doubt.
        public static string Sha256Short(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "none";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString();
            }
        }

        void ReportWorkerError(SpatialSamPreprocessorWorker worker)
        {
            string error = worker.LastError;
            if (!string.IsNullOrEmpty(error) && error != lastWorkerError)
            {
                lastWorkerError = error;
                Debug.LogWarning("[SpatialAI SAM] Preprocessor worker error: " + error);
            }
        }

        [Serializable] class RequestBody
        {
            public string model;
            public List<RequestInput> input;
            public bool stream;
            public RequestMetadata metadata;
        }
        [Serializable] class RequestInput
        {
            public string type = "message";
            public string role = "user";
            public List<RequestContent> content;
        }
        [Serializable] class RequestContent
        {
            public string type;
            public string text;
            public string image_url;
        }
        [Serializable] class RequestMetadata
        {
            // Documented as strings: any other JSON type returns 400.
            public string include_confidence = "true";
        }
        [Serializable] class SseEvent
        {
            public string type;
            public string delta;
        }
        [Serializable] class ResponseBody
        {
            public List<ResponseOutput> output;
            public string output_text;
        }
        [Serializable] class ResponseOutput
        {
            public List<ResponseContent> content;
        }
        [Serializable] class ResponseContent
        {
            public string type;
            public string text;
        }

        IEnumerator SendPayload(SpatialKeyframePayload payload, string concept)
        {
            // Runs to the first yield synchronously under StartCoroutine,
            // so the schedule rotation below stays atomic on the main
            // thread even with overlapping requests in flight.
            inFlight++;
            try
            {
                string key = ResolvedApiKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    PayloadsSkippedNoKey++;
                    if (!warnedNoKey)
                    {
                        warnedNoKey = true;
                        Debug.LogWarning("[SpatialAI SAM] No API key: set SpatialSamClient.apiKey or the META_API_KEY environment variable. Payloads are skipped, SLAM is unaffected.");
                    }
                    yield break;
                }
                if (payload == null || payload.JpegBytes == null || payload.JpegBytes.Length == 0)
                {
                    LastError = "Payload has no JPEG data";
                    yield break;
                }

                var body = new RequestBody
                {
                    model = ResolvedModel,
                    stream = true,
                    metadata = new RequestMetadata(),
                    input = new List<RequestInput>
                    {
                        new RequestInput
                        {
                            content = new List<RequestContent>
                            {
                                new RequestContent { type = "input_text", text = concept },
                                new RequestContent
                                {
                                    type = "input_image",
                                    image_url = "data:image/jpeg;base64," + Convert.ToBase64String(payload.JpegBytes)
                                }
                            }
                        }
                    }
                };
                string json = JsonUtility.ToJson(body);
                RequestsSent++;
                // The schedule rotates per SENT request, so every concept is
                // evaluated in turn even when individual requests fail.
                conceptIndex++;
                ConceptsEvaluated++;
                string requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
                string jpegSha = Sha256Short(payload.JpegBytes);
                MaybeSaveDebugFrame(payload, concept);
                Debug.Log("[SpatialAI SAM] Request: concept='" + concept + "' keyframe=" +
                    payload.KeyframeId + " image=" + payload.DownscaledWidth + "x" +
                    payload.DownscaledHeight + " jpeg=" + (payload.JpegBytes.Length / 1024) + "KB" +
                    " model=" + ResolvedModel + " endpoint=" + Endpoint +
                    " req=" + requestId + " sha=" + jpegSha +
                    " shape={model:string,input:[{type:message,role:user,content:[{type:input_text},{type:input_image,data-url-jpeg-bytes=" + payload.JpegBytes.Length + "}]}],stream:bool:true,metadata:{include_confidence:string:true}}");
                using (var request = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Accept", "text/event-stream");
                    request.SetRequestHeader("Authorization", "Bearer " + key);
                    request.timeout = Mathf.Max(1, requestTimeoutSeconds);
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        RequestsFailed++;
                        LastError = "HTTP " + request.responseCode + ": " + request.error;
                        Debug.LogWarning("[SpatialAI SAM] Request failed: concept='" + concept +
                            "' keyframe=" + payload.KeyframeId + ": " + LastError);
                        yield break;
                    }
                    Debug.Log("[SpatialAI SAM] HTTP: concept='" + concept + "' keyframe=" +
                        payload.KeyframeId + " status=" + request.responseCode +
                        " sseBytes=" + request.downloadHandler.text.Length +
                        " req=" + requestId + " sha=" + jpegSha);
                    HandleResponse(payload, concept, request.downloadHandler.text, requestId, jpegSha);
                }
            }
            finally { inFlight = Mathf.Max(0, inFlight - 1); }
        }

        // Accumulates response.output_text.delta lanes from the SSE stream.
        // A non-streaming same-format body (output_text / output content) is
        // accepted as a fallback; anything else fails loudly.
        static bool TryExtractOutputText(string responseText, out string outputText,
            out int deltaEvents, out bool sawCompleted)
        {
            outputText = "";
            deltaEvents = 0;
            sawCompleted = false;
            if (string.IsNullOrEmpty(responseText)) return false;
            var builder = new StringBuilder();
            bool sawStream = false;
            var lines = responseText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                sawStream = true;
                string data = line.Substring(5).Trim();
                if (data == "[DONE]") { sawCompleted = true; continue; }
                SseEvent evt = null;
                try { evt = JsonUtility.FromJson<SseEvent>(data); }
                catch { continue; }
                if (evt == null || string.IsNullOrEmpty(evt.type)) continue;
                if (evt.type == "response.completed") { sawCompleted = true; continue; }
                if (evt.type == "response.output_text.delta" && !string.IsNullOrEmpty(evt.delta))
                {
                    deltaEvents++;
                    builder.Append(evt.delta);
                }
            }
            if (sawStream)
            {
                outputText = builder.ToString();
                return true;
            }
            // Non-SSE fallback: same-format complete response.
            try
            {
                var body = JsonUtility.FromJson<ResponseBody>(responseText);
                if (body != null)
                {
                    if (!string.IsNullOrWhiteSpace(body.output_text))
                    {
                        outputText = body.output_text;
                        return true;
                    }
                    if (body.output != null)
                    {
                        foreach (var output in body.output)
                        {
                            if (output == null || output.content == null) continue;
                            foreach (var content in output.content)
                                if (content != null && !string.IsNullOrWhiteSpace(content.text))
                                    builder.Append(content.text);
                        }
                        if (builder.Length > 0)
                        {
                            outputText = builder.ToString();
                            return true;
                        }
                    }
                }
            }
            catch { /* Fall through to failure. */ }
            return false;
        }

        // Depth waterfall, second tier: intersect the capture ray through the
        // mask centroid with ARCore planes. Real plane geometry, never
        // estimated depth: smooth furniture carries no ORB landmarks but
        // still stands on a detected floor. False when no plane is hit.
        // Guard for plane-hit depth, kept pure for checks: closer than 20cm
        // is behind the lens or on it, farther than 30m is a runaway ray.
        public static bool PlaneDepthInRange(float depth) { return depth >= 0.2f && depth <= 30f; }

        int CountTrackedPlanes()
        {
            var controller = FindAnyObjectByType<SpatialAIController>();
            var manager = controller != null && controller.Origin != null
                ? controller.Origin.GetComponent<ARPlaneManager>() : null;
            if (manager == null || manager.trackables == null) return 0;
            return manager.trackables.count;
        }

        bool TryPlaneDepth(SemanticDetection2D detection, SpatialKeyframePayload payload,
            out Vector3 mapPosition, out Vector3 extent, out float depth, out string planeId, out bool usedFreshMap)
        {
            mapPosition = Vector3.zero; extent = Vector3.zero; depth = 0f; planeId = ""; usedFreshMap = false;
            Vector3 originMap, dirMap;
            if (!SpatialSemanticLifter.MapCentroidRay(detection, payload, out originMap, out dirMap))
                return false;
            var controller = FindAnyObjectByType<SpatialAIController>();
            var session = controller != null ? controller.Session : null;
            if (controller == null || session == null || controller.Origin == null) return false;
            Vector3 originWorld, farWorld;
            if (!(session.TryMapToWorld(originMap, out originWorld)
                && session.TryMapToWorld(originMap + dirMap, out farWorld)))
            {
                // Fresh capture, never localized: the map frame IS the
                // session frame (captureFromWorld is identity), so the map
                // ray is already a world ray. Exact, not approximate.
                originWorld = originMap; farWorld = originMap + dirMap; usedFreshMap = true;
            }
            if (planeRaycaster == null)
            {
                planeRaycaster = controller.Origin.GetComponent<ARRaycastManager>();
                if (planeRaycaster == null) planeRaycaster = controller.Origin.gameObject.AddComponent<ARRaycastManager>();
            }
            planeHits.Clear();
            Vector3 castDir = farWorld - originWorld;
            if (castDir.sqrMagnitude < 1e-8f) return false;
            if (!planeRaycaster.Raycast(new Ray(originWorld, castDir.normalized), planeHits, TrackableType.PlaneWithinPolygon))
                return false;
            var hit = planeHits[0];
            Vector3 hitMap;
            if (usedFreshMap) hitMap = hit.pose.position;
            else if (!session.TryWorldToMap(hit.pose.position, out hitMap)) return false;
            depth = Vector3.Dot(hitMap - originMap, dirMap);
            if (!PlaneDepthInRange(depth)) return false;
            Vector2 center; float pixelWidth, pixelHeight;
            SpatialSemanticLifter.MaskCentroidAndSize(detection, out center, out pixelWidth, out pixelHeight);
            var k = payload.DownscaledIntrinsics;
            mapPosition = SpatialSemanticLifter.UnprojectToMap(center, depth, k, payload.MetricCameraPose);
            extent = SpatialSemanticLifter.BoxExtent(pixelWidth, pixelHeight, depth, k);
            planeId = hit.trackableId.ToString();
            return true;
        }

        void HandleResponse(SpatialKeyframePayload payload, string concept, string responseText, string requestId, string jpegSha)
        {
            string outputText;
            int deltaEvents;
            bool sawCompleted;
            if (!TryExtractOutputText(responseText, out outputText, out deltaEvents, out sawCompleted))
            {
                RequestsFailed++;
                LastError = "Could not extract output_text from SAM response";
                Debug.LogWarning("[SpatialAI SAM] Unparseable response: concept='" + concept +
                    "' keyframe=" + payload.KeyframeId + " req=" + requestId + " sha=" + jpegSha);
                return;
            }
            if (string.IsNullOrWhiteSpace(outputText))
            {
                // Documented normal outcome: the concept is not present.
                EmptyResponses++;
                var census = CensusEvents(responseText);
                var censusText = new StringBuilder();
                foreach (var kv in census)
                {
                    if (censusText.Length > 0) censusText.Append(",");
                    censusText.Append(kv.Key).Append("=").Append(kv.Value);
                }
                Debug.Log("[SpatialAI SAM] Tokens: concept='" + concept + "' keyframe=" +
                    payload.KeyframeId + " deltas=" + deltaEvents +
                    " completed=" + sawCompleted + " empty (zero matches, normal)" +
                    " req=" + requestId + " sha=" + jpegSha);
                Debug.Log("[SpatialAI SAM] EmptyDetail: concept='" + concept + "' keyframe=" +
                    payload.KeyframeId + " sseBytes=" + (responseText != null ? responseText.Length : 0) +
                    " events={" + censusText + "}" +
                    " req=" + requestId + " sha=" + jpegSha);
                if (!string.IsNullOrEmpty(responseText) && responseText.Length < 8192)
                    Debug.Log("[SpatialAI SAM] RawResponse: concept='" + concept + "' keyframe=" +
                        payload.KeyframeId + " req=" + requestId + " sha=" + jpegSha + " text=" + responseText);
                return;
            }
            Debug.Log("[SpatialAI SAM] Tokens: concept='" + concept + "' keyframe=" +
                payload.KeyframeId + " deltas=" + deltaEvents + " chars=" + outputText.Length +
                " completed=" + sawCompleted + " req=" + requestId + " sha=" + jpegSha);
            List<SamRecord> records;
            try
            {
                records = SamLineParser.ParseOutputText(outputText);
            }
            catch (SamMaskCodecException e)
            {
                RequestsFailed++;
                LastError = "SAM grammar error: " + e.Message;
                Debug.LogWarning("[SpatialAI SAM] Grammar error: concept='" + concept +
                    "' keyframe=" + payload.KeyframeId + " req=" + requestId + " sha=" + jpegSha + ": " + e.Message);
                return;
            }
            var landmarks = spatialMap.Data != null ? spatialMap.Data.landmarks : null;
            int decoded = 0, registered = 0, updated = 0;
            // Per-response drops: the summary below must describe THIS
            // response, not lifetime totals.
            int dropDepth = 0, dropMask = 0, failDecode = 0;
            foreach (var record in records)
            {
                if (record.confidence >= 0f && record.confidence < minimumConfidence)
                    continue;
                byte[] raster;
                try
                {
                    raster = SamMaskCodec.DecodeToRaster(record);
                }
                catch (SamMaskCodecException e)
                {
                    MasksFailedDecode++;
                    failDecode++;
                    Debug.LogWarning("[SpatialAI SAM] Mask decode failed: concept='" + concept +
                        "' keyframe=" + payload.KeyframeId + " id=" + record.objectId +
                        " req=" + requestId + " sha=" + jpegSha + ": " + e.Message);
                    continue;
                }
                decoded++;
                // Coherence gate: a raster that is empty or a sliver inside
                // its own box carries no segmented object. Reject loudly
                // instead of lifting bare box geometry (shipped wireframes
                // on wrong objects and thin air).
                float maskFill;
                if (!SpatialDetectionSanity.MaskFillSane(raster, record.maskW, record.maskH, out maskFill))
                {
                    MasksRejectedBadMask++;
                    dropMask++;
                    Debug.LogWarning("[SAM Mask Warning] Dropped " + concept +
                        ": mask fill " + maskFill.ToString("F3") + " over " + record.maskW + "x" + record.maskH +
                        " box (keyframe " + payload.KeyframeId + " id=" + record.objectId + " req=" + requestId + " sha=" + jpegSha + "). Box carries no segmented object.");
                    continue;
                }
                // Box corners are downscaled-image source pixels (top-left
                // origin); the mask raster covers the box rect.
                var box = new Rect(
                    record.x1, record.y1,
                    Mathf.Max(1f, record.x2 - record.x1),
                    Mathf.Max(1f, record.y2 - record.y1));
                var detection = new SemanticDetection2D
                {
                    label = concept,
                    confidence = record.confidence,
                    pixelBox = box,
                    maskRaster = raster,
                    maskW = record.maskW,
                    maskH = record.maskH,
                    samObjectId = record.objectId
                };
                Vector3 position, extent; float usedDepth;
                string depthSource = "landmark";
                if (!SpatialSemanticLifter.TryLift(detection, payload, landmarks,
                        out position, out extent, out usedDepth))
                {
                    string planeId; bool usedFreshMap;
                    if (TryPlaneDepth(detection, payload, out position, out extent, out usedDepth, out planeId, out usedFreshMap))
                    {
                        depthSource = "plane";
                        Debug.Log("[SpatialAI SAM] PlaneDepth " + concept + ": depth " + usedDepth.ToString("F2") +
                            "m plane=" + planeId + (usedFreshMap ? " freshMap" : "") + " (keyframe " + payload.KeyframeId + " id=" + record.objectId +
                            " req=" + requestId + " sha=" + jpegSha + ")");
                    }
                    else
                    {
                        MasksRejectedNoLandmarks++;
                        dropDepth++;
                        Debug.Log("[SpatialAI SAM] PlaneMiss " + concept +
                            ": no plane along centroid ray (planesTracked=" + CountTrackedPlanes() +
                            " keyframe " + payload.KeyframeId + " id=" + record.objectId +
                            " req=" + requestId + " sha=" + jpegSha + ")");
                        Debug.LogWarning("[SAM Depth Warning] Dropped " + concept +
                            ": 0 landmarks inside mask (keyframe " + payload.KeyframeId +
                            " id=" + record.objectId + " req=" + requestId + " sha=" + jpegSha + "). Sparse SLAM tracking is the bottleneck - no depth invented.");
                        continue;
                    }
                }
                var candidate = new SemanticEntity3D
                {
                    label = concept,
                    confidence = record.confidence >= 0f ? Mathf.Clamp01(record.confidence) : 1f,
                    position = position,
                    extent = extent,
                    rotation = SpatialSemanticLifter.MapRotation(payload.MetricCameraPose),
                    depthSource = depthSource,
                    samObjectId = record.objectId,
                    discoveryTimestamp = payload.Timestamp,
                    discoveredAtUtc = DateTime.UtcNow.ToString("O")
                };
                bool created;
                // Phase 4: the reconciler owns lifecycle changes (Moved /
                // NewObstruction / Static) when one is in the scene; the
                // direct register path stays as the fallback.
                var stored = reconciler != null
                    ? reconciler.ReconcileDetection(candidate, payload.KeyframeId, out created)
                    : spatialMap.RegisterSemanticEntity(candidate, payload.KeyframeId,
                        clusterRadiusMeters, out created);
                if (stored == null) continue;
                if (created)
                {
                    registered++;
                    EntitiesDiscovered++;
                    Debug.Log("[SpatialAI SAM] Discovered " + stored.label + " at " +
                        stored.position.ToString("F2") + " extent " + stored.extent.ToString("F2") +
                        " box " + (record.x2 - record.x1) + "x" + (record.y2 - record.y1) + "px" +
                        " fill " + maskFill.ToString("F3") +
                        " confidence " + stored.confidence.ToString("F2") +
                        " depth " + usedDepth.ToString("F2") + "m samId " + record.objectId +
                        " id " + stored.entityId + " keyframe " + payload.KeyframeId +
                        " source=" + stored.depthSource +
                        " req=" + requestId + " sha=" + jpegSha);
                }
                else
                {
                    updated++;
                    Debug.Log("[SpatialAI SAM] Updated " + stored.label + " at " +
                        stored.position.ToString("F2") + " observations " + stored.observationCount +
                        " samId " + record.objectId + " id " + stored.entityId +
                        " req=" + requestId + " sha=" + jpegSha);
                }
            }
            Debug.Log("[SpatialAI SAM] Decode: concept='" + concept + "' keyframe=" +
                payload.KeyframeId + " records=" + records.Count + " decoded=" + decoded +
                " registered=" + registered + " updated=" + updated +
                " droppedNoDepth=" + dropDepth +
                " droppedBadMask=" + dropMask +
                " failedDecode=" + failDecode +
                " req=" + requestId + " sha=" + jpegSha);
        }
    }
}
