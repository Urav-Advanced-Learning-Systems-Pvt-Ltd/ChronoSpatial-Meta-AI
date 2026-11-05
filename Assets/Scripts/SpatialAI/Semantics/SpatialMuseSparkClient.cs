using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SpatialAI
{
    // Structured result of one Muse Spark spatial reasoning query.
    [Serializable]
    public class SpatialNavigationAdvice
    {
        public string nextAction;   // e.g. "Turn Left", "Straight", "Arrived".
        public string speech;       // Natural language sentence for SpatialSpeech.
        public List<string> hazards = new List<string>();
        public string destinationQuery;
        public int nearbyEntityCount;
    }

    [Serializable]
    public class SpatialEntityContext
    {
        public string label;
        public float distanceMeters;
        public float bearingDegrees; // 0 = ahead, negative = left, positive = right.
        public float confidence;
        public Vector3 position;
        // Phase 4 dynamic state, mirrored from SemanticEntity3D.
        public string status;          // SemanticEntityStatus name, e.g. "Moved".
        public float displacementMeters;
        public Vector3 previousPosition;
    }

    // Muse Spark spatial reasoning client. Given the camera pose and a
    // destination query, it gathers SemanticEntity3D objects within 10 m,
    // asks meta-models/muse-spark for structured navigation JSON, speaks the
    // returned sentence through SpatialSpeech, and surfaces hazards.
    // Requests are coroutine UnityWebRequest: the game thread never blocks.
    public class SpatialMuseSparkClient : MonoBehaviour
    {
        public const float ContextRadiusMeters = 10f;

        [Header("References")]
        [SerializeField] SpatialMap spatialMap;
        [SerializeField] SpatialSpeech speech;
        [SerializeField] Transform cameraTransform;

        [Header("Reasoning")]
        [SerializeField] int requestTimeoutSeconds = 45;

        [Header("Phase 4 Dynamic Route Awareness")]
        [Tooltip("Horizontal distance (metres) from the active route that counts as blocking it.")]
        [SerializeField] float routeCorridorMeters = 0.9f;

        public bool Busy { get; private set; }
        public string LastError { get; private set; }
        public SpatialNavigationAdvice LastAdvice { get; private set; }

        public event Action<SpatialNavigationAdvice> AdviceReceived;
        public event Action<string> QueryFailed;

        [Serializable] class RequestBody
        {
            public string model;
            public List<SamRequestInput> input;
        }
        [Serializable] class SamRequestInput
        {
            public string role = "user";
            public List<SamRequestContent> content;
        }
        [Serializable] class SamRequestContent
        {
            public string type;
            public string text;
        }
        [Serializable] class ResponseBody
        {
            public List<SamResponseOutput> output;
            public string output_text;
        }
        [Serializable] class SamResponseOutput
        {
            public List<SamResponseContent> content;
        }
        [Serializable] class SamResponseContent
        {
            public string type;
            public string text;
        }
        [Serializable] class AdviceJson
        {
            public string nextAction;
            public string speech;
            public List<string> hazards;
        }

        void Awake()
        {
            if (spatialMap == null) spatialMap = FindAnyObjectByType<SpatialMap>();
            if (speech == null) speech = FindAnyObjectByType<SpatialSpeech>();
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        }

        // Entities within ContextRadiusMeters of the camera, with bearing
        // relative to camera forward. Pure math; safe to call per query.
        public List<SpatialEntityContext> GatherContext(Vector3 cameraPosition, Vector3 cameraForward)
        {
            var result = new List<SpatialEntityContext>();
            if (spatialMap == null) return result;
            Vector3 flatForward = Vector3.ProjectOnPlane(cameraForward, Vector3.up);
            if (flatForward.sqrMagnitude < 0.0001f) flatForward = Vector3.forward;
            flatForward.Normalize();
            foreach (var entity in spatialMap.SemanticEntitiesSnapshot())
            {
                if (entity == null) continue;
                Vector3 to = entity.position - cameraPosition;
                float distance = to.magnitude;
                if (distance > ContextRadiusMeters) continue;
                Vector3 flatTo = Vector3.ProjectOnPlane(to, Vector3.up);
                float bearing = flatTo.sqrMagnitude < 0.0001f
                    ? 0f : Vector3.SignedAngle(flatForward, flatTo.normalized, Vector3.up);
                result.Add(new SpatialEntityContext
                {
                    label = entity.label,
                    distanceMeters = distance,
                    bearingDegrees = bearing,
                    confidence = entity.confidence,
                    position = entity.position,
                    status = entity.Status.ToString(),
                    displacementMeters = entity.DisplacementMeters,
                    previousPosition = entity.PreviousPosition
                });
            }
            result.Sort((a, b) => a.distanceMeters.CompareTo(b.distanceMeters));
            return result;
        }

        string BuildPrompt(string destinationQuery, Vector3 cameraPosition,
            Vector3 cameraForward, List<SpatialEntityContext> context)
        {
            var builder = new StringBuilder();
            builder.Append("You are a spatial navigation guide for a visually impaired user. ");
            builder.Append("Camera position (metres): ").Append(cameraPosition.ToString("F2"))
                .Append(", facing direction: ").Append(cameraForward.ToString("F2")).Append(". ");
            builder.Append("Destination query: \"").Append(destinationQuery).Append("\". ");
            builder.Append("Nearby semantic objects within 10 metres (bearing 0 = straight ahead, negative = left, positive = right): ");
            if (context.Count == 0) builder.Append("none detected. ");
            foreach (var item in context)
            {
                builder.Append(item.label).Append(" at ").Append(item.distanceMeters.ToString("F1"))
                    .Append("m, bearing ").Append(item.bearingDegrees.ToString("F0")).Append(" degrees; ");
            }
            // Phase 4: call out every dynamic change explicitly so the model
            // warns about relocated furniture and new on-route blockages
            // instead of treating the map as static.
            var dynamicHazards = DynamicHazards(context);
            if (dynamicHazards.Count > 0)
            {
                builder.Append("DYNAMIC CHANGES since the map was recorded (the physical world no longer matches the stored map): ");
                foreach (var hazard in dynamicHazards) builder.Append(hazard).Append("; ");
                builder.Append("Your reply MUST warn the user about these changes in \"speech\" and list each one in \"hazards\". ");
                builder.Append("If any change blocks the direct path, say so plainly and suggest stopping or rerouting. ");
            }
            builder.Append("Return ONLY JSON, no prose: {\"nextAction\": one of " +
                "\"Turn Left\", \"Turn Right\", \"Straight\", \"Turn Around\", \"Stop\", \"Arrived\", " +
                "\"speech\": one short natural spoken sentence, \"hazards\": [safety hazards on the path]}.");
            return builder.ToString();
        }

        // Human-readable Phase 4 changes inside the context, nearest first
        // (GatherContext already sorts by distance).
        public static List<string> DynamicHazards(List<SpatialEntityContext> context)
        {
            var hazards = new List<string>();
            if (context == null) return hazards;
            foreach (var item in context)
            {
                if (item == null) continue;
                if (item.status == nameof(SemanticEntityStatus.NewObstruction))
                    hazards.Add("NEW OBSTRUCTION: " + item.label + " is blocking the route " +
                        item.distanceMeters.ToString("F1") + "m away");
                else if (item.status == nameof(SemanticEntityStatus.Moved))
                    hazards.Add("MOVED: " + item.label + " has moved " +
                        item.displacementMeters.ToString("F1") + "m from its mapped position, now " +
                        item.distanceMeters.ToString("F1") + "m away");
            }
            return hazards;
        }

        // True when a dynamic change sits in the user's direct path: any
        // NewObstruction nearby, or a Moved entity now inside the active
        // route corridor. Alert names the nearest offender.
        public bool IsPathCompromised(List<SpatialEntityContext> context, out string alert)
        {
            alert = null;
            if (context == null) return false;
            var route = spatialMap != null && spatialMap.Data != null ? spatialMap.Data.route : null;
            SpatialEntityContext worst = null;
            foreach (var item in context)
            {
                if (item == null) continue;
                bool blocks = item.status == nameof(SemanticEntityStatus.NewObstruction)
                    || (item.status == nameof(SemanticEntityStatus.Moved) && route != null
                        && SpatialMapReconciler.DistanceToRoute(item.position, route) <= routeCorridorMeters);
                if (blocks && (worst == null || item.distanceMeters < worst.distanceMeters))
                    worst = item;
            }
            if (worst == null) return false;
            alert = worst.status == nameof(SemanticEntityStatus.NewObstruction)
                ? "Warning. Your path is blocked by a " + worst.label + " about " +
                    Mathf.Max(1, Mathf.RoundToInt(worst.distanceMeters)) + " metres away. Please stop."
                : "Warning. A " + worst.label + " has moved into your path about " +
                    Mathf.Max(1, Mathf.RoundToInt(worst.distanceMeters)) + " metres away. Please stop.";
            return true;
        }

        // Main entry point. Callback and AdviceReceived fire on completion;
        // QueryFailed fires with a reason when no advice could be produced.
        public void Ask(string destinationQuery, Action<SpatialNavigationAdvice> onAdvice = null)
        {
            if (Busy) { if (QueryFailed != null) QueryFailed.Invoke("Reasoning query already in progress"); return; }
            if (string.IsNullOrWhiteSpace(destinationQuery))
            {
                if (QueryFailed != null) QueryFailed.Invoke("Destination query is empty");
                return;
            }
            // Speech is created at runtime by SpatialNavigationController,
            // possibly after this component's Awake: resolve it lazily too.
            if (speech == null) speech = FindAnyObjectByType<SpatialSpeech>();
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform == null)
            {
                if (QueryFailed != null) QueryFailed.Invoke("No camera available for spatial reasoning");
                return;
            }
            StartCoroutine(Query(destinationQuery.Trim(), onAdvice));
        }

        IEnumerator Query(string destinationQuery, Action<SpatialNavigationAdvice> onAdvice)
        {
            Busy = true;
            try
            {
                var config = SpatialApiConfig.Instance;
                string key = config.ApiKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    string env = Environment.GetEnvironmentVariable("META_API_KEY");
                    key = env != null ? env.Trim() : null;
                }
                if (string.IsNullOrWhiteSpace(key))
                {
                    LastError = "No API key configured";
                    if (QueryFailed != null) QueryFailed.Invoke(LastError);
                    Debug.LogWarning("[SpatialAI Muse] No API key: open Settings (gear) or set META_API_KEY.");
                    yield break;
                }
                Vector3 cameraPosition = cameraTransform.position;
                Vector3 cameraForward = cameraTransform.forward;
                var context = GatherContext(cameraPosition, cameraForward);
                // Phase 4: never let network latency delay a safety alert.
                // A compromised direct path is spoken locally at once; the
                // model still receives the changes in the prompt below.
                string compromiseAlert;
                bool pathCompromised = IsPathCompromised(context, out compromiseAlert);
                if (pathCompromised)
                {
                    Debug.LogWarning("[SpatialAI Muse] Dynamic route alert: " + compromiseAlert);
                    if (speech != null) speech.Speak(compromiseAlert);
                }
                var body = new RequestBody
                {
                    model = config.ModelName,
                    input = new List<SamRequestInput>
                    {
                        new SamRequestInput
                        {
                            content = new List<SamRequestContent>
                            {
                                new SamRequestContent
                                {
                                    type = "input_text",
                                    text = BuildPrompt(destinationQuery, cameraPosition, cameraForward, context)
                                }
                            }
                        }
                    }
                };
                string json = JsonUtility.ToJson(body);
                using (var request = new UnityWebRequest(config.ResponsesEndpoint, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Authorization", "Bearer " + key);
                    request.timeout = Mathf.Max(1, requestTimeoutSeconds);
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        LastError = "HTTP " + request.responseCode + ": " + request.error;
                        if (QueryFailed != null) QueryFailed.Invoke(LastError);
                        Debug.LogWarning("[SpatialAI Muse] Query failed: " + LastError);
                        yield break;
                    }
                    SpatialNavigationAdvice advice;
                    if (!TryParseAdvice(request.downloadHandler.text, destinationQuery, context.Count, out advice))
                    {
                        LastError = "Could not parse Muse Spark response";
                        if (QueryFailed != null) QueryFailed.Invoke(LastError);
                        yield break;
                    }
                    // Keep Phase 4 hazards even if the model omits them.
                    foreach (var hazard in DynamicHazards(context))
                        if (!advice.hazards.Contains(hazard)) advice.hazards.Add(hazard);
                    if (pathCompromised && !advice.hazards.Contains(compromiseAlert))
                        advice.hazards.Add(compromiseAlert);
                    LastAdvice = advice;
                    Debug.Log("[SpatialAI Muse] Action: " + advice.nextAction + " | " + advice.speech +
                        (advice.hazards.Count > 0 ? " | Hazards: " + string.Join(", ", advice.hazards) : ""));
                    if (speech != null && !string.IsNullOrWhiteSpace(advice.speech)) speech.Speak(advice.speech);
                    if (AdviceReceived != null) AdviceReceived.Invoke(advice);
                    if (onAdvice != null) onAdvice.Invoke(advice);
                }
            }
            finally { Busy = false; }
        }

        static bool TryParseAdvice(string responseText, string destinationQuery,
            int nearbyCount, out SpatialNavigationAdvice advice)
        {
            advice = null;
            if (string.IsNullOrWhiteSpace(responseText)) return false;
            string text = responseText;
            try
            {
                var body = JsonUtility.FromJson<ResponseBody>(responseText);
                if (body != null)
                {
                    if (!string.IsNullOrWhiteSpace(body.output_text)) text = body.output_text;
                    else if (body.output != null)
                    {
                        var builder = new StringBuilder();
                        foreach (var output in body.output)
                        {
                            if (output == null || output.content == null) continue;
                            foreach (var content in output.content)
                                if (content != null && !string.IsNullOrWhiteSpace(content.text))
                                    builder.Append(content.text);
                        }
                        if (builder.Length > 0) text = builder.ToString();
                    }
                }
            }
            catch { /* Treat raw body as advice JSON. */ }

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return false;
            try
            {
                var parsed = JsonUtility.FromJson<AdviceJson>(text.Substring(start, end - start + 1));
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.nextAction)) return false;
                advice = new SpatialNavigationAdvice
                {
                    nextAction = parsed.nextAction.Trim(),
                    speech = parsed.speech != null ? parsed.speech.Trim() : "",
                    hazards = parsed.hazards ?? new List<string>(),
                    destinationQuery = destinationQuery,
                    nearbyEntityCount = nearbyCount
                };
                return true;
            }
            catch { return false; }
        }
    }
}
