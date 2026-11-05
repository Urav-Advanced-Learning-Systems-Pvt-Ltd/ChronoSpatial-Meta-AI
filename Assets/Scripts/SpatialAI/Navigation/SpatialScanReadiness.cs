using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    public static class SpatialScanReadiness
    {
        // This is an authoring gate, not a promise that a future query will localize.
        public static bool Evaluate(SpatialMapData map, out string hint)
        {
            hint = "Scan the starting area. Move slowly and look at textured surfaces.";
            if (map == null || map.landmarks == null || map.landmarks.Count < 24) return false;
            var supported = new List<SpatialKeyframe>();
            foreach (var frame in map.keyframes)
            {
                if (frame.observations == null || frame.observations.Count < 12 || frame.imageWidth <= 0 || frame.imageHeight <= 0) continue;
                var cells = new HashSet<int>();
                foreach (var observation in frame.observations)
                {
                    int x = Mathf.Clamp((int)(observation.x * 3 / frame.imageWidth), 0, 2);
                    int y = Mathf.Clamp((int)(observation.y * 3 / frame.imageHeight), 0, 2);
                    cells.Add(y * 3 + x);
                }
                if (cells.Count >= 4) supported.Add(frame);
            }
            hint = "Look across the starting area and move a little sideways.";
            if (supported.Count < 4) return false;
            float baseline = 0, angle = 0;
            for (int i = 0; i < supported.Count; i++)
                for (int j = i+1; j < supported.Count; j++)
                {
                    baseline = Mathf.Max(baseline, Vector3.Distance(supported[i].cameraPose.position, supported[j].cameraPose.position));
                    angle = Mathf.Max(angle, Quaternion.Angle(supported[i].cameraPose.rotation, supported[j].cameraPose.rotation));
                }
            if (baseline < .4f || angle < 20) return false;
            hint = "Starting area ready. Press Navigation Path.";
            return true;
        }
    }
}
