using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    public enum SpatialAssociationKind { DescriptorOnly, LandmarkBacked, InvalidAssociation, ConflictingAssociation, ExcludedGeometry }

    // Job-owned snapshot. No Unity objects, cached session transform, or serialized indices.
    public sealed class SpatialLandmarkObservationIndex
    {
        public sealed class Entry
        {
            public SpatialFeature feature;
            public int originalIndex, observationIndex = -1, mappedIndex = -1;
            public string keyframeId, landmarkId, trackId, reason;
            public Vector3 point;
            public SpatialAssociationKind kind;
        }
        public sealed class Frame
        {
            public string id;
            public int width, height;
            public readonly List<Entry> features = new List<Entry>();
            public readonly List<Entry> mappedFeatures = new List<Entry>();
            // Filtered descriptor index -> original frame.features index, never inferred from IDs.
            public readonly List<int> originalFeatureIndices = new List<int>();
        }
        public string MapId { get; private set; }
        public readonly List<Frame> Frames = new List<Frame>();
        public readonly Dictionary<(string frame, string feature), Entry> ByFeature = new Dictionary<(string, string), Entry>();
        public readonly Dictionary<string, List<Entry>> ByLandmark = new Dictionary<string, List<Entry>>();
        public readonly int[] Counts = new int[5];
        public int DescriptorBytes { get; private set; }

        public static SpatialFeature CopyFeature(SpatialFeature f) => new SpatialFeature { featureId = f.featureId,
            imagePoint = f.imagePoint, descriptor = (byte[])f.descriptor.Clone(), descriptorType = f.descriptorType,
            descriptorSize = f.descriptorSize, quality = f.quality };

        public static SpatialLandmarkObservationIndex Build(SpatialMapData map)
        {
            if (map == null || !SpatialMapStorage.ValidId(map.mapId) || map.keyframes == null || map.keyframes.Count > 200
                || map.landmarks == null || map.landmarks.Count > 60000 || map.tracks == null || map.tracks.Count > 60000)
                throw new ArgumentException("Invalid localization index map or resource bounds");
            var index = new SpatialLandmarkObservationIndex { MapId = map.mapId };
            var landmarks = Unique(map.landmarks, l => l.landmarkId);
            var tracks = Unique(map.tracks, t => t.landmarkId);
            var frameIds = new HashSet<string>();
            foreach (var source in map.keyframes)
            {
                if (source == null || string.IsNullOrEmpty(source.keyframeId) || !frameIds.Add(source.keyframeId)
                    || source.features == null || source.features.Count > 300 || source.observations == null || source.observations.Count > 300)
                    throw new ArgumentException("Invalid or conflicting index keyframe");
                var observations = new Dictionary<string, List<SpatialFeatureObservation>>();
                foreach (var o in source.observations)
                {
                    if (o == null || string.IsNullOrEmpty(o.featureId)) continue;
                    if (!observations.TryGetValue(o.featureId, out var list)) observations.Add(o.featureId, list = new List<SpatialFeatureObservation>());
                    list.Add(o);
                }
                var ids = new HashSet<string>();
                var frame = new Frame { id = source.keyframeId, width = source.imageWidth, height = source.imageHeight };
                index.Frames.Add(frame);
                for (var i = 0; i < source.features.Count; i++)
                {
                    var f = source.features[i];
                    if (!BinaryFeatureMatcher.Valid(f) || !ids.Add(f.featureId) || f.imagePoint.x < 0 || f.imagePoint.y < 0
                        || f.imagePoint.x >= source.imageWidth || f.imagePoint.y >= source.imageHeight)
                        throw new ArgumentException("Invalid, duplicate or incompatible index feature");
                    var entry = new Entry { feature = CopyFeature(f), originalIndex = i, keyframeId = frame.id };
                    frame.features.Add(entry); index.ByFeature.Add((frame.id, f.featureId), entry); index.DescriptorBytes += f.descriptor.Length;
                    if (observations.TryGetValue(f.featureId, out var linked))
                    {
                        entry.kind = SpatialAssociationKind.InvalidAssociation;
                        entry.reason = "Missing or inconsistent observation/track/landmark";
                        if (linked.Count != 1) { entry.kind = SpatialAssociationKind.ConflictingAssociation; entry.reason = "Duplicate feature observations"; }
                        else
                        {
                            var o = linked[0]; entry.observationIndex = source.observations.IndexOf(o); entry.landmarkId = o.landmarkId;
                            if (!string.IsNullOrEmpty(o.landmarkId) && landmarks.TryGetValue(o.landmarkId, out var landmark)
                                && tracks.TryGetValue(o.landmarkId, out var track))
                            {
                                if (landmark == null || track == null) { entry.kind = SpatialAssociationKind.ConflictingAssociation; entry.reason = "Conflicting landmark or track"; }
                                else if (o.keyframeId == frame.id && Vector2.Distance(f.imagePoint, new Vector2(o.x, o.y)) <= .01f
                                    && SpatialFrameGeometry.Finite(o.x) && SpatialFrameGeometry.Finite(o.y)
                                    && !string.IsNullOrEmpty(track.trackId) && track.observations != null && track.observations.Count >= 2
                                    && track.observations.FindAll(t => Same(t, o)).Count == 1
                                    && landmark.observedKeyframes != null && landmark.observedKeyframes.Count >= 2 && landmark.observedKeyframes.Contains(frame.id))
                                {
                                    entry.trackId = track.trackId; entry.point = landmark.position;
                                    var p = PnPPoseSolver.Project(landmark.position, source.cameraPose.position, source.cameraPose.rotation, source.intrinsics);
                                    entry.kind = SpatialFrameGeometry.ValidPose(source.cameraPose) && source.intrinsics.imageWidth == frame.width
                                        && source.intrinsics.imageHeight == frame.height && p.valid && SpatialFrameGeometry.Finite(p.pixel.x)
                                        && SpatialFrameGeometry.Finite(p.pixel.y) && Vector2.Distance(p.pixel, f.imagePoint) <= SpatialTriangulator.MaximumReprojectionError
                                        ? SpatialAssociationKind.LandmarkBacked : SpatialAssociationKind.ExcludedGeometry;
                                    entry.reason = entry.kind == SpatialAssociationKind.LandmarkBacked ? "Validated stored observation" : "Stored projection invalid";
                                }
                            }
                        }
                    }
                    else entry.reason = "Descriptor has no observation";
                    index.Counts[(int)entry.kind]++;
                    if (entry.kind != SpatialAssociationKind.LandmarkBacked) continue;
                    entry.mappedIndex = frame.mappedFeatures.Count;
                    frame.mappedFeatures.Add(entry); frame.originalFeatureIndices.Add(i);
                    if (!index.ByLandmark.TryGetValue(entry.landmarkId, out var representations)) index.ByLandmark.Add(entry.landmarkId, representations = new List<Entry>());
                    representations.Add(entry);
                }
            }
            return index;
        }
        static bool Same(SpatialFeatureObservation a, SpatialFeatureObservation b) => a != null && a.keyframeId == b.keyframeId
            && a.featureId == b.featureId && a.landmarkId == b.landmarkId && a.x == b.x && a.y == b.y;
        static Dictionary<string, T> Unique<T>(List<T> values, Func<T, string> key) where T : class
        {
            var result = new Dictionary<string, T>();
            foreach (var value in values)
            {
                if (value == null || string.IsNullOrEmpty(key(value))) continue;
                var id = key(value); result[id] = result.ContainsKey(id) ? null : value;
            }
            return result;
        }
    }
}
