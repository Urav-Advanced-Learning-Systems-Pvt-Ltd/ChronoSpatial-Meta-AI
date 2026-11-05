using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SpatialAI
{
    public partial class SpatialMap
    {
        public const int MappingPartnerWindow = 32;
        public int MapEpoch { get; private set; }
        public int TrackingSegment { get; private set; }
        public int CrossSegmentMatchCount { get; private set; }
        public int PendingMappingJobs => MappingIdle ? 0 : 1;
        public bool MappingIdle => applying == null && pendingFrame == null && !(mappingWorker?.Busy ?? false);
        public double LastWorkerMs { get; private set; }
        public double LastApplyMs { get; private set; }
        public double LastEnqueueMs { get; private set; }
        public double MaximumApplyMs { get; private set; }
        public int ApplyBudgetOverruns { get; private set; }
        public int LastMappingPartnerCount { get; private set; }
        public string LastMappingError { get; private set; }
        public event Action<string> MappingCompleted;

        SpatialMappingWorker mappingWorker;
        SpatialSamPreprocessorWorker samWorker;
        public SpatialSamPreprocessorWorker SamPreprocessor { get { return samWorker; } }
        public int SamPendingCount { get { return samWorker != null ? samWorker.PendingCount : 0; } }

        // Accepted-keyframe sidecar hook. Raw array handoff only: payload
        // creation is field copies plus intrinsics scale math. No encoding,
        // no network, and no queue wait ever runs on the Unity thread.
        void OfferSamPayload(SpatialKeyframe keyframe, Color32[] pixels)
        {
            try
            {
                if (pixels == null) return;
                if (samWorker == null) samWorker = new SpatialSamPreprocessorWorker();
                samWorker.TryOffer(SpatialKeyframePayload.Create(keyframe, pixels));
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[SpatialAI SAM] payload offer failed: " + e.Message); }
        }

        SpatialFrame pendingFrame;
        SpatialCaptureGate pendingGate;
        int pendingSegment;
        IEnumerator<int> applying;
        readonly Dictionary<string, int> trackingSegments = new Dictionary<string, int>();

        public static bool IsNormalSkip(string reason) => reason == "movement below thresholds" || reason == "keyframe interval not elapsed";
        static string FormatCaptureDecision(string reason) => (IsNormalSkip(reason) ? "Skipped: " : "Rejected: ") + reason;
        public void NotifyTrackingRecovered() { TrackingSegment++; }
        public void ClearCaptureHint() { LastKeyframeDecision = "No candidate evaluated"; }

        void Update() { ApplyMappingResults(3); }
        void OnDisable() { CancelMappingWork(); }
        void CancelAsyncMapping()
        {
            MapEpoch++;
            mappingWorker?.Cancel();
            applying?.Dispose(); applying = null;
            pendingFrame = null; pendingGate = null;
        }

        // Texture and frame metadata are read only here, on Unity's thread.
        // One slot remains occupied until extraction, matching AND ordered application complete.
        public bool QueueVisualFrame(SpatialFrame frame) => QueueVisualFrameAt(frame, Time.realtimeSinceStartupAsDouble);

        bool QueueVisualFrameAt(SpatialFrame frame, double now)
        {
            VisualFrameAttempts++;
            if (!MappingIdle) { LastKeyframeDecision = "Deferred: mapping busy"; return false; }
            if (LastMappingError != null) { LastKeyframeDecision = "Rejected: mapping failed; start a new scan"; return false; }
            if (mappingCancellation?.IsCancellationRequested ?? false) ResetMappingWork();
            if (frame == null || !frame.hasImage || frame.image == null)
            { LastKeyframeDecision = "Rejected: image unavailable"; return false; }
            if (Data.keyframes.Count >= MaximumKeyframeCount)
            { LastKeyframeDecision = "Rejected: maximum keyframe count reached"; return false; }
            VisualGateEvaluations++;
            var gate = captureGate.Copy();
            if (!gate.AcceptVisual(frame, now, MaximumKeyframeCount,
                MinimumKeyframeInterval, MinimumKeyframeTranslation, MinimumKeyframeRotation, out var reason))
            { LastKeyframeDecision = FormatCaptureDecision(reason); return false; }
            var clock = Stopwatch.StartNew();
            var request = new SpatialMappingWorker.Request { epoch = MapEpoch, width = frame.image.width,
                descriptorSchema = SpatialFeatureSchemas.ForMap(Data) ?? SpatialFeatureSchemas.NewScan,
                height = frame.image.height, pixels = frame.image.GetPixels32(), references = new List<SpatialKeyframe>() };
            // Only descriptors and identity cross to the worker, never mutable observations or Unity objects.
            for (var i = Data.keyframes.Count - 1; i >= 0 && request.references.Count < MappingPartnerWindow; i--)
            {
                var reference = Data.keyframes[i];
                if (reference.features == null || reference.features.Count == 0) continue;
                request.references.Add(new SpatialKeyframe { keyframeId = reference.keyframeId,
                    features = reference.features.ConvertAll(SpatialLandmarkObservationIndex.CopyFeature) });
            }
            if (mappingWorker == null) mappingWorker = new SpatialMappingWorker();
            if (!mappingWorker.TryEnqueue(request)) { LastKeyframeDecision = "Deferred: mapping busy"; return false; }
            pendingFrame = new SpatialFrame { arPose = frame.arPose, imagePose = frame.imagePose, hasImagePose = frame.hasImagePose,
                intrinsics = frame.intrinsics, source = frame.source, imageTimestamp = frame.imageTimestamp,
                receivedTimestamp = frame.receivedTimestamp, clockDomain = frame.clockDomain, trackingValid = frame.trackingValid };
            pendingGate = gate;
            pendingSegment = TrackingSegment;
            LastEnqueueMs = clock.Elapsed.TotalMilliseconds;
            LastKeyframeDecision = "Queued visual frame";
            return true;
        }

        public void ApplyMappingResults(double budgetMs)
        {
            if (budgetMs <= 0 || double.IsNaN(budgetMs)) return;
            var clock = Stopwatch.StartNew();
            try
            {
                if (applying == null && mappingWorker != null && mappingWorker.TryTake(out var result))
                {
                    if (result.request.epoch != MapEpoch || pendingFrame == null) return;
                    if (result.error != null)
                    {
                        LastMappingError = result.error is OperationCanceledException ? "Mapping cancelled" : result.error.Message;
                        pendingFrame = null; pendingGate = null;
                        MappingCompleted?.Invoke("Rejected: " + LastMappingError); return;
                    }
                    var features = result.features;
                    // Preserve existing visual acceptance: zero features rejects, it does NOT create a pose-only frame.
                    if (features == null || features.features.Count == 0 || features.features.Count > 300
                        || features.imageWidth != result.request.width || features.imageHeight != result.request.height)
                    {
                        pendingFrame = null; pendingGate = null;
                        LastKeyframeDecision = "Rejected: image/features unavailable or dimensions mismatch";
                        MappingCompleted?.Invoke(LastKeyframeDecision); return;
                    }
                    var ids = new HashSet<string>();
                    foreach (var feature in features.features)
                        if (!BinaryFeatureMatcher.Valid(feature) || !ids.Add(feature.featureId) || feature.imagePoint.x < 0
                            || feature.imagePoint.y < 0 || feature.imagePoint.x >= features.imageWidth || feature.imagePoint.y >= features.imageHeight)
                            throw new InvalidOperationException("Invalid worker feature");
                    applying = ApplyResult(result, pendingFrame).GetEnumerator();
                }
                while (applying != null && clock.Elapsed.TotalMilliseconds < budgetMs)
                {
                    if (applying.MoveNext()) continue;
                    applying.Dispose(); applying = null; pendingFrame = null; pendingGate = null;
                    MappingCompleted?.Invoke(LastKeyframeDecision);
                }
            }
            catch (Exception e)
            {
                applying?.Dispose(); applying = null; pendingFrame = null; pendingGate = null;
                LastMappingError = e.Message;
                LastKeyframeDecision = "Rejected: mapping application failed";
                UnityEngine.Debug.LogException(e);
                MappingCompleted?.Invoke(LastKeyframeDecision);
            }
            finally
            {
                LastApplyMs = clock.Elapsed.TotalMilliseconds;
                MaximumApplyMs = Math.Max(MaximumApplyMs, LastApplyMs);
                if (LastApplyMs > budgetMs) ApplyBudgetOverruns++;
            }
        }

        IEnumerable<int> ApplyResult(SpatialMappingWorker.Result result, SpatialFrame frame)
        {
            var features = result.features;
            var keyframe = new SpatialKeyframe { keyframeId = "kf-" + Data.keyframes.Count.ToString("D5"), timestamp = frame.Timestamp,
                cameraPose = frame.ImagePose, intrinsics = frame.intrinsics, imageWidth = features.imageWidth,
                imageHeight = features.imageHeight, sourceFrame = frame.source, features = features.features };
            captureGate = pendingGate;
            Data.keyframes.Add(keyframe); Data.transientRevision++; cachedValidation = null;
            var frameSegment = pendingSegment;
            trackingSegments[keyframe.keyframeId] = frameSegment;
            matchingTicks = extensionTicks = triangulationTicks = 0;
            var started = MappingClock;
            foreach (var partner in result.partners)
            {
                var reference = Data.keyframes.Find(k => k.keyframeId == partner.keyframeId);
                if (reference == null) throw new InvalidOperationException("Mapping reference disappeared");
                if (partner.matches.Count > 0 && trackingSegments.TryGetValue(reference.keyframeId, out var segment) && segment != frameSegment)
                    CrossSegmentMatchCount++;
                foreach (var step in ApplyMatches(reference, keyframe, partner.matches)) yield return step;
            }
            LastWorkerMs = result.extractMs + result.matchMs;
            LastMappingPartnerCount = result.partners.Count;
            Data.transientRevision++; cachedValidation = null;
            LastKeyframeDecision = "Accepted visual keyframe " + keyframe.keyframeId;
            OfferSamPayload(keyframe, result.request.pixels);
            SpatialImageEvidence.KeyframePixels(Data, keyframe, result.request.pixels);
            if (!captureVisualsHidden) { UpdateTrajectory(); CreateMarker(frame.arPose.position); }
            UnityEngine.Debug.Log($"[SpatialAI MappingPerformance] build={Application.buildGUID} frame={keyframe.keyframeId} epoch={MapEpoch} segment={TrackingSegment} crossSegmentPartners={CrossSegmentMatchCount} candidatePartners={result.partners.Count} visitedPartners={result.partners.Count} extractionMs={result.extractMs:F3} matchingMs={result.matchMs:F3} trackExtensionMs={MappingMilliseconds(extensionTicks):F3} triangulationMs={MappingMilliseconds(triangulationTicks):F3} applyElapsedMs={MappingMilliseconds(MappingClock-started):F3} enqueueMs={LastEnqueueMs:F3} execution=single-worker-ordered-apply features={keyframe.features.Count} landmarks={LandmarkCount}");
        }
    }
}
