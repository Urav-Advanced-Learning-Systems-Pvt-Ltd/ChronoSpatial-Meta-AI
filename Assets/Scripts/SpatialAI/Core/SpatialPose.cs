using System;
using UnityEngine;
namespace SpatialAI {
    [Serializable] public struct SpatialPose { public Vector3 position; public Quaternion rotation; public float timestamp; public SpatialPose(Vector3 p, Quaternion r, float t) { position=p; rotation=r; timestamp=t; } }
    // Compatibility alias for older callers. New SDK APIs use SpatialMapPose.
    [Obsolete("Use SpatialMapPose for SDK map-space poses.")] [Serializable] public struct MapPose { public Vector3 position; public Quaternion rotation; public float confidence; public float timestamp; }
}
