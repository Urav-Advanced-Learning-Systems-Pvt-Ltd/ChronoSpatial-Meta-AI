using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    [Serializable] public sealed class SpatialAssociationDecision
    {
        public string queryFeatureId, referenceFeatureId, trackId, landmarkId, outcome;
        public int queryIndex = -1, referenceIndex = -1, observationIndex = -1;
    }
    [Serializable] public sealed class SpatialCandidateMatchReport
    {
        public string keyframeId, compatibilityFailure;
        public int rawCandidates, distanceRejected, ratioRejected, ambiguousRejected, invalidFeatures, descriptorDuplicates;
        public int withObservations, withTracks, withLandmarks, associationDuplicates, geometryRejected, invalidReferences;
        public int missingObservations, missingTracks, missingLandmarks, finalCorrespondences;
        public List<SpatialFeatureMatch> acceptedMatches = new List<SpatialFeatureMatch>();
        public List<SpatialAssociationDecision> decisions = new List<SpatialAssociationDecision>();
    }
    [Serializable] public sealed class SpatialMatchingReport
    {
        public bool rawCountsAvailable;
        public List<SpatialCandidateMatchReport> candidates = new List<SpatialCandidateMatchReport>();
        public List<SpatialCorrespondence> correspondences = new List<SpatialCorrespondence>();
    }

    // Shared by live localization and offline replay. Feature IDs are frame-local; indexes are diagnostics only.
    public static class SpatialCorrespondenceBuilder
    {
        public static List<SpatialKeyframe> SelectCandidates(SpatialMapData map, int maximum)
        {
            var visual = map.keyframes.FindAll(k => k != null && k.features != null && k.features.Count > 0 && k.observations != null && k.observations.Count > 0);
            var result = new List<SpatialKeyframe>();
            var limit = Mathf.Min(visual.Count, Mathf.Clamp(maximum, 1, 6));
            for (var i = 0; i < limit; i++) result.Add(visual[limit == 1 ? 0 : Mathf.RoundToInt(i * (visual.Count - 1f) / (limit - 1))]);
            return result;
        }

        public static SpatialMatchingReport Build(SpatialFeatureSet query, List<SpatialKeyframe> candidates, SpatialMapData map, ISpatialFeatureMatcher matcher)
        {
            var result = new SpatialMatchingReport { rawCountsAvailable = matcher is BinaryFeatureMatcher };
            var usedQueries = new HashSet<string>(); var usedLandmarks = new HashSet<string>();
            var landmarks = new Dictionary<string, SpatialLandmark>();
            var tracks = new Dictionary<string, SpatialFeatureTrack>();
            foreach (var landmark in map.landmarks)
                if (landmark != null && !string.IsNullOrEmpty(landmark.landmarkId))
                    landmarks[landmark.landmarkId] = landmarks.ContainsKey(landmark.landmarkId) ? null : landmark;
            foreach (var track in map.tracks)
                if (track != null && !string.IsNullOrEmpty(track.landmarkId))
                    tracks[track.landmarkId] = tracks.ContainsKey(track.landmarkId) ? null : track;
            foreach (var frame in candidates)
            {
                var report = new SpatialCandidateMatchReport { keyframeId = frame.keyframeId };
                result.candidates.Add(report);
                report.acceptedMatches = matcher.Match(query, new SpatialFeatureSet { features = frame.features, imageWidth = frame.imageWidth, imageHeight = frame.imageHeight });
                if (matcher is BinaryFeatureMatcher binary)
                {
                    report.rawCandidates = binary.RawCandidates; report.distanceRejected = binary.DistanceRejected; report.ratioRejected = binary.RatioRejected;
                    report.ambiguousRejected = binary.AmbiguousRejected; report.invalidFeatures = binary.InvalidFeatures; report.descriptorDuplicates = binary.DuplicateRejected;
                    report.compatibilityFailure = binary.CompatibilityFailure;
                }
                foreach (var match in report.acceptedMatches)
                {
                    var decision = new SpatialAssociationDecision { queryFeatureId = match.queryFeatureId, referenceFeatureId = match.mapFeatureId };
                    report.decisions.Add(decision);
                    decision.queryIndex = query.features.FindIndex(f => f != null && f.featureId == match.queryFeatureId);
                    decision.referenceIndex = frame.features.FindIndex(f => f != null && f.featureId == match.mapFeatureId);
                    if (decision.queryIndex < 0 || decision.referenceIndex < 0
                        || query.features.FindAll(f => f != null && f.featureId == match.queryFeatureId).Count != 1
                        || frame.features.FindAll(f => f != null && f.featureId == match.mapFeatureId).Count != 1)
                    { Reject(report, decision, "InvalidFeatureReference"); continue; }
                    var q = query.features[decision.queryIndex]; var reference = frame.features[decision.referenceIndex];
                    if (!ValidPixel(q, query.imageWidth, query.imageHeight) || !ValidPixel(reference, frame.imageWidth, frame.imageHeight) || !SpatialFrameGeometry.Finite(match.score))
                    { report.geometryRejected++; decision.outcome = "InvalidFeatureGeometry"; continue; }
                    var observations = frame.observations.FindAll(o => o != null && o.featureId == match.mapFeatureId);
                    if (observations.Count == 0) { report.missingObservations++; decision.outcome = "NoObservation"; continue; }
                    report.withObservations++;
                    if (observations.Count != 1) { Reject(report, decision, "AmbiguousObservation"); continue; }
                    var observation = observations[0]; decision.observationIndex = frame.observations.IndexOf(observation); decision.landmarkId = observation.landmarkId;
                    if (observation.keyframeId != frame.keyframeId || string.IsNullOrEmpty(observation.landmarkId)
                        || !SpatialFrameGeometry.Finite(observation.x) || !SpatialFrameGeometry.Finite(observation.y)
                        || Vector2.Distance(reference.imagePoint, new Vector2(observation.x, observation.y)) > .01f)
                    { Reject(report, decision, "ObservationReferenceMismatch"); continue; }
                    if (!tracks.TryGetValue(observation.landmarkId, out var track) || track == null || track.observations == null
                        || track.observations.Count < 2 || track.observations.FindAll(o => SameObservation(o, observation)).Count != 1)
                    { report.missingTracks++; decision.outcome = "MissingOrConflictingTrack"; continue; }
                    report.withTracks++; decision.trackId = track.trackId;
                    if (!landmarks.TryGetValue(observation.landmarkId, out var landmark) || landmark == null
                        || landmark.observedKeyframes == null || landmark.observedKeyframes.Count < 2 || !landmark.observedKeyframes.Contains(frame.keyframeId))
                    { report.missingLandmarks++; decision.outcome = "MissingOrConflictingLandmark"; continue; }
                    var projection = PnPPoseSolver.Project(landmark.position, frame.cameraPose.position, frame.cameraPose.rotation, frame.intrinsics);
                    if (!SpatialFrameGeometry.ValidPose(frame.cameraPose) || !frame.intrinsics.IsValid || !Finite(landmark.position)
                        || !projection.valid || !SpatialFrameGeometry.Finite(projection.pixel.x) || !SpatialFrameGeometry.Finite(projection.pixel.y)
                        || Vector2.Distance(projection.pixel, reference.imagePoint) > SpatialTriangulator.MaximumReprojectionError)
                    { report.geometryRejected++; decision.outcome = "InvalidStoredLandmarkGeometry"; continue; }
                    report.withLandmarks++;
                    if (usedQueries.Contains(q.featureId) || usedLandmarks.Contains(landmark.landmarkId))
                    { report.associationDuplicates++; decision.outcome = "DuplicateQueryOrLandmark"; continue; }
                    // Reserve IDs only after every validation; a bad earlier match cannot consume a later valid one.
                    usedQueries.Add(q.featureId); usedLandmarks.Add(landmark.landmarkId);
                    result.correspondences.Add(new SpatialCorrespondence { imageFeatureId = q.featureId, landmarkId = landmark.landmarkId,
                        imagePoint = q.imagePoint, worldPoint = landmark.position, matchScore = match.score });
                    report.finalCorrespondences++; decision.outcome = "Correspondence";
                }
            }
            return result;
        }
        static bool ValidPixel(SpatialFeature f, int width, int height) => BinaryFeatureMatcher.Valid(f) && f.imagePoint.x >= 0 && f.imagePoint.y >= 0 && f.imagePoint.x < width && f.imagePoint.y < height;
        static bool SameObservation(SpatialFeatureObservation a, SpatialFeatureObservation b) => a != null && a.keyframeId == b.keyframeId && a.featureId == b.featureId && a.landmarkId == b.landmarkId && a.x == b.x && a.y == b.y;
        static bool Finite(Vector3 point) => SpatialFrameGeometry.Finite(point.x) && SpatialFrameGeometry.Finite(point.y) && SpatialFrameGeometry.Finite(point.z);
        static void Reject(SpatialCandidateMatchReport r, SpatialAssociationDecision d, string reason) { r.invalidReferences++; d.outcome = reason; }
    }
}
