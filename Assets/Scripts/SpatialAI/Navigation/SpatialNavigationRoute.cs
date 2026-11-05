using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    [Serializable]
    public sealed class SpatialNavigationRoute
    {
        public int version = 1;
        public string routeId;
        public string destination;
        public float floorY;
        public List<Vector3> points = new List<Vector3>();
        public const int MaximumPoints = 4000;
        public const float MaximumStep = 1.5f;
        public Vector3 Endpoint => points[points.Count - 1];

        public static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name)
            && name.Trim().Length <= SpatialPlace.MaximumNameLength && !HasControl(name);
        static bool HasControl(string value) { foreach (var c in value) if (char.IsControl(c)) return true; return false; }
        public static Vector3 Ground(Vector3 position, float floor) => new Vector3(position.x, floor, position.z);
        public static bool Finite(Vector3 p) => SpatialFrameGeometry.Finite(p.x) && SpatialFrameGeometry.Finite(p.y) && SpatialFrameGeometry.Finite(p.z);
        public static string Validate(SpatialNavigationRoute route)
        {
            if (route == null) return null; // Legacy maps have no route.
            if (route.version != 1 || !SpatialMapStorage.ValidId(route.routeId) || !ValidName(route.destination)
                || !SpatialFrameGeometry.Finite(route.floorY) || route.points == null
                || route.points.Count < 2 || route.points.Count > MaximumPoints) return "Invalid navigation route";
            float length = 0;
            for (int i = 0; i < route.points.Count; i++)
            {
                var point = route.points[i];
                if (!Finite(point) || Mathf.Abs(point.y - route.floorY) > .02f) return "Route must stay on its recorded floor";
                if (i == 0) continue;
                var distance = Vector3.Distance(route.points[i-1], point);
                if (distance < .02f || distance > MaximumStep) return "Route contains a gap or duplicate point";
                length += distance;
            }
            return length < 1 ? "Walk at least one metre before ending the path" : length > 200 ? "Route exceeds 200 metres" : null;
        }

        // Require nearby triangulated visual support, not merely AR poses along a walk.
        public static bool HasVisualCoverage(SpatialMapData map, SpatialNavigationRoute route)
        {
            foreach (var point in route.points)
            {
                bool supported = false;
                foreach (var frame in map.keyframes)
                    if (frame.observations != null && frame.observations.Count >= 8
                        && Vector3.Distance(point, Ground(frame.cameraPose.position, route.floorY)) <= 3)
                    { supported = true; break; }
                if (!supported) return false;
            }
            return true;
        }
    }

    public sealed class SpatialRouteRecorder
    {
        public SpatialNavigationRoute Route { get; }
        public bool Paused { get; private set; }
        public string Reason { get; private set; }
        Vector3 previous;
        double previousTime;
        bool hasPrevious;
        public SpatialRouteRecorder(string destination, float floor)
        {
            Route = new SpatialNavigationRoute { routeId = Guid.NewGuid().ToString("N"), destination = destination.Trim(), floorY = floor };
        }
        public void Pause(string reason) { Paused = true; Reason = reason; hasPrevious = false; }
        public bool Resume(Vector3 point)
        {
            point = SpatialNavigationRoute.Ground(point, Route.floorY);
            if (!SpatialNavigationRoute.Finite(point) || (Route.points.Count > 0 && Vector3.Distance(point, Route.Endpoint) > .5f)) return false;
            Paused = false; Reason = null; hasPrevious = false; return true;
        }
        public bool Sample(Vector3 point, double now, bool tracking, bool force = false)
        {
            if (Paused) return false;
            if (!tracking || !SpatialNavigationRoute.Finite(point) || double.IsNaN(now) || double.IsInfinity(now))
            { Pause("Tracking lost. Return to the last recorded point."); return false; }
            point = SpatialNavigationRoute.Ground(point, Route.floorY);
            if (hasPrevious && (now <= previousTime || now - previousTime > 1
                || Vector3.Distance(point, previous) > Mathf.Max(.35f, (float)(now - previousTime) * 2.5f)))
            { Pause("Camera movement was interrupted. Return to the last recorded point."); return false; }
            previous = point; previousTime = now; hasPrevious = true;
            if (Route.points.Count > 0)
            {
                float distance = Vector3.Distance(Route.Endpoint, point);
                if (distance > SpatialNavigationRoute.MaximumStep) { Pause("Route gap detected. Return to the last recorded point."); return false; }
                if (distance < (force ? .03f : .35f)) return false;
            }
            if (Route.points.Count >= SpatialNavigationRoute.MaximumPoints) { Pause("Path capacity reached. Press End to save."); return false; }
            Route.points.Add(point); return true;
        }
    }
}
