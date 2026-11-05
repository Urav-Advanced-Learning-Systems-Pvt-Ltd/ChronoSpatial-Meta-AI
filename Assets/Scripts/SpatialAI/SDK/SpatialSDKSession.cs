using UnityEngine;
using System.Linq;
using System;
using System.Collections;
using System.Threading;

namespace SpatialAI
{
    /// UI-independent session facade. It never reports localization without a valid solver result.
    public sealed class SpatialSDKSession
    {
        readonly SpatialSDKConfiguration configuration;
        SpatialMapData map;
        string mapLoadFailure;
        SpatialMap mapComponentForDiagnostics;
        SpatialTrackingState state = SpatialTrackingState.Unavailable;
        SpatialMapPose lastPose;
        public SpatialDiagnostics Diagnostics { get; private set; } = new SpatialDiagnostics();
        Matrix4x4 mapFromAr; bool alignmentValid; float lastTrackingTime;
        double lastVisualTime = double.NegativeInfinity;
        double lastQueryTimestamp = double.NegativeInfinity;
        string lastQueryClock;
        int alignmentGeneration;
        bool localizationRunning;
        CancellationTokenSource localizationCancellation;
        public bool LocalizationRunning => localizationRunning;
        public SpatialLocalizationCandidates LastCandidates { get; private set; }
        public void NotifySessionReset()
        {
            InvalidateAlignment("AR session changed; fresh visual localization required");
            // Keep the last successful verification time as history, never as valid alignment.
            lastQueryTimestamp = double.NegativeInfinity; lastQueryClock = null;
        }
        SpatialPose previousArPose;
        public double LastVisualVerificationAge => double.IsNegativeInfinity(lastVisualTime) ? double.PositiveInfinity : Time.realtimeSinceStartupAsDouble - lastVisualTime;
        public void InvalidateAlignment(string reason)
        {
            alignmentGeneration++; localizationCancellation?.Cancel();
            alignmentValid = false; lastPose.valid = false;
            state = map == null ? SpatialTrackingState.Unavailable : SpatialTrackingState.TrackingLost;
            Diagnostics.mapPoseValid = false; Diagnostics.mapAlignmentValid = false; Diagnostics.trackingState = state;
            Diagnostics.failureReason = reason;
        }
        public bool TryMapToWorld(Vector3 mapPosition, out Vector3 worldPosition)
        {
            worldPosition = default;
            if (!TryGetMapPose(out _)) return false;
            worldPosition = mapFromAr.inverse.MultiplyPoint3x4(mapPosition); return true;
        }
        public bool TryWorldToMap(Vector3 worldPosition, out Vector3 mapPosition)
        {
            mapPosition = default;
            if (!TryGetMapPose(out _)) return false;
            mapPosition = mapFromAr.MultiplyPoint3x4(worldPosition); return true;
        }
        public bool TryGetCaptureTransform(out Matrix4x4 transform)
        {
            transform = mapFromAr;
            return TryGetMapPose(out _);
        }
        public SpatialTrackingState TrackingState => state;
        public SpatialMapData LoadedMap => map;
        public SpatialMapValidationResult ValidateMap() => SpatialMapValidationResult.Validate(map);
        public SpatialSDKSession(SpatialSDKConfiguration configuration = null) { this.configuration = configuration ?? new SpatialSDKConfiguration(); }
        public void LoadMap(SpatialMapData value) { InvalidateAlignment("Map changed"); Diagnostics=new SpatialDiagnostics(); lastVisualTime=double.NegativeInfinity; lastQueryTimestamp=double.NegativeInfinity; lastQueryClock=null; var validation=SpatialMapValidationResult.Validate(value); mapLoadFailure=validation.valid?null:validation.message; map=validation.valid ? value : null; mapComponentForDiagnostics=UnityEngine.Object.FindAnyObjectByType<SpatialMap>(); state=map == null ? SpatialTrackingState.Unavailable : SpatialTrackingState.Searching; lastPose=new SpatialMapPose(); }
        public void UnloadMap() { mapLoadFailure=null; InvalidateAlignment("Map unloaded"); Diagnostics=new SpatialDiagnostics(); lastVisualTime=double.NegativeInfinity; lastQueryTimestamp=double.NegativeInfinity; lastQueryClock=null; map=null; state=SpatialTrackingState.Unavailable; lastPose=new SpatialMapPose(); }
        public void StartLocalization() { InvalidateAlignment("Visual verification in progress"); Diagnostics.localization=new SpatialLocalizationDiagnostics(); state=map == null ? SpatialTrackingState.Unavailable : SpatialTrackingState.Searching; }
        public void StopLocalization() { InvalidateAlignment("Localization stopped"); state=map == null ? SpatialTrackingState.Unavailable : SpatialTrackingState.Searching; }
        public SpatialLocalizationResult Localize(SpatialFrame frame)
        {
            SpatialLocalizationResult result = null;
            var steps = LocalizeRoutine(frame, value => result = value);
            try { while (steps.MoveNext()) { } }
            finally { (steps as IDisposable)?.Dispose(); }
            return result;
        }
        // Main-thread coroutine: extraction and Unity state stay on the Unity thread.
        // The candidate job owns copies and yields in bounded slices. Reset/load cancels it.
        public IEnumerator LocalizeRoutine(SpatialFrame frame, Action<SpatialLocalizationResult> completed)
            => LocalizePreparedRoutine(frame,null,completed);
        internal SpatialFeatureSet ExtractQuery(SpatialFrame frame)
            => (SpatialFeatureSchemas.ForMap(map)==SpatialFeatureSchemas.OrbDistinct
                ? new OrbFeatureExtractor() : configuration.FeatureExtractor).Extract(frame.image);
        internal IEnumerator LocalizePreparedRoutine(SpatialFrame frame, SpatialFeatureSet prepared, Action<SpatialLocalizationResult> completed)
        {
            if (localizationRunning)
            {
                completed(SpatialLocalizationResult.Failure(state, SpatialFailureReason.InsufficientEvidence, "A localization job is already running."));
                yield break;
            }
            InvalidateAlignment("Visual verification in progress");
            state = map == null ? SpatialTrackingState.Unavailable : SpatialTrackingState.Searching;
            localizationRunning = true; LastCandidates = null;
            var cancellation = new CancellationTokenSource(); localizationCancellation = cancellation;
            var generation = alignmentGeneration; var source = map;
            var revision = source == null ? 0 : source.transientRevision;
            IEnumerator steps = null;
            try
            {
                SpatialFeatureSet query = null; SpatialLocalizationResult early = null;
                SpatialLocalizationCandidateBuilder job = null;
                SpatialReferenceLocalizer referenceJob = null;
                try
                {
                    early = PrepareQuery(frame, prepared, out query);
                    if (early == null)
                    {
                        if (!(configuration.FeatureMatcher is BinaryFeatureMatcher matcher))
                            early = Fail(SpatialFailureReason.NoMatches, "Landmark retrieval requires the configured binary descriptor matcher.");
                        else if (SpatialFeatureSchemas.IsOrb(SpatialFeatureSchemas.ForMap(map)))
                            referenceJob = new SpatialReferenceLocalizer(SpatialLandmarkObservationIndex.Build(map), query, frame.intrinsics, matcher, configuration.PoseSolver, cancellation.Token);
                        else job = new SpatialLocalizationCandidateBuilder(SpatialLandmarkObservationIndex.Build(map), query, matcher, cancellation.Token, bypassMatchingFilters: true);
                    }
                }
                catch (Exception e) { early = Fail(SpatialFailureReason.InvalidMap, "Localization preparation failed: " + e.Message); }
                if (early != null) { completed(early); yield break; }
                steps = referenceJob != null ? referenceJob.RunSteps() : job.RunSteps();
                yield return null; // Allow cancellation/UI presentation before the bounded search starts.
                while (true)
                {
                    if (source != map || source.transientRevision != revision || generation != alignmentGeneration) cancellation.Cancel();
                    bool more = false; Exception failure = null;
                    try { more = steps.MoveNext(); }
                    catch (Exception e) { failure = e; }
                    if (failure != null)
                    {
                        var message = failure is OperationCanceledException ? "Localization cancelled: map or tracking state changed." : failure.Message;
                        // An obsolete job must not overwrite the newly loaded map/session state.
                        completed(generation == alignmentGeneration ? Fail(SpatialFailureReason.InsufficientEvidence, message)
                            : SpatialLocalizationResult.Failure(state, SpatialFailureReason.InsufficientEvidence, message));
                        yield break;
                    }
                    if (!more) break;
                    yield return steps.Current;
                }
                if (source != map || source.transientRevision != revision || generation != alignmentGeneration || cancellation.IsCancellationRequested)
                { completed(SpatialLocalizationResult.Failure(state, SpatialFailureReason.InsufficientEvidence, "Localization cancelled before validation.")); yield break; }
                LastCandidates = referenceJob != null ? referenceJob.Result : job.Result;
                SpatialLocalizationResult final;
                try { final = FinishLocalization(frame, query, LastCandidates); }
                catch (Exception e) { final = Fail(SpatialFailureReason.PoseSolverUnavailable, "Localization validation failed: " + e.Message); }
                completed(final);
            }
            finally
            {
                (steps as IDisposable)?.Dispose();
                if (ReferenceEquals(localizationCancellation, cancellation)) localizationCancellation = null;
                cancellation.Dispose(); localizationRunning = false;
            }
        }
        SpatialLocalizationResult PrepareQuery(SpatialFrame frame, SpatialFeatureSet prepared, out SpatialFeatureSet query)
        {
            query = null;
            Diagnostics = new SpatialDiagnostics();
            // Report the acquired frame even when map readiness rejects before extraction.
            Diagnostics.frameAvailable = frame != null && frame.hasImage && frame.image != null;
            if (Diagnostics.frameAvailable) { Diagnostics.imageWidth = frame.image.width; Diagnostics.imageHeight = frame.image.height; }
            if (frame != null) { Diagnostics.intrinsicsAvailable = frame.intrinsics.IsValid; Diagnostics.intrinsicsReason = frame.intrinsicsReason; Diagnostics.cameraTracking = frame.trackingValid; }
            if (map == null) return Fail(SpatialFailureReason.NoMap, mapLoadFailure ?? "No map is loaded.");
            var validation=ValidateMap();
            if (!validation.valid) { state=SpatialTrackingState.Unavailable; return Fail(SpatialFailureReason.InvalidMap, validation.message); }
            if (validation.readiness == SpatialMapReadiness.PoseOnly) return Fail(SpatialFailureReason.PoseOnlyMap, "Map contains no visual features or landmarks.");
            if (validation.readiness != SpatialMapReadiness.LocalizationReady) return Fail(SpatialFailureReason.MapNotLocalizationReady, $"Map is not localization-ready. Readiness: {validation.readiness}. {validation.message}");
            if (!SpatialFrameGeometry.Fresh(frame, Time.realtimeSinceStartupAsDouble, out var frameReason)) return Fail(SpatialFailureReason.InvalidFrame, frameReason);
            if (lastQueryClock != null && frame.clockDomain != lastQueryClock) return Fail(SpatialFailureReason.InvalidFrame, "Image clock changed without a session reset");
            lastQueryClock = frame.clockDomain;
            if (frame.imageTimestamp <= lastQueryTimestamp) return Fail(SpatialFailureReason.InvalidFrame, "Duplicate or regressed localization image");
            lastQueryTimestamp = frame.imageTimestamp;
            if (!frame.hasImage || frame.image == null) return Fail(SpatialFailureReason.CameraImageUnavailable, "Camera image data is unavailable on this provider.");
            if (!frame.intrinsics.IsValid) return Fail(SpatialFailureReason.InvalidIntrinsics, string.IsNullOrWhiteSpace(frame.intrinsicsReason)?"Camera intrinsics are unavailable or invalid.":frame.intrinsicsReason);
            if (frame.intrinsics.imageWidth != frame.image.width || frame.intrinsics.imageHeight != frame.image.height)
                return Fail(SpatialFailureReason.InvalidIntrinsics, "Image/intrinsics resolution mismatch");
            Diagnostics.featureExtractionAttempted = true;
            var schema = SpatialFeatureSchemas.ForMap(map);
            if (schema == SpatialFeatureSchemas.Orb) return Fail(SpatialFailureReason.InvalidMap, "This experimental ORB map needs a new scan with distinct visual points");
            query=prepared ?? ExtractQuery(frame); Diagnostics.frameAvailable=true; Diagnostics.imageWidth=frame.image.width; Diagnostics.imageHeight=frame.image.height; Diagnostics.intrinsicsAvailable=frame.intrinsics.IsValid; Diagnostics.intrinsicsReason=frame.intrinsicsReason; Diagnostics.cameraTracking=frame.trackingValid;
            if (query == null || query.features == null || query.features.Count > 300 || query.imageWidth != frame.image.width || query.imageHeight != frame.image.height)
                return Fail(SpatialFailureReason.InvalidFrame, "Invalid query feature dimensions/count");
            var queryIds = new System.Collections.Generic.HashSet<string>();
            foreach (var feature in query.features)
            {
                if (feature != null && feature.descriptorType != schema) return Fail(SpatialFailureReason.InvalidFrame, BinaryPatchDescriptor.IncompatibleMessage);
                if (!BinaryFeatureMatcher.Valid(feature) || !queryIds.Add(feature.featureId) || feature.imagePoint.x < 0 || feature.imagePoint.y < 0
                    || feature.imagePoint.x >= query.imageWidth || feature.imagePoint.y >= query.imageHeight)
                    return Fail(SpatialFailureReason.InvalidFrame, "Invalid or duplicate query feature");
            }
            if (query.features.Count < configuration.MinimumFeaturesForLocalization) return Fail(
                new SpatialLocalizationResult { diagnostics=new SpatialLocalizationDiagnostics { queryFeatureCount=query.features.Count } },
                SpatialFailureReason.NoFeatures, "Not enough visible detail. Hold the phone steady and look at fixed, textured surroundings near the mapped starting point.");
            return null;
        }
        SpatialLocalizationResult FinishLocalization(SpatialFrame frame, SpatialFeatureSet query, SpatialLocalizationCandidates matching)
        {
            var correspondences = matching.correspondences;
            var result = new SpatialLocalizationResult { state = SpatialTrackingState.Searching, mapId = map.mapId,
                diagnostics = new SpatialLocalizationDiagnostics { queryFeatureCount = query.features.Count,
                    candidateKeyframeCount = matching.eligibleReferences, rawMatchCountAvailable = true,
                    rawMatchCount = matching.descriptorCandidates, acceptedMatchCount = matching.ambiguityFilteredMatches,
                    landmarkBackedMatchCount = matching.landmarkBackedMatches, observationMatchCount = matching.landmarkBackedMatches,
                    trackMatchCount = matching.landmarkBackedMatches, associationDuplicates = matching.duplicateConflicts,
                    reciprocalMatchCount = matching.reciprocalMatches, missingAssociationCount = matching.missingAssociations,
                    landmarkReferenceCount = matching.mappedFeatures, descriptorComparisonCount = matching.descriptorComparisons } };
            Diagnostics.ratioRejected = matching.landmarkBackedMatches - matching.ambiguityFilteredMatches;
            Diagnostics.duplicateRejected = matching.duplicateConflicts;
            Debug.Log($"[SpatialAI LandmarkRetrieval] method={matching.method} references={matching.referencesConsidered} eligible={matching.eligibleReferences} mappedReferenceDescriptors={matching.mappedFeatures} comparisons={matching.descriptorComparisons} descriptorCandidates={matching.descriptorCandidates} missingAssociations={matching.missingAssociations} landmarkBacked={matching.landmarkBackedMatches} ambiguityFiltered={matching.ambiguityFilteredMatches} reciprocal={matching.reciprocalMatches} conflicts={matching.duplicateConflicts} candidateCorrespondences={correspondences.Count} milliseconds={matching.elapsedMilliseconds:F1}");
            SpatialImageEvidence.QueryCandidates(map, frame, query, matching, configuration.MinimumFeaturesForLocalization);
            result.diagnostics.correspondenceCount=correspondences.Count; UpdateLocalizationDiagnostics(result);
            if(correspondences.Count==0)return Fail(result,SpatialFailureReason.NoLandmarks,"No unambiguous landmark-backed candidates survived filtering.");
            if(correspondences.Count<configuration.MinimumFeaturesForLocalization)return Fail(result,SpatialFailureReason.InsufficientEvidence,$"Insufficient 2D-to-3D correspondences: {correspondences.Count}; minimum is {configuration.MinimumFeaturesForLocalization}.");
            Diagnostics.poseSolverAttempted = true;
            var solved=configuration.PoseSolver.Solve(correspondences,frame.intrinsics); result.diagnostics.inlierCount=solved.inlierCount;result.diagnostics.reprojectionError=solved.reprojectionError;
            result.diagnostics.bestHypothesisInliers = solved.bestHypothesisInliers;
            result.diagnostics.poseFailure = solved.failureReason;
            Debug.Log($"[SpatialAI PoseDiagnosis] candidates={correspondences.Count} bestInitialSupport={solved.bestHypothesisInliers} required={Math.Max(8,(correspondences.Count+1)/2)} reason={solved.failureReason ?? "None"}");
            Debug.Log($"[SpatialAI PoseValidation] attempted=true candidates={correspondences.Count} solverSuccess={solved.success} poseValid={solved.pose.valid} consensusInliers={solved.inlierCount} reprojectionPx={solved.reprojectionError} minimumPass={solved.inlierCount>=configuration.MinimumFeaturesForLocalization} fractionPass={solved.inlierCount>=correspondences.Count*.5f} reprojectionPass={SpatialFrameGeometry.Finite(solved.reprojectionError)&&solved.reprojectionError<=5f} rigidPosePass={SpatialFrameGeometry.ValidPose(new SpatialPose(solved.pose.position,solved.pose.rotation,0))} cameraBasis=physical-image");
            if(!solved.success||!solved.pose.valid||solved.inlierCount<configuration.MinimumFeaturesForLocalization||solved.inlierCount<correspondences.Count*.5f||!SpatialFrameGeometry.Finite(solved.reprojectionError)||solved.reprojectionError>5f||!SpatialFrameGeometry.ValidPose(new SpatialPose(solved.pose.position,solved.pose.rotation,0))){UpdateLocalizationDiagnostics(result);return Fail(result,SpatialFailureReason.PoseEstimationFailed,solved.failureReason ?? "Pose did not pass geometric validation.");}
            Debug.Log($"[SpatialAI LocalizationResult] accepted=true reason=ValidatedVisualPose finalGeometricInliers={solved.inlierCount} reprojectionPx={solved.reprojectionError} build={Application.buildGUID}");
            mapFromAr = SpatialTransformUtility.MapFromAR(solved.pose, frame.ImagePose);
            // PnP solves the physical image camera. Return the display camera pose to
            // callers and AR propagation, using the same visually validated alignment.
            var displayPose = SpatialTransformUtility.ToMapPose(mapFromAr, frame.arPose);
            solved.pose.position = displayPose.position; solved.pose.rotation = displayPose.rotation;
            solved.pose.mapId=map.mapId; solved.pose.timestamp=frame.Timestamp; result.success=true;result.state=SpatialTrackingState.Localized;result.pose=solved.pose;result.confidence=solved.pose.confidence;state=SpatialTrackingState.Tracking;lastPose=solved.pose;alignmentValid=true;lastTrackingTime=Time.realtimeSinceStartup;lastVisualTime=frame.receivedTimestamp;previousArPose=frame.arPose;UpdateLocalizationDiagnostics(result);return result;
        }
        void UpdateLocalizationDiagnostics(SpatialLocalizationResult result){Diagnostics.localization=result.diagnostics;Diagnostics.mapId=map==null?string.Empty:map.mapId;var validation=ValidateMap();Diagnostics.mapReadiness=validation.readiness;Diagnostics.mapReason=validation.message;Diagnostics.keyframes=map==null?0:map.keyframes.Count;Diagnostics.features=map==null?0:map.keyframes.Where(k=>k!=null&&k.features!=null).Sum(k=>k.features.Count);Diagnostics.tracks=map==null||map.tracks==null?0:map.tracks.Count;Diagnostics.landmarks=map==null||map.landmarks==null?0:map.landmarks.Count;if(mapComponentForDiagnostics!=null){Diagnostics.trackExtensionAttempts=mapComponentForDiagnostics.TrackExtensionAttempts;Diagnostics.newTracks=mapComponentForDiagnostics.NewTracks;Diagnostics.observationsCreated=mapComponentForDiagnostics.ObservationsCreated;Diagnostics.triangulationAttempts=mapComponentForDiagnostics.TriangulationAttempts;Diagnostics.triangulationSuccesses=mapComponentForDiagnostics.TriangulationSuccesses;Diagnostics.triangulationFailures=mapComponentForDiagnostics.TriangulationFailures;Diagnostics.triangulationFailureReason=mapComponentForDiagnostics.TriangulationFailureReason;}Diagnostics.failureReason=result.failureReason+": "+result.message;Diagnostics.mapPoseValid=result.pose.valid;Diagnostics.mapPose=result.pose;Diagnostics.trackingState=state;Diagnostics.mapAlignmentValid=alignmentValid;}
        public SpatialMapPose UpdateTracking(SpatialPose arPose, bool arTrackingValid)
        {
            if (state != SpatialTrackingState.Tracking || !alignmentValid || !arTrackingValid || !SpatialFrameGeometry.ValidPose(arPose)
                || Time.realtimeSinceStartup - lastTrackingTime > configuration.TrackingPoseTimeout || LastVisualVerificationAge > 30
                || Vector3.Distance(arPose.position, previousArPose.position) > 2 || Quaternion.Angle(arPose.rotation, previousArPose.rotation) > 60)
            { InvalidateAlignment("Tracking unavailable, stale verification, or AR pose discontinuity"); return lastPose; }
            lastPose = SpatialTransformUtility.ToMapPose(mapFromAr, arPose, 0);
            lastPose.mapId = map.mapId; previousArPose = arPose; lastTrackingTime = Time.realtimeSinceStartup;
            Diagnostics.mapPose = lastPose; Diagnostics.mapPoseValid = true; Diagnostics.mapAlignmentValid = true;
            return lastPose;
        }
        public bool TryGetMapPose(out SpatialMapPose pose)
        {
            if (alignmentValid && (Time.realtimeSinceStartup - lastTrackingTime > configuration.TrackingPoseTimeout || LastVisualVerificationAge > 30))
                InvalidateAlignment("Pose or visual verification expired");
            pose = lastPose; pose.valid = pose.valid && alignmentValid && state == SpatialTrackingState.Tracking; return pose.valid;
        }
        public SpatialMapPose GetMapPose() { TryGetMapPose(out var pose); return pose; }
        SpatialLocalizationResult Fail(SpatialFailureReason reason, string message)
        {
            return Fail(new SpatialLocalizationResult(), reason, message);
        }
        SpatialLocalizationResult Fail(SpatialLocalizationResult result, SpatialFailureReason reason, string message)
        {
            Debug.Log($"[SpatialAI LocalizationResult] accepted=false reason={reason} detail={message} poseAttempted={Diagnostics.poseSolverAttempted} candidateCorrespondences={result.diagnostics.correspondenceCount} finalGeometricInliers=unvalidated build={Application.buildGUID}");
            InvalidateAlignment(message); state = map == null ? SpatialTrackingState.Unavailable : SpatialTrackingState.Searching;
            result.success = false; result.pose = default; result.failureReason = reason;
            result.message = NormalizeMessage(reason, message); result.state = state;
            result.diagnostics.reprojectionError = float.PositiveInfinity;
            UpdateLocalizationDiagnostics(result); return result;
        }
        static string NormalizeMessage(SpatialFailureReason reason,string message) { return string.IsNullOrWhiteSpace(message)?"Localization failed: "+(reason==SpatialFailureReason.None?SpatialFailureReason.InsufficientEvidence:reason):message; }
    }
}
