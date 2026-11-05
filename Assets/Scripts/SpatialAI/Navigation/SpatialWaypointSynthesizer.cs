using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    // Autonomous waypoint synthesis without manual route walking. Keyframe
    // camera poses are proven unobstructed free space, so the graph they
    // form is a walkable topological map. A* searches that graph from the
    // current pose to the target, penalizing Phase 4 MOVED / NEW_OBSTRUCTION
    // entities, then smooths and resamples the result for arrow rendering.
    public static class SpatialWaypointSynthesizer
    {
        public const float LinkStepMeters = 1.5f;   // Matches the recorder's maximum step.
        public const float CorridorMeters = 0.9f;   // Matches the reconciler obstruction corridor.
        public const float CollisionMeters = 0.35f; // Hard exclusion around changed entities.
        public const float ResampleMeters = 0.4f;   // Arrow-friendly waypoint spacing.

        struct Obstacle { public Vector2 center; public float radius; }

        // All positions are map space. Start and target are grounded to the
        // start height. Returns null with a user-facing failure when no clear
        // path exists through mapped free space.
        public static List<Vector3> Synthesize(SpatialMapData map, Vector3 start, Vector3 target, out string failure)
        {
            failure = null;
            if (map == null || map.keyframes == null || map.keyframes.Count == 0)
            { failure = "No mapped free space yet. Scan the area first."; return null; }
            float floorY = start.y;
            var nodes = new List<Vector3> { new Vector3(start.x, floorY, start.z) };
            foreach (var frame in map.keyframes)
            {
                if (frame == null) continue;
                var p = frame.cameraPose.position;
                if (!SpatialNavigationRoute.Finite(p)) continue;
                var grounded = new Vector3(p.x, floorY, p.z);
                if ((grounded - nodes[nodes.Count - 1]).sqrMagnitude < 0.01f) continue;
                nodes.Add(grounded);
            }
            nodes.Add(new Vector3(target.x, floorY, target.z));

            var obstacles = new List<Obstacle>();
            if (map.semanticEntities != null)
            {
                foreach (var entity in map.semanticEntities)
                {
                    if (entity == null) continue;
                    if (entity.Status != SemanticEntityStatus.Moved && entity.Status != SemanticEntityStatus.NewObstruction) continue;
                    float r = Mathf.Max(entity.extent.x, entity.extent.z) * 0.5f + CorridorMeters;
                    obstacles.Add(new Obstacle
                    {
                        center = new Vector2(entity.position.x, entity.position.z),
                        radius = Mathf.Max(CollisionMeters, r)
                    });
                }
            }

            var path = Search(nodes, obstacles);
            if (path == null) { failure = "No clear path to the destination through mapped space."; return null; }
            return Smooth(path, floorY);
        }

        static List<Vector3> Search(List<Vector3> nodes, List<Obstacle> obstacles)
        {
            int n = nodes.Count;
            var grid = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                var key = new Vector2Int(Mathf.FloorToInt(nodes[i].x / LinkStepMeters), Mathf.FloorToInt(nodes[i].z / LinkStepMeters));
                if (!grid.TryGetValue(key, out var list)) { list = new List<int>(); grid[key] = list; }
                list.Add(i);
            }
            var blocked = new bool[n];
            var penalty = new float[n];
            for (int i = 1; i < n - 1; i++)
            {
                var xz = new Vector2(nodes[i].x, nodes[i].z);
                foreach (var o in obstacles)
                {
                    float d = Vector2.Distance(xz, o.center);
                    if (d < CollisionMeters) { blocked[i] = true; break; }
                    if (d < o.radius) penalty[i] = 10f;
                }
            }
            float stepSqr = LinkStepMeters * LinkStepMeters;
            var g = new float[n];
            var parent = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.PositiveInfinity; parent[i] = -1; }
            g[0] = 0;
            var open = new List<int> { 0 };
            var goal = nodes[n - 1];
            while (open.Count > 0)
            {
                int pick = -1;
                float pickF = float.PositiveInfinity;
                for (int k = 0; k < open.Count; k++)
                {
                    int i = open[k];
                    float f = g[i] + Vector3.Distance(nodes[i], goal);
                    if (f < pickF) { pickF = f; pick = k; }
                }
                int cur = open[pick];
                open.RemoveAt(pick);
                if (closed[cur]) continue;
                closed[cur] = true;
                if (cur == n - 1) break;
                var cell = new Vector2Int(Mathf.FloorToInt(nodes[cur].x / LinkStepMeters), Mathf.FloorToInt(nodes[cur].z / LinkStepMeters));
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue(new Vector2Int(cell.x + dx, cell.y + dz), out var bucket)) continue;
                        foreach (int j in bucket)
                        {
                            if (j == cur || closed[j] || blocked[j]) continue;
                            if ((nodes[j] - nodes[cur]).sqrMagnitude > stepSqr) continue;
                            float cost = g[cur] + Vector3.Distance(nodes[cur], nodes[j]) * (1f + penalty[j]);
                            if (cost < g[j]) { g[j] = cost; parent[j] = cur; if (!open.Contains(j)) open.Add(j); }
                        }
                    }
            }
            if (parent[n - 1] < 0 && n > 2) return null;
            var path = new List<Vector3>();
            for (int i = n - 1; i >= 0; i = parent[i])
            {
                path.Add(nodes[i]);
                if (i == 0) break;
            }
            path.Reverse();
            return path;
        }

        static List<Vector3> Smooth(List<Vector3> path, float floorY)
        {
            var pts = new List<Vector3>(path);
            for (int iter = 0; iter < 2 && pts.Count > 2; iter++)
            {
                var next = new List<Vector3> { pts[0] };
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    next.Add(Vector3.Lerp(pts[i], pts[i + 1], 0.25f));
                    next.Add(Vector3.Lerp(pts[i], pts[i + 1], 0.75f));
                }
                next.Add(pts[pts.Count - 1]);
                pts = next;
            }
            float total = 0;
            for (int i = 1; i < pts.Count; i++) total += Vector3.Distance(pts[i - 1], pts[i]);
            int count = Mathf.Max(2, Mathf.CeilToInt(total / ResampleMeters) + 1);
            var sampled = new List<Vector3> { new Vector3(pts[0].x, floorY, pts[0].z) };
            float step = total / (count - 1);
            float acc = 0;
            int seg = 1;
            for (int s = 1; s < count - 1; s++)
            {
                float want = step * s;
                while (seg < pts.Count - 1 && acc + Vector3.Distance(pts[seg - 1], pts[seg]) < want)
                { acc += Vector3.Distance(pts[seg - 1], pts[seg]); seg++; }
                float segLen = Vector3.Distance(pts[seg - 1], pts[seg]);
                float t = segLen > 0 ? (want - acc) / segLen : 0;
                var p = Vector3.Lerp(pts[seg - 1], pts[seg], Mathf.Clamp01(t));
                sampled.Add(new Vector3(p.x, floorY, p.z));
            }
            sampled.Add(new Vector3(pts[pts.Count - 1].x, floorY, pts[pts.Count - 1].z));
            var deduped = new List<Vector3>();
            foreach (var p in sampled)
                if (deduped.Count == 0 || (p - deduped[deduped.Count - 1]).sqrMagnitude > 0.0025f) deduped.Add(p);
            return deduped.Count >= 2 ? deduped : new List<Vector3> { sampled[0], sampled[sampled.Count - 1] };
        }
    }
}
