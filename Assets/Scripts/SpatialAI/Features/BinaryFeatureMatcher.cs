using System.Collections.Generic;

namespace SpatialAI
{
    public sealed class BinaryFeatureMatcher : ISpatialFeatureMatcher
    {
        // Audit scopes remain isolated. Runtime supports explicit legacy and ORB schemas.
        internal static string ExpectedDescriptorType
        {
            get
            {
#if UNITY_EDITOR
                return offlineDescriptorType ?? BinaryPatchDescriptor.Type;
#else
                return BinaryPatchDescriptor.Type;
#endif
            }
        }
#if UNITY_EDITOR
        [System.ThreadStatic] static string offlineDescriptorType;
        public static System.IDisposable BeginOfflineSchemaAudit(string schema)
        {
            if (UnityEngine.Application.isPlaying || offlineDescriptorType != null
                || (schema != BinaryPatchDescriptor.Type && schema != "opencv-orb-4.12-256" && schema != "opencv-akaze-mldb-4.12-256"))
                throw new System.InvalidOperationException("Invalid or nested offline descriptor audit");
            offlineDescriptorType = schema; return new OfflineSchemaScope();
        }
        sealed class OfflineSchemaScope : System.IDisposable
        {
            public void Dispose() { offlineDescriptorType = null; }
        }
#endif
        public int MaxDescriptorDistance = 80;
        public float RatioTest = .8f;
        public int MinimumMatches = 8;
        public int RawCandidates { get; private set; }
        public int DistanceRejected { get; private set; }
        public int RatioRejected { get; private set; }
        public int AmbiguousRejected { get; private set; }
        public int InvalidFeatures { get; private set; }
        public int DuplicateRejected { get; private set; }
        public string CompatibilityFailure { get; private set; }

        public static bool SupportedSchema(string schema)
        {
#if UNITY_EDITOR
            if (offlineDescriptorType != null) return schema == offlineDescriptorType;
#endif
            return schema == BinaryPatchDescriptor.Type || SpatialFeatureSchemas.IsOrb(schema);
        }

        public static bool Valid(SpatialFeature feature) => feature != null && !string.IsNullOrWhiteSpace(feature.featureId)
            && SupportedSchema(feature.descriptorType) && feature.descriptorSize == 32
            && feature.descriptor != null && feature.descriptor.Length == 32
            && SpatialFrameGeometry.Finite(feature.imagePoint.x) && SpatialFrameGeometry.Finite(feature.imagePoint.y);

        public List<SpatialFeatureMatch> Match(SpatialFeatureSet query, SpatialFeatureSet reference)
            => Match(query, reference, default);

        public List<SpatialFeatureMatch> Match(SpatialFeatureSet query, SpatialFeatureSet reference, System.Threading.CancellationToken cancellation)
        {
            RawCandidates = DistanceRejected = RatioRejected = AmbiguousRejected = InvalidFeatures = DuplicateRejected = 0;
            CompatibilityFailure = null;
            var candidates = new List<SpatialFeatureMatch>();
            if (query?.features == null || reference?.features == null || query.features.Count > 300 || reference.features.Count > 300) return candidates;
            string schema = null;
            foreach (var set in new[] { query, reference })
                foreach (var feature in set.features)
                    if (feature != null)
                    {
                        schema = schema ?? feature.descriptorType;
                        if (!SupportedSchema(feature.descriptorType) || feature.descriptorType != schema)
                        { CompatibilityFailure = BinaryPatchDescriptor.IncompatibleMessage; return candidates; }
                    }
            var packedReference = new ulong[reference.features.Count][];
            for (var i = 0; i < reference.features.Count; i++)
                if (!Valid(reference.features[i])) InvalidFeatures++;
                else packedReference[i] = PackedDescriptor.Pack(reference.features[i].descriptor);
            foreach (var feature in query.features)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Valid(feature)) { InvalidFeatures++; continue; }
                var packedQuery = PackedDescriptor.Pack(feature.descriptor);
                var best = int.MaxValue;
                var second = int.MaxValue;
                SpatialFeature nearest = null;
                for (var i = 0; i < reference.features.Count; i++)
                {
                    if (packedReference[i] == null) continue;
                    var other = reference.features[i];
                    var distance = PackedDescriptor.Distance(packedQuery, packedReference[i]);
                    if (distance < best) { second = best; best = distance; nearest = other; }
                    else if (distance < second) second = distance;
                }
                if (nearest == null) continue;
                RawCandidates++;
                if (best > MaxDescriptorDistance) { DistanceRejected++; continue; }
                if (second == int.MaxValue) { AmbiguousRejected++; continue; }
                if (!PassesRatio(best, second)) { if (best == second) AmbiguousRejected++; else RatioRejected++; continue; }
                candidates.Add(new SpatialFeatureMatch { queryFeatureId = feature.featureId, mapFeatureId = nearest.featureId, score = 1f - best / 256f });
            }
            candidates.Sort((left, right) => { var score = right.score.CompareTo(left.score); return score != 0 ? score : string.CompareOrdinal(left.queryFeatureId, right.queryFeatureId); });
            var usedQuery = new HashSet<string>();
            var usedMap = new HashSet<string>();
            var matches = new List<SpatialFeatureMatch>();
            foreach (var match in candidates)
            {
                if (usedQuery.Contains(match.queryFeatureId) || usedMap.Contains(match.mapFeatureId)) { DuplicateRejected++; continue; }
                usedQuery.Add(match.queryFeatureId); usedMap.Add(match.mapFeatureId); matches.Add(match);
            }
            return matches;
        }

        public bool PassesRatio(int best, int second)
        {
            if (best < 0 || second <= 0 || best > 256 || second > 256
                || !SpatialFrameGeometry.Finite(RatioTest) || RatioTest <= 0 || RatioTest >= 1) return false;
            // Force binary32 rounding before the strict comparison. Mono can retain an
            // extended intermediate where IL2CPP rounds 50 * .8f to exactly 40.
            // Positive finite IEEE-754 float bit patterns preserve numeric ordering.
            var thresholdBits = System.BitConverter.SingleToInt32Bits((float)(second * (double)RatioTest));
            return System.BitConverter.SingleToInt32Bits((float)best) < thresholdBits;
        }

        public static int Hamming(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != BinaryPatchDescriptor.Bytes || right.Length != BinaryPatchDescriptor.Bytes) return int.MaxValue;
            var count = 0;
            for (var index = 0; index < 32; index += 8)
                count += PackedDescriptor.PopCount(System.BitConverter.ToUInt64(left, index) ^ System.BitConverter.ToUInt64(right, index));
            return count;
        }
    }
}
