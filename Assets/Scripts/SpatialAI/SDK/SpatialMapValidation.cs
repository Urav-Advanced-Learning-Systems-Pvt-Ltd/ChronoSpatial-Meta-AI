using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    [Serializable]
    public sealed class SpatialMapValidationResult
    {
        public bool valid;
        public SpatialMapReadiness readiness;
        public string message;
        public int featureCount;
        public int landmarkCount;

        public static SpatialMapValidationResult Validate(SpatialMapData map)
        {
            SpatialMapValidationResult result=null;
            var steps=ValidateRoutine(map,value=>result=value);
            try { while(steps.MoveNext()){} }finally{(steps as IDisposable)?.Dispose();}
            return result;
        }
        // Same rules for synchronous callers and cooperative authoring checks.
        // Proposed positions are validated without mutating the active map.
        public static IEnumerator ValidateRoutine(SpatialMapData map,Action<SpatialMapValidationResult> completed,
            IReadOnlyDictionary<string,Vector3> proposedPositions=null)
        {
            if (map == null || !SpatialMapStorage.ValidId(map.mapId) || map.version != SpatialMapSerializer.CurrentVersion) { completed(Invalid("Invalid map ID or unsupported version; old visual maps require a new scan"));yield break; }
            if (!string.IsNullOrEmpty(map.displayName) && !SpatialNavigationRoute.ValidName(map.displayName)) { completed(Invalid("Invalid map name"));yield break; }
            if(map.routes!=null && (map.routes.Count>1 || (map.routes.Count==1 && map.routes[0]==null))) { completed(Invalid("Invalid route collection"));yield break; }
            var routeFailure = SpatialNavigationRoute.Validate(map.route);
            if (routeFailure != null) { completed(Invalid(routeFailure));yield break; }
            if (map.keyframes == null || map.landmarks == null || map.tracks == null || map.places == null
                || map.keyframes.Count > 200 || map.landmarks.Count > 60000 || map.tracks.Count > 60000 || map.places.Count > 100) { completed(Invalid("Missing collections or resource limit exceeded"));yield break; }
            var frames = new Dictionary<string, SpatialKeyframe>();
            var features = 0;
            var schema = SpatialFeatureSchemas.ForMap(map);
            foreach (var frame in map.keyframes)
            {
                if (frame == null || string.IsNullOrEmpty(frame.keyframeId) || frames.ContainsKey(frame.keyframeId) || !SpatialFrameGeometry.ValidPose(frame.cameraPose)
                    || frame.features == null || frame.observations == null || frame.features.Count > 300 || frame.observations.Count > 300) { completed(Invalid("Invalid or duplicate keyframe"));yield break; }
                frames.Add(frame.keyframeId, frame);
                if (frame.features.Count > 0 && frame.sourceFrame == ARCoreImagePose.LegacySource) { completed(Invalid(ARCoreImagePose.IncompatibleMessage));yield break; }
                var featureIds = new HashSet<string>();
                foreach (var feature in frame.features)
                {
                    if (feature != null && (!BinaryFeatureMatcher.SupportedSchema(feature.descriptorType) || feature.descriptorType != schema)) { completed(Invalid(BinaryPatchDescriptor.IncompatibleMessage));yield break; }
                    if (!BinaryFeatureMatcher.Valid(feature) || !featureIds.Add(feature.featureId) || feature.imagePoint.x < 0 || feature.imagePoint.y < 0
                        || feature.imagePoint.x >= frame.imageWidth || feature.imagePoint.y >= frame.imageHeight) { completed(Invalid("Invalid feature or image coordinates"));yield break; }
                    yield return null;
                }
                features += frame.features.Count;
                yield return null;
            }
            var landmarks = new Dictionary<string, SpatialLandmark>();
            foreach (var landmark in map.landmarks)
            {
                if (landmark == null || string.IsNullOrEmpty(landmark.landmarkId) || landmarks.ContainsKey(landmark.landmarkId) || !Finite(landmark.position)
                    || landmark.observedKeyframes == null || landmark.observedKeyframes.Count < 2 || landmark.observedKeyframes.Count > 200) { completed(Invalid("Invalid landmark"));yield break; }
                var observed = new HashSet<string>();
                foreach (var id in landmark.observedKeyframes) if (string.IsNullOrEmpty(id) || !frames.ContainsKey(id) || !observed.Add(id)) { completed(Invalid("Dangling or duplicate landmark frame"));yield break; }
                landmarks.Add(landmark.landmarkId, landmark);
                yield return null;
            }
            var observations = new Dictionary<string, SpatialFeatureObservation>();
            foreach (var frame in frames.Values)
            {
                foreach (var observation in frame.observations)
                {
                    if (observation == null || observation.keyframeId != frame.keyframeId || string.IsNullOrEmpty(observation.landmarkId)
                        || !landmarks.TryGetValue(observation.landmarkId, out var landmark) || !landmark.observedKeyframes.Contains(frame.keyframeId)
                        || !frame.intrinsics.IsValid || frame.intrinsics.imageWidth != frame.imageWidth || frame.intrinsics.imageHeight != frame.imageHeight) { completed(Invalid("Dangling observation or invalid intrinsics"));yield break; }
                    var feature = frame.features.Find(candidate => candidate.featureId == observation.featureId);
                    var key = frame.keyframeId + "/" + observation.featureId;
                    if (feature == null || observations.ContainsKey(key) || !SpatialFrameGeometry.Finite(observation.x) || !SpatialFrameGeometry.Finite(observation.y)
                        || Vector2.Distance(feature.imagePoint, new Vector2(observation.x, observation.y)) > .01f) { completed(Invalid("Duplicate or mismatched observation"));yield break; }
                    var position=proposedPositions!=null && proposedPositions.TryGetValue(landmark.landmarkId,out var proposed)?proposed:landmark.position;
                    var projection = PnPPoseSolver.Project(position, frame.cameraPose.position, frame.cameraPose.rotation, frame.intrinsics);
                    if (!projection.valid || Vector2.Distance(projection.pixel, feature.imagePoint) > SpatialTriangulator.MaximumReprojectionError) { completed(Invalid("Landmark reprojection invalid"));yield break; }
                    observations.Add(key, observation);
                    yield return null;
                }
            }
            var trackIds = new HashSet<string>(); var trackedObservations = new HashSet<string>(); var trackedLandmarks = new HashSet<string>();
            foreach (var track in map.tracks)
            {
                if (track == null || string.IsNullOrEmpty(track.trackId) || !trackIds.Add(track.trackId) || string.IsNullOrEmpty(track.landmarkId)
                    || !landmarks.ContainsKey(track.landmarkId) || !trackedLandmarks.Add(track.landmarkId) || track.observations == null || track.observations.Count < 2 || track.observations.Count > 200) { completed(Invalid("Invalid track"));yield break; }
                var frameIds = new HashSet<string>();
                foreach (var observation in track.observations)
                {
                    if (observation == null || !frameIds.Add(observation.keyframeId)) { completed(Invalid("Conflicting track frame"));yield break; }
                    var key = observation.keyframeId + "/" + observation.featureId;
                    if (!observations.TryGetValue(key, out var stored) || stored.landmarkId != track.landmarkId || observation.landmarkId != track.landmarkId
                        || observation.x != stored.x || observation.y != stored.y || !trackedObservations.Add(key)) { completed(Invalid("Track observation mismatch"));yield break; }
                }
                yield return null;
                if (frameIds.Count != landmarks[track.landmarkId].observedKeyframes.Count) { completed(Invalid("Landmark visibility mismatch"));yield break; }
            }
            if (trackedObservations.Count != observations.Count || trackedLandmarks.Count != landmarks.Count) { completed(Invalid("Untracked geometry"));yield break; }
            var placeIds = new HashSet<string>();
            foreach (var place in map.places)
                if (place == null || string.IsNullOrEmpty(place.placeId) || !placeIds.Add(place.placeId) || string.IsNullOrWhiteSpace(place.name)
                    || place.name.Length > SpatialPlace.MaximumNameLength || !Finite(place.mapPosition)) { completed(Invalid("Invalid named place"));yield break; }
            var readiness = landmarks.Count >= 8 ? SpatialMapReadiness.LocalizationReady : landmarks.Count > 0 ? SpatialMapReadiness.LandmarkBearing
                : features > 0 ? SpatialMapReadiness.FeatureBearing : SpatialMapReadiness.PoseOnly;
            var readinessMessage = readiness == SpatialMapReadiness.LocalizationReady
                ? $"{landmarks.Count} landmarks available; query correspondences and pose still require validation"
                : $"Only {landmarks.Count} validated landmarks; minimum 8 required before visual localization. Capture more usable viewpoints and save again.";
            completed(new SpatialMapValidationResult { valid = true, readiness = readiness, message = readinessMessage, featureCount = features, landmarkCount = landmarks.Count });
        }

        static bool Finite(Vector3 point) => SpatialFrameGeometry.Finite(point.x) && SpatialFrameGeometry.Finite(point.y) && SpatialFrameGeometry.Finite(point.z);
        static SpatialMapValidationResult Invalid(string reason) => new SpatialMapValidationResult { message = reason };
    }
}
