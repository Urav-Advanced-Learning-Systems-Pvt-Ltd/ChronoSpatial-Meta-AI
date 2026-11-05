using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace SpatialAI
{
    [Serializable] public sealed class SpatialReferenceRank
    {
        public string keyframeId;
        public int mappedFeatures, descriptorSupport, landmarkSupport, queryCells, referenceCells;
        public float retrievalScore; // Ordering heuristic only. Never a pose confidence.
    }
    [Serializable] public sealed class SpatialCandidateProvenance
    {
        public int queryIndex, referenceIndex = -1, filteredReferenceIndex = -1, observationIndex = -1;
        public string queryFeatureId, keyframeId, referenceFeatureId, trackId, landmarkId, outcome;
        public int distance = 257, alternativeLandmarkDistance = 257, unassociatedDistance = 257;
        public Vector2 queryPixel, referencePixel;
        public Vector3 landmarkPosition;
    }
    [Serializable] public sealed class SpatialLocalizationCandidates
    {
        public string mapId, method = "all-frames-landmark-distinct-ambiguity-reciprocal-v1";
        public int maximumDescriptorDistance;
        public float ratioTest;
        public int referencesConsidered, eligibleReferences, descriptorOnlyFeatures, mappedFeatures, invalidAssociations, conflictingAssociations, excludedGeometry;
        public int descriptorCandidates, ambiguityFilteredMatches, reciprocalMatches, missingAssociations, landmarkBackedMatches, duplicateConflicts;
        public long descriptorComparisons;
        public int ownedDescriptorBytes;
        public double elapsedMilliseconds;
        public List<SpatialReferenceRank> rankedReferences = new List<SpatialReferenceRank>();
        public List<SpatialCandidateProvenance> decisions = new List<SpatialCandidateProvenance>();
        public List<SpatialCorrespondence> correspondences = new List<SpatialCorrespondence>();
    }

    // One bounded job visits every reference, yielding after approximately 4ms of comparisons.
    // All descriptors and reference coordinates belong to the job; no Texture/Transform access.
    public sealed class SpatialLocalizationCandidateBuilder
    {
        sealed class Best
        {
            public SpatialLandmarkObservationIndex.Entry first, second;
            public int d1 = 257, d2 = 257, unmapped = 257;
            public void Add(SpatialLandmarkObservationIndex.Entry entry, int distance)
            {
                if (entry.kind != SpatialAssociationKind.LandmarkBacked) { unmapped = Math.Min(unmapped, distance); return; }
                if (first != null && first.landmarkId == entry.landmarkId)
                { if (distance < d1) { first = entry; d1 = distance; } return; }
                if (second != null && second.landmarkId == entry.landmarkId)
                { if (distance < d2) { second = entry; d2 = distance; } }
                else if (distance < d2) { second = entry; d2 = distance; }
                if (d2 < d1) { var e = first; first = second; second = e; var d = d1; d1 = d2; d2 = d; }
            }
        }
        sealed class Reverse
        {
            public int query = -1, distance = 257;
            public bool tied;
            public void Add(int q, int d)
            {
                if (d < distance) { distance = d; query = q; tied = false; }
                else if (d == distance && q != query) tied = true;
            }
        }
        readonly SpatialLandmarkObservationIndex index;
        readonly SpatialFeatureSet query;
        readonly BinaryFeatureMatcher thresholds;
        readonly CancellationToken cancellation;
        readonly double budgetSeconds;
        readonly bool bypassMatchingFilters;
        public SpatialLocalizationCandidates Result { get; private set; }
        public SpatialLocalizationCandidateBuilder(SpatialLandmarkObservationIndex index, SpatialFeatureSet query,
            BinaryFeatureMatcher matcher, CancellationToken cancellation = default, double budgetSeconds = 15, bool bypassMatchingFilters = false)
        {
            this.index = index ?? throw new ArgumentNullException(nameof(index));
            if (query == null || query.features == null || query.features.Count > 300 || query.imageWidth <= 0 || query.imageHeight <= 0)
                throw new ArgumentException("Invalid candidate query bounds");
            var ids = new HashSet<string>();
            this.query = new SpatialFeatureSet { imageWidth = query.imageWidth, imageHeight = query.imageHeight };
            foreach (var f in query.features)
            {
                if (!BinaryFeatureMatcher.Valid(f) || !ids.Add(f.featureId) || f.imagePoint.x < 0 || f.imagePoint.y < 0
                    || f.imagePoint.x >= query.imageWidth || f.imagePoint.y >= query.imageHeight) throw new ArgumentException("Invalid candidate query feature");
                this.query.features.Add(SpatialLandmarkObservationIndex.CopyFeature(f));
            }
            thresholds = new BinaryFeatureMatcher { MaxDescriptorDistance = matcher.MaxDescriptorDistance, RatioTest = matcher.RatioTest };
            this.cancellation = cancellation; this.budgetSeconds = budgetSeconds;
            this.bypassMatchingFilters = bypassMatchingFilters;
        }
        public SpatialLocalizationCandidates Run()
        {
            var steps = RunSteps();
            try { while (steps.MoveNext()) { } }
            finally { (steps as IDisposable)?.Dispose(); }
            return Result;
        }
        public IEnumerator RunSteps()
        {
            if (Result != null) throw new InvalidOperationException("Candidate job already completed");
            var timer = Stopwatch.StartNew();
            var slice = Stopwatch.StartNew();
            var report = new SpatialLocalizationCandidates { mapId = index.MapId, referencesConsidered = index.Frames.Count,
                maximumDescriptorDistance = thresholds.MaxDescriptorDistance, ratioTest = thresholds.RatioTest,
                descriptorOnlyFeatures = index.Counts[0], mappedFeatures = index.Counts[1], invalidAssociations = index.Counts[2],
                conflictingAssociations = index.Counts[3], excludedGeometry = index.Counts[4],
                ownedDescriptorBytes = index.DescriptorBytes + query.features.Count * 32 };
            var best = new Best[query.features.Count];
            var packedQuery = query.features.ConvertAll(f => PackedDescriptor.Pack(f.descriptor));
            for (var q = 0; q < best.Length; q++) best[q] = new Best();
            var reverse = new Dictionary<string, Reverse>();
            foreach (var id in index.ByLandmark.Keys) reverse.Add(id, new Reverse());
            foreach (var frame in index.Frames)
            {
                Check(timer);
                var packedReference = frame.features.ConvertAll(e => PackedDescriptor.Pack(e.feature.descriptor));
                var rank = new SpatialReferenceRank { keyframeId = frame.id, mappedFeatures = frame.mappedFeatures.Count };
                var supported = new HashSet<string>(); var queryCells = new HashSet<int>(); var referenceCells = new HashSet<int>();
                for (var q = 0; q < query.features.Count; q++)
                {
                    var local = new Best();
                    for (var referenceIndex = 0; referenceIndex < frame.features.Count; referenceIndex++)
                    {
                        var entry = frame.features[referenceIndex];
                        var distance = PackedDescriptor.Distance(packedQuery[q], packedReference[referenceIndex]);
                        report.descriptorComparisons++;
                        local.Add(entry, distance); best[q].Add(entry, distance);
                        if (entry.kind == SpatialAssociationKind.LandmarkBacked) reverse[entry.landmarkId].Add(q, distance);
                    }
                    if (Math.Min(local.d1, local.unmapped) <= thresholds.MaxDescriptorDistance) rank.descriptorSupport++;
                    if (local.first != null && local.d1 <= thresholds.MaxDescriptorDistance && thresholds.PassesRatio(local.d1, Math.Min(local.d2, local.unmapped)))
                    {
                        supported.Add(local.first.landmarkId);
                        queryCells.Add(Cell(query.features[q].imagePoint, query.imageWidth, query.imageHeight));
                        referenceCells.Add(Cell(local.first.feature.imagePoint, frame.width, frame.height));
                    }
                    if (slice.Elapsed.TotalMilliseconds >= 4) { Check(timer); yield return null; slice.Restart(); }
                }
                rank.landmarkSupport = supported.Count; rank.queryCells = queryCells.Count; rank.referenceCells = referenceCells.Count;
                rank.retrievalScore = rank.landmarkSupport + (rank.queryCells + rank.referenceCells) / 64f;
                if (rank.mappedFeatures > 0) { report.eligibleReferences++; report.rankedReferences.Add(rank); }
            }
            // Broad coverage is retained: ranking drops no eligible frames and is never acceptance.
            report.rankedReferences.Sort((a, b) => { var c = b.retrievalScore.CompareTo(a.retrievalScore); return c != 0 ? c : string.CompareOrdinal(a.keyframeId, b.keyframeId); });
            var usedLandmarks = new HashSet<string>();
            var rawWinner = new Dictionary<string, int>();
            if (bypassMatchingFilters)
            {
                report.method = "raw-nearest-no-distance-ratio-reciprocal-unique-landmarks-v1";
                // Only structural uniqueness remains: the unchanged pose solver rejects duplicate 3D IDs.
                for (var q = 0; q < best.Length; q++)
                {
                    var entry = best[q].first;
                    if (entry == null) continue;
                    if (!rawWinner.TryGetValue(entry.landmarkId, out var previous) || best[q].d1 < best[previous].d1)
                        rawWinner[entry.landmarkId] = q;
                }
            }
            for (var q = 0; q < best.Length; q++)
            {
                var b = best[q]; var feature = query.features[q];
                var d = new SpatialCandidateProvenance { queryIndex = q, queryFeatureId = feature.featureId, queryPixel = feature.imagePoint,
                    distance = b.d1, alternativeLandmarkDistance = b.d2, unassociatedDistance = b.unmapped };
                report.decisions.Add(d);
                if (Math.Min(b.d1, b.unmapped) <= thresholds.MaxDescriptorDistance) report.descriptorCandidates++;
                if (b.first == null || (!bypassMatchingFilters && b.d1 > thresholds.MaxDescriptorDistance))
                { d.outcome = b.unmapped <= thresholds.MaxDescriptorDistance ? "NoLandmarkWithinDistance" : "DistanceRejected"; if (b.unmapped <= thresholds.MaxDescriptorDistance) report.missingAssociations++; continue; }
                var e = b.first;
                d.keyframeId = e.keyframeId; d.referenceIndex = e.originalIndex; d.filteredReferenceIndex = e.mappedIndex;
                d.referenceFeatureId = e.feature.featureId; d.referencePixel = e.feature.imagePoint; d.observationIndex = e.observationIndex;
                d.landmarkId = e.landmarkId; d.landmarkPosition = e.point; d.trackId = e.trackId;
                report.landmarkBackedMatches++;
                if (b.unmapped <= b.d1) report.missingAssociations++;
                // Unmapped/invalid descriptors still compete. Same-landmark observations do not compete with each other.
                if (!bypassMatchingFilters && !thresholds.PassesRatio(b.d1, Math.Min(b.d2, b.unmapped))) { d.outcome = "AmbiguityRejected"; continue; }
                report.ambiguityFilteredMatches++;
                var r = reverse[e.landmarkId];
                if (!bypassMatchingFilters && (r.tied || r.query != q)) { d.outcome = "ReciprocalConflict"; report.duplicateConflicts++; continue; }
                if (bypassMatchingFilters && rawWinner[e.landmarkId] != q) { d.outcome = "DuplicateLandmark"; report.duplicateConflicts++; continue; }
                report.reciprocalMatches++;
                if (!usedLandmarks.Add(e.landmarkId)) { d.outcome = "DuplicateLandmark"; report.duplicateConflicts++; continue; }
                d.outcome = "CandidateCorrespondence";
                report.correspondences.Add(new SpatialCorrespondence { imageFeatureId = feature.featureId, imagePoint = feature.imagePoint,
                    landmarkId = e.landmarkId, worldPoint = e.point, matchScore = 1f - b.d1 / 256f });
            }
            Check(timer); report.elapsedMilliseconds = timer.Elapsed.TotalMilliseconds; Result = report;
        }
        void Check(Stopwatch timer)
        {
            cancellation.ThrowIfCancellationRequested();
            if (timer.Elapsed.TotalSeconds > budgetSeconds) throw new TimeoutException("Localization candidate job exceeded bounded time budget");
        }
        static int Cell(Vector2 p, int width, int height) => Mathf.Clamp((int)(p.x * 4 / width), 0, 3) + 4 * Mathf.Clamp((int)(p.y * 4 / height), 0, 3);
    }
}
