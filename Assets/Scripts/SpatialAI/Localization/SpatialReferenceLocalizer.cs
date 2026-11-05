using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace SpatialAI
{
    // Ratio competitors belong to one reference image, including unmapped features.
    // References supply alternative hypotheses, not a pooled bag of nearest descriptors.
    public sealed class SpatialReferenceLocalizer
    {
        readonly SpatialLandmarkObservationIndex index;
        readonly SpatialFeatureSet query;
        readonly SpatialCameraIntrinsics intrinsics;
        readonly CancellationToken cancellation;
        readonly BinaryFeatureMatcher matcher;
        readonly IPoseSolver solver;
        public SpatialLocalizationCandidates Result { get; private set; }
        public SpatialReferenceLocalizer(SpatialLandmarkObservationIndex index, SpatialFeatureSet query,
            SpatialCameraIntrinsics intrinsics, BinaryFeatureMatcher matcher, IPoseSolver solver, CancellationToken cancellation = default)
        {
            this.index = index; this.intrinsics = intrinsics; this.solver = solver; this.cancellation = cancellation;
            this.matcher = new BinaryFeatureMatcher { MaxDescriptorDistance = matcher.MaxDescriptorDistance, RatioTest = matcher.RatioTest };
            if (query?.features == null || query.features.Count > 300 || !intrinsics.IsValid) throw new ArgumentException("Invalid reference query");
            this.query = new SpatialFeatureSet { imageWidth = query.imageWidth, imageHeight = query.imageHeight };
            var ids = new HashSet<string>();
            foreach (var f in query.features)
            {
                if (!BinaryFeatureMatcher.Valid(f) || !ids.Add(f.featureId) || f.imagePoint.x < 0 || f.imagePoint.y < 0
                    || f.imagePoint.x >= query.imageWidth || f.imagePoint.y >= query.imageHeight) throw new ArgumentException("Invalid reference query feature");
                this.query.features.Add(SpatialLandmarkObservationIndex.CopyFeature(f));
            }
        }
        public IEnumerator RunSteps()
        {
            if (Result != null) throw new InvalidOperationException("Reference job already completed");
            var timer = Stopwatch.StartNew(); var slice = Stopwatch.StartNew();
            var packed = query.features.ConvertAll(f => PackedDescriptor.Pack(f.descriptor));
            var alternatives = new List<SpatialLocalizationCandidates>();
            long comparisons = 0;
            foreach (var frame in index.Frames)
            {
                cancellation.ThrowIfCancellationRequested();
                if (frame.mappedFeatures.Count < 8) continue;
                var refs = frame.features.ConvertAll(e => PackedDescriptor.Pack(e.feature.descriptor));
                var candidate = new SpatialLocalizationCandidates { mapId = index.MapId,
                    method = "per-reference-ratio-pose-v1", maximumDescriptorDistance = matcher.MaxDescriptorDistance,
                    ratioTest = matcher.RatioTest, mappedFeatures = frame.mappedFeatures.Count };
                var ranked = new List<SpatialCandidateProvenance>();
                for (var q = 0; q < query.features.Count; q++)
                {
                    var best = 257; var second = 257; var nearest = -1;
                    for (var r = 0; r < refs.Count; r++)
                    {
                        if (frame.features[r].feature.descriptorType != query.features[q].descriptorType)
                            throw new ArgumentException("Mixed descriptor schemas in reference job");
                        var distance = PackedDescriptor.Distance(packed[q], refs[r]); comparisons++;
                        if (distance < best) { second = best; best = distance; nearest = r; }
                        else if (distance < second) second = distance;
                    }
                    if (nearest >= 0 && best <= matcher.MaxDescriptorDistance)
                    {
                        candidate.descriptorCandidates++;
                        var e = frame.features[nearest];
                        if (matcher.PassesRatio(best, second) && e.kind == SpatialAssociationKind.LandmarkBacked)
                            ranked.Add(new SpatialCandidateProvenance { queryIndex = q, queryFeatureId = query.features[q].featureId,
                                queryPixel = query.features[q].imagePoint, referenceIndex = e.originalIndex,
                                referenceFeatureId = e.feature.featureId, referencePixel = e.feature.imagePoint,
                                keyframeId = frame.id, landmarkId = e.landmarkId, landmarkPosition = e.point,
                                trackId = e.trackId, observationIndex = e.observationIndex, distance = best,
                                alternativeLandmarkDistance = second, outcome = "CandidateCorrespondence" });
                    }
                    if (slice.Elapsed.TotalMilliseconds >= 4)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Reference matching exceeded time budget");
                        yield return null; slice.Restart();
                    }
                }
                ranked.Sort((a,b) => a.distance != b.distance ? a.distance.CompareTo(b.distance) : a.queryIndex.CompareTo(b.queryIndex));
                var landmarks = new HashSet<string>();
                foreach (var d in ranked)
                {
                    if (candidate.decisions.Exists(previous => Vector2.Distance(previous.queryPixel,d.queryPixel) < 4
                        || Vector2.Distance(previous.referencePixel,d.referencePixel) < 4))
                    { candidate.duplicateConflicts++; continue; }
                    if (!landmarks.Add(d.landmarkId)) { candidate.duplicateConflicts++; continue; }
                    candidate.decisions.Add(d);
                    candidate.correspondences.Add(new SpatialCorrespondence { imageFeatureId = d.queryFeatureId,
                        imagePoint = d.queryPixel, landmarkId = d.landmarkId, worldPoint = d.landmarkPosition, matchScore = 1 - d.distance / 256f });
                }
                candidate.ambiguityFilteredMatches = candidate.landmarkBackedMatches = candidate.correspondences.Count;
                alternatives.Add(candidate);
            }
            alternatives.Sort((a,b) => b.correspondences.Count.CompareTo(a.correspondences.Count));
            SpatialLocalizationCandidates selected = null; SpatialPoseEstimate bestPose = default;
            var accepted = new List<SpatialPoseEstimate>();
            foreach (var candidate in alternatives)
            {
                cancellation.ThrowIfCancellationRequested();
                if (timer.Elapsed.TotalSeconds > 25) throw new TimeoutException("Reference localization exceeded time budget");
                if (candidate.correspondences.Count < 8) continue;
                var pose = solver.Solve(candidate.correspondences, intrinsics);
                if (pose.success && pose.pose.valid)
                {
                    accepted.Add(pose);
                    if (selected == null || pose.inlierCount > bestPose.inlierCount)
                    { selected = candidate; bestPose = pose; }
                }
                yield return null;
            }
            // Visually indistinguishable areas must not produce an arbitrary winning location.
            foreach (var pose in accepted)
                if (Vector3.Distance(pose.pose.position, bestPose.pose.position) > .5f
                    || Quaternion.Angle(pose.pose.rotation, bestPose.pose.rotation) > 15)
                    throw new InvalidOperationException("Saved views suggest conflicting locations; scan more of the surrounding area");
            Result = selected ?? (alternatives.Count > 0 ? alternatives[0] : new SpatialLocalizationCandidates { mapId = index.MapId, method = "per-reference-ratio-pose-v1" });
            Result.referencesConsidered = index.Frames.Count; Result.eligibleReferences = alternatives.Count;
            Result.descriptorComparisons = comparisons; Result.elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
    }
}
