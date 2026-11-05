using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    // Thread-safe semantic entity registry on SpatialMap. Entities persist
    // in SpatialMapData.semanticEntities alongside landmarks; all access is
    // serialized through SemanticSync because SAM results may be prepared
    // off the Unity thread, even though registration normally happens on it.
    public partial class SpatialMap
    {
        readonly object semanticSync = new object();

        public int SemanticEntityCount
        {
            get { lock (semanticSync) return Data.semanticEntities != null ? Data.semanticEntities.Count : 0; }
        }

        public List<SemanticEntity3D> SemanticEntitiesSnapshot()
        {
            lock (semanticSync)
            {
                EnsureSemanticEntities();
                return new List<SemanticEntity3D>(Data.semanticEntities);
            }
        }

        void EnsureSemanticEntities()
        {
            if (Data.semanticEntities == null) Data.semanticEntities = new List<SemanticEntity3D>();
        }

        // Registers a lifted entity. When an entity with the same label
        // (case-insensitive) already exists within clusterRadius metres, the
        // detection is merged into it instead of creating a duplicate:
        // position becomes the running mean, confidence keeps the strongest
        // observation, and observationCount grows. Returns the stored
        // entity; created is true only for a genuinely new physical object.
        public SemanticEntity3D RegisterSemanticEntity(SemanticEntity3D candidate,
            string sourceKeyframeId, float clusterRadius, out bool created)
        {
            created = false;
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.label)) return null;
            float radius = Mathf.Max(0.05f, clusterRadius);
            lock (semanticSync)
            {
                EnsureSemanticEntities();
                SemanticEntity3D nearest = null;
                float nearestSqr = radius * radius;
                foreach (var entity in Data.semanticEntities)
                {
                    if (entity == null) continue;
                    if (!string.Equals(entity.label, candidate.label, StringComparison.OrdinalIgnoreCase)) continue;
                    float sqr = (entity.position - candidate.position).sqrMagnitude;
                    if (sqr <= nearestSqr) { nearestSqr = sqr; nearest = entity; }
                }
                if (nearest != null)
                {
                    int n = Mathf.Max(1, nearest.observationCount);
                    nearest.position = (nearest.position * n + candidate.position) / (n + 1);
                    nearest.extent = Vector3.Max(nearest.extent, candidate.extent);
                    nearest.confidence = Mathf.Max(nearest.confidence, candidate.confidence);
                    nearest.observationCount = n + 1;
                    nearest.lastSeenTimestamp = candidate.discoveryTimestamp;
                    if (!string.IsNullOrEmpty(sourceKeyframeId) && !nearest.sourceKeyframeIds.Contains(sourceKeyframeId))
                        nearest.sourceKeyframeIds.Add(sourceKeyframeId);
                    Data.transientRevision++;
                    return nearest;
                }
                candidate.entityId = string.IsNullOrEmpty(candidate.entityId)
                    ? "sem-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    : candidate.entityId;
                candidate.observationCount = Mathf.Max(1, candidate.observationCount);
                if (string.IsNullOrEmpty(candidate.discoveredAtUtc))
                    candidate.discoveredAtUtc = DateTime.UtcNow.ToString("O");
                candidate.lastSeenTimestamp = candidate.discoveryTimestamp;
                if (!string.IsNullOrEmpty(sourceKeyframeId) && !candidate.sourceKeyframeIds.Contains(sourceKeyframeId))
                    candidate.sourceKeyframeIds.Add(sourceKeyframeId);
                Data.semanticEntities.Add(candidate);
                Data.transientRevision++;
                created = true;
                return candidate;
            }
        }
    }
}
