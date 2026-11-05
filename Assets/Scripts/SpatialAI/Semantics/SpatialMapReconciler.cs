using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    // Phase 4: 4D Living Map reconciler. Compares each incoming semantic
    // detection against the persistent map and maintains the dynamic
    // lifecycle on SemanticEntity3D:
    //  - same label shifted more than MoveThresholdMeters -> Moved, with
    //    PreviousPosition / DisplacementMeters recorded;
    //  - a genuinely new entity inside the active route corridor ->
    //    NewObstruction;
    //  - a known entity re-observed in place -> Static, confidence grows.
    // OnPhysicalChangeDetected fires for Moved, NewObstruction, and Removed
    // transitions so navigation, speech, and debug views can react at once.
    // All calls are expected on the Unity main thread (SAM results are
    // applied there), matching SpatialMap.RegisterSemanticEntity usage.
    public class SpatialMapReconciler : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] SpatialMap spatialMap;

        [Header("Change Detection")]
        [Tooltip("Same-label shift beyond this distance (metres) counts as Moved.")]
        [SerializeField] float moveThresholdMeters = 0.6f;
        [Tooltip("Same-label search radius (metres) for matching a detection to a known entity.")]
        [SerializeField] float matchRadiusMeters = 3f;
        [Tooltip("Horizontal distance (metres) from the active route that counts as on-path.")]
        [SerializeField] float obstructionCorridorMeters = 0.9f;
        [Tooltip("Confidence gained each time an entity is re-verified in place.")]
        [SerializeField] float staticConfidenceGain = 0.05f;

        public float MoveThresholdMeters { get { return moveThresholdMeters; } }
        public float ObstructionCorridorMeters { get { return obstructionCorridorMeters; } }

        public event Action<SemanticEntity3D> OnPhysicalChangeDetected;

        void Awake()
        {
            if (spatialMap == null) spatialMap = FindAnyObjectByType<SpatialMap>();
        }

        // Lazy resolve so runtime-created components work even when added
        // before the map exists (Awake ordering is not guaranteed).
        SpatialMap Map
        {
            get
            {
                if (spatialMap == null) spatialMap = FindAnyObjectByType<SpatialMap>();
                return spatialMap;
            }
        }

        // The route currently being navigated, or null when idle.
        public SpatialNavigationRoute ActiveRoute
        {
            get { var map = Map; return map != null && map.Data != null ? map.Data.route : null; }
        }

        // Horizontal (XZ) distance from a map point to the route polyline.
        // Y is ignored: a floor-standing object blocks the path regardless
        // of the height at which SAM lifted its centre.
        public static float DistanceToRoute(Vector3 position, SpatialNavigationRoute route)
        {
            if (route == null || route.points == null || route.points.Count == 0)
                return float.PositiveInfinity;
            if (route.points.Count == 1)
                return HorizontalDistance(position, route.points[0]);
            float best = float.PositiveInfinity;
            for (int i = 0; i < route.points.Count - 1; i++)
            {
                float d = DistanceToSegmentXZ(position, route.points[i], route.points[i + 1]);
                if (d < best) best = d;
            }
            return best;
        }

        static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float DistanceToSegmentXZ(Vector3 p, Vector3 a, Vector3 b)
        {
            float abx = b.x - a.x, abz = b.z - a.z;
            float lengthSqr = abx * abx + abz * abz;
            float t = lengthSqr > 0.000001f
                ? Mathf.Clamp01(((p.x - a.x) * abx + (p.z - a.z) * abz) / lengthSqr)
                : 0f;
            float dx = p.x - (a.x + abx * t), dz = p.z - (a.z + abz * t);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public bool IsOnActiveRoute(Vector3 position)
        {
            var route = ActiveRoute;
            return route != null && DistanceToRoute(position, route) <= obstructionCorridorMeters;
        }

        // Reconciles one lifted detection with the persistent map. Returns
        // the stored entity; created is true only for a new physical object.
        public SemanticEntity3D ReconcileDetection(SemanticEntity3D candidate,
            string sourceKeyframeId, out bool created)
        {
            created = false;
            var map = Map;
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.label) || map == null)
                return null;

            var match = FindLabelMatch(candidate);
            if (match == null)
            {
                // No known object with this label nearby: register it new.
                // A tiny cluster radius forces the create path, reusing the
                // map's ID and timestamp bookkeeping.
                var stored = map.RegisterSemanticEntity(candidate, sourceKeyframeId,
                    0.05f, out created);
                if (stored == null) return null;
                stored.PreviousPosition = stored.position;
                stored.DisplacementMeters = 0f;
                stored.LastVerifiedTimestamp = candidate.discoveryTimestamp;
                if (IsOnActiveRoute(stored.position))
                {
                    stored.Status = SemanticEntityStatus.NewObstruction;
                    map.Data.transientRevision++;
                    Debug.Log("[SpatialAI Reconciler] New obstruction on route: " + stored.label +
                        " at " + stored.position.ToString("F2") + " id " + stored.entityId);
                    RaiseChange(stored);
                }
                else
                {
                    stored.Status = SemanticEntityStatus.Static;
                }
                return stored;
            }

            // Known object: compare the stored position with this sighting
            // before any merging can hide the shift.
            float displacement = Vector3.Distance(match.position, candidate.position);
            match.lastSeenTimestamp = candidate.discoveryTimestamp;
            match.LastVerifiedTimestamp = candidate.discoveryTimestamp;
            match.observationCount = Mathf.Max(1, match.observationCount) + 1;
            match.extent = Vector3.Max(match.extent, candidate.extent);
            if (!string.IsNullOrEmpty(sourceKeyframeId) && !match.sourceKeyframeIds.Contains(sourceKeyframeId))
                match.sourceKeyframeIds.Add(sourceKeyframeId);

            if (displacement > moveThresholdMeters)
            {
                match.PreviousPosition = match.position;
                match.position = candidate.position;
                match.rotation = candidate.rotation;
                match.depthSource = candidate.depthSource;
                match.DisplacementMeters = displacement;
                match.confidence = Mathf.Max(match.confidence, candidate.confidence);
                match.Status = SemanticEntityStatus.Moved;
                map.Data.transientRevision++;
                Debug.Log("[SpatialAI Reconciler] Moved: " + match.label + " shifted " +
                    displacement.ToString("F2") + "m from " + match.PreviousPosition.ToString("F2") +
                    " to " + match.position.ToString("F2") + " id " + match.entityId);
                RaiseChange(match);
                return match;
            }

            // Re-observed in place: fold the sighting into the running mean,
            // grow confidence, and confirm the entity as Static. A Moved
            // entity confirmed at its new position settles back to Static;
            // an obstruction still sitting on the route stays flagged.
            int n = Mathf.Max(1, match.observationCount - 1);
            match.position = (match.position * n + candidate.position) / (n + 1);
            match.confidence = Mathf.Min(0.99f,
                Mathf.Max(match.confidence + staticConfidenceGain, candidate.confidence));
            bool wasObstruction = match.Status == SemanticEntityStatus.NewObstruction;
            match.Status = wasObstruction && IsOnActiveRoute(match.position)
                ? SemanticEntityStatus.NewObstruction
                : SemanticEntityStatus.Static;
            map.Data.transientRevision++;
            return match;
        }

        SemanticEntity3D FindLabelMatch(SemanticEntity3D candidate)
        {
            SemanticEntity3D nearest = null;
            bool nearestSamId = false;
            float nearestSqr = matchRadiusMeters * matchRadiusMeters;
            var map = Map;
            if (map == null) return null;
            foreach (var entity in map.SemanticEntitiesSnapshot())
            {
                if (entity == null) continue;
                if (!string.Equals(entity.label, candidate.label, StringComparison.OrdinalIgnoreCase))
                    continue;
                float sqr = (entity.position - candidate.position).sqrMagnitude;
                if (sqr > nearestSqr) continue;
                // SAM object ids are stable only within one response, never
                // across separate requests, so an id can prefer - never
                // force - a match: a same-id entity in radius wins over a
                // nearer different-id one, but a different id never blocks.
                bool samId = candidate.samObjectId >= 0 && entity.samObjectId == candidate.samObjectId;
                if (nearest == null || (samId && !nearestSamId) ||
                    (samId == nearestSamId && sqr < nearestSqr))
                {
                    nearest = entity;
                    nearestSamId = samId;
                    nearestSqr = sqr;
                }
            }
            return nearest;
        }

        // Marks every mapped entity absent from a completed rescan as
        // Removed. seenEntityIds holds the entityIds observed in that scan.
        // Returns how many entities transitioned.
        public int SweepUnseen(ICollection<string> seenEntityIds, float scanTimestamp)
        {
            var map = Map;
            if (map == null || seenEntityIds == null) return 0;
            int removed = 0;
            foreach (var entity in map.SemanticEntitiesSnapshot())
            {
                if (entity == null || entity.Status == SemanticEntityStatus.Removed) continue;
                if (seenEntityIds.Contains(entity.entityId)) continue;
                entity.Status = SemanticEntityStatus.Removed;
                entity.LastVerifiedTimestamp = scanTimestamp;
                removed++;
                Debug.Log("[SpatialAI Reconciler] Removed: " + entity.label +
                    " last seen at " + entity.position.ToString("F2") + " id " + entity.entityId);
                RaiseChange(entity);
            }
            if (removed > 0) map.Data.transientRevision++;
            return removed;
        }

        // Living Map Sync merge report: counts the physical changes a
        // downloaded map caused, for the "Living Map Synced" notice.
        public struct LanMergeReport
        {
            public bool applied;
            public string failure;
            public int newEntities;
            public int movedObjects;
            public int obstructions;
            public int adoptedKeyframes;
            public int adoptedLandmarks;
        }

        // Merges a map received over LAN sync into the live map. Entities
        // go through the normal reconcile path (new/moved/static/
        // obstruction transitions plus change events); keyframes and
        // landmarks missing by ID are adopted verbatim. Main thread only.
        public LanMergeReport Merge(SpatialMapData incoming)
        {
            var report = new LanMergeReport();
            var map = Map;
            if (incoming == null) { report.failure = "Downloaded map is empty."; return report; }
            if (map == null || map.Data == null) { report.failure = "No active map to merge into."; return report; }
            if (incoming.semanticEntities != null)
            {
                foreach (var candidate in incoming.semanticEntities)
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.label)) continue;
                    var stored = ReconcileDetection(candidate, "lan-sync", out bool created);
                    if (stored == null) continue;
                    if (created) report.newEntities++;
                    else if (stored.Status == SemanticEntityStatus.Moved) report.movedObjects++;
                    else if (stored.Status == SemanticEntityStatus.NewObstruction) report.obstructions++;
                }
            }
            if (incoming.keyframes != null)
            {
                var known = new HashSet<string>(StringComparer.Ordinal);
                foreach (var keyframe in map.Data.keyframes)
                    if (keyframe != null) known.Add(keyframe.keyframeId);
                foreach (var keyframe in incoming.keyframes)
                {
                    if (keyframe == null || string.IsNullOrEmpty(keyframe.keyframeId)) continue;
                    if (!known.Add(keyframe.keyframeId)) continue;
                    map.Data.keyframes.Add(keyframe);
                    report.adoptedKeyframes++;
                }
            }
            if (incoming.landmarks != null)
            {
                var known = new HashSet<string>(StringComparer.Ordinal);
                foreach (var landmark in map.Data.landmarks)
                    if (landmark != null) known.Add(landmark.landmarkId);
                foreach (var landmark in incoming.landmarks)
                {
                    if (landmark == null || string.IsNullOrEmpty(landmark.landmarkId)) continue;
                    if (!known.Add(landmark.landmarkId)) continue;
                    map.Data.landmarks.Add(landmark);
                    report.adoptedLandmarks++;
                }
            }
            map.Data.transientRevision++;
            report.applied = true;
            return report;
        }

        void RaiseChange(SemanticEntity3D entity)
        {
            var handler = OnPhysicalChangeDetected;
            if (handler != null) handler.Invoke(entity);
        }
    }
}
