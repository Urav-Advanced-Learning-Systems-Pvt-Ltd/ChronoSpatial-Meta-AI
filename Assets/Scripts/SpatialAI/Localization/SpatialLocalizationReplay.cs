using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SpatialAI
{
    [Serializable] public sealed class SpatialReplaySnapshot
    {
        public string schema = "spatialai-matching-replay-v1", descriptorType = BinaryPatchDescriptor.Type;
        public string ratioArithmetic = "binary32-product-strict-v1";
        public string conventions = "native CPU pixels; x right/y down; metres; Unity camera +z forward/+y up; projection v=cy-fy*y/z";
        public string buildGuid, createdUtc, imageClock, cameraIdentity;
        public double imageTimestamp, receivedTimestamp;
        public SpatialCameraIntrinsics intrinsics;
        public SpatialPose arPose;
        public SpatialPose imagePose;
        public bool hasImagePose;
        public string imagePoseEvidence;
        public int maximumDistance, minimumCorrespondences, maximumCandidates;
        public float ratioTest;
        public SpatialFeatureSet query;
        // Only selected frames and their associated tracks/landmarks; not a loadable full spatial map.
        public SpatialMapData candidateData;
        public SpatialMatchingReport recorded;
    }

    public static class SpatialLocalizationReplay
    {
        public const int MaximumBytes = 8 * 1024 * 1024;
        public const string RequestName = "capture-one-query.request";
        public const string RequestToken = "capture-one-query-v1";
        public const string OutputName = "localization-replay.json";
        public static string DirectoryPath => Path.Combine(Application.persistentDataPath, "SpatialAI", "Diagnostics");

        public static SpatialReplaySnapshot Create(SpatialFeatureSet query, List<SpatialKeyframe> candidates, SpatialMapData source,
            SpatialFrame frame, SpatialSDKConfiguration config, SpatialMatchingReport recorded)
        {
            if (!(config.FeatureMatcher is BinaryFeatureMatcher matcher)) throw new ArgumentException("Replay requires BinaryFeatureMatcher");
            var ids = new HashSet<string>(candidates.SelectMany(k => k.observations).Select(o => o.landmarkId));
            return new SpatialReplaySnapshot
            {
                buildGuid = Application.buildGUID, createdUtc = DateTime.UtcNow.ToString("O"), imageTimestamp = frame.imageTimestamp,
                receivedTimestamp = frame.receivedTimestamp, imageClock = frame.clockDomain, cameraIdentity = frame.cameraIdentity,
                intrinsics = frame.intrinsics, arPose = frame.arPose, imagePose = frame.imagePose, hasImagePose = frame.hasImagePose, imagePoseEvidence = frame.imagePoseEvidence, query = query, maximumDistance = matcher.MaxDescriptorDistance,
                ratioTest = matcher.RatioTest, minimumCorrespondences = config.MinimumFeaturesForLocalization, maximumCandidates = config.MaximumCandidateKeyframes,
                candidateData = new SpatialMapData { mapId = source.mapId, keyframes = candidates,
                    tracks = source.tracks.FindAll(t => ids.Contains(t.landmarkId)), landmarks = source.landmarks.FindAll(l => ids.Contains(l.landmarkId)) },
                recorded = recorded
            };
        }

        public static string Serialize(SpatialReplaySnapshot snapshot)
        {
            Validate(snapshot);
            var json = JsonUtility.ToJson(snapshot);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new ArgumentException("Replay exceeds 8 MiB limit");
            return json;
        }
        public static SpatialReplaySnapshot Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new ArgumentException("Invalid replay size");
            var snapshot = JsonUtility.FromJson<SpatialReplaySnapshot>(json); Validate(snapshot); return snapshot;
        }
        static void Validate(SpatialReplaySnapshot snapshot)
        {
            if (snapshot == null || snapshot.schema != "spatialai-matching-replay-v1" || snapshot.descriptorType != BinaryPatchDescriptor.Type)
                throw new ArgumentException(BinaryPatchDescriptor.IncompatibleMessage);
            // Earlier snapshots omitted arithmetic metadata; recorded decisions must still
            // match exactly on replay. Unknown explicitly versioned arithmetic is rejected.
            if (!string.IsNullOrEmpty(snapshot.ratioArithmetic) && snapshot.ratioArithmetic != "binary32-product-strict-v1")
                throw new ArgumentException("Unsupported replay ratio arithmetic");
            if (!snapshot.intrinsics.IsValid || snapshot.maximumDistance < 0 || snapshot.maximumDistance > 256
                || !SpatialFrameGeometry.Finite(snapshot.ratioTest) || snapshot.ratioTest <= 0 || snapshot.ratioTest >= 1
                || snapshot.minimumCorrespondences < 8 || snapshot.maximumCandidates < 1 || snapshot.maximumCandidates > 6
                || snapshot.candidateData?.keyframes == null || snapshot.candidateData.keyframes.Count > 6
                || snapshot.candidateData.tracks == null || snapshot.candidateData.tracks.Count > 1800
                || snapshot.candidateData.landmarks == null || snapshot.candidateData.landmarks.Count > 1800)
                throw new ArgumentException("Invalid replay configuration or resource bounds");
            ValidateFeatures(snapshot.query);
            if (snapshot.query.imageWidth != snapshot.intrinsics.imageWidth || snapshot.query.imageHeight != snapshot.intrinsics.imageHeight)
                throw new ArgumentException("Replay image/intrinsics mismatch");
            foreach (var keyframe in snapshot.candidateData.keyframes)
            {
                if (keyframe == null || keyframe.observations == null || keyframe.observations.Count > 300) throw new ArgumentException("Invalid replay candidate");
                ValidateFeatures(new SpatialFeatureSet { imageWidth = keyframe.imageWidth, imageHeight = keyframe.imageHeight, features = keyframe.features });
            }
            var totalObservations = 0;
            foreach (var track in snapshot.candidateData.tracks)
            {
                if (track == null || track.observations == null || track.observations.Count > 200) throw new ArgumentException("Invalid replay track bounds");
                totalObservations += track.observations.Count;
            }
            if (totalObservations > 8192) throw new ArgumentException("Replay track observations exceed bounded capture capacity");
        }
        static void ValidateFeatures(SpatialFeatureSet features)
        {
            if (features?.features == null || features.features.Count > 300 || features.imageWidth <= 0 || features.imageHeight <= 0)
                throw new ArgumentException("Invalid replay features");
            var ids = new HashSet<string>();
            foreach (var feature in features.features)
                if (!BinaryFeatureMatcher.Valid(feature) || !ids.Add(feature.featureId) || feature.imagePoint.x < 0 || feature.imagePoint.y < 0
                    || feature.imagePoint.x >= features.imageWidth || feature.imagePoint.y >= features.imageHeight)
                    throw new ArgumentException("Invalid or incompatible replay feature");
        }
        public static SpatialMatchingReport Replay(SpatialReplaySnapshot snapshot)
        {
            Validate(snapshot);
            return SpatialCorrespondenceBuilder.Build(snapshot.query, snapshot.candidateData.keyframes, snapshot.candidateData,
                new BinaryFeatureMatcher { MaxDescriptorDistance = snapshot.maximumDistance, RatioTest = snapshot.ratioTest });
        }
        // No images or networking. Explicit local request, consumed once, one bounded output, never overwritten.
        public static bool CaptureOnce(string directory, Func<SpatialReplaySnapshot> create, out string message)
        {
            message = null;
            var request = Path.Combine(directory, RequestName);
            if (!File.Exists(request)) return false;
            try
            {
                if (new FileInfo(request).Length > 64 || File.ReadAllText(request).Trim() != RequestToken)
                { message = "Replay request invalid; no capture"; return false; }
                File.Delete(request);
                var output = Path.Combine(directory, OutputName);
                if (File.Exists(output)) { message = "Replay already exists; archive it locally before rearming"; return false; }
                var json = Serialize(create());
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream)) writer.Write(json);
                message = "One private matching replay saved locally; capture disarmed"; return true;
            }
            catch (Exception exception) { message = "Replay capture failed: " + exception.GetType().Name; return false; }
        }
    }
}
