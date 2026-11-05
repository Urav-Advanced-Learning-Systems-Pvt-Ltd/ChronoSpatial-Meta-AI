using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    public sealed class SpatialRouteFollower
    {
        public struct Turn { public float distance; public bool left; public float angle; }
        public readonly SpatialNavigationRoute Route;
        public readonly List<Turn> Turns = new List<Turn>();
        readonly float[] distances;
        int segment;
        public float Progress { get; private set; }
        public float Length => distances[distances.Length-1];
        public bool Started { get; private set; }
        public bool OnRoute { get; private set; }
        public bool Arrived { get; private set; }
        public SpatialRouteFollower(SpatialNavigationRoute route)
        {
            Route = route;
            distances = new float[route.points.Count];
            for (int i=1;i<distances.Length;i++) distances[i] = distances[i-1]+Vector3.Distance(route.points[i-1],route.points[i]);
            for (int i=1;i<distances.Length-1;i++)
            {
                float at = distances[i];
                if (at < 1 || Length-at < 1) continue;
                var incoming = PointAt(at)-PointAt(at-1.2f);
                var outgoing = PointAt(at+1.2f)-PointAt(at);
                float angle = Vector3.SignedAngle(incoming,outgoing,Vector3.up);
                if (Mathf.Abs(angle)<40) continue;
                var turn = new Turn { distance=at, left=angle<0, angle=Mathf.Abs(angle) };
                if (Turns.Count>0 && at-Turns[Turns.Count-1].distance<2)
                { if(turn.angle>Turns[Turns.Count-1].angle) Turns[Turns.Count-1]=turn; }
                else Turns.Add(turn);
            }
        }
        public Vector3 PointAt(float distance)
        {
            distance = Mathf.Clamp(distance,0,Length);
            for(int i=1;i<distances.Length;i++) if(distance<=distances[i])
                return Vector3.Lerp(Route.points[i-1],Route.points[i],(distance-distances[i-1])/(distances[i]-distances[i-1]));
            return Route.Endpoint;
        }
        public void Suspend() { OnRoute=false; }
        public bool Update(Vector3 position, bool aligned)
        {
            OnRoute=false;
            if(!aligned || !SpatialNavigationRoute.Finite(position)) return false;
            position=SpatialNavigationRoute.Ground(position,Route.floorY);
            if(!Started)
            {
                if(Vector3.Distance(position,Route.points[0])>1) return false;
                Started=true;
            }
            // Only adjacent segments can advance. A nearby later loop/parallel aisle
            // must never skip an unwalked section or trigger an early arrival.
            float best=float.PositiveInfinity, candidate=Progress;
            int selected=segment;
            for(int i=Mathf.Max(0,segment-1);i<=Mathf.Min(Route.points.Count-2,segment+2);i++)
            {
                var a=Route.points[i]; var delta=Route.points[i+1]-a;
                float t=Mathf.Clamp01(Vector3.Dot(position-a,delta)/delta.sqrMagnitude);
                float distance=Vector3.Distance(position,a+delta*t);
                float along=distances[i]+t*(distances[i+1]-distances[i]);
                if(along<Progress-.75f || along>Progress+1.5f || distance>=best) continue;
                best=distance;candidate=along;selected=i;
            }
            if(best>1.2f) return false;
            OnRoute=true; segment=selected; Progress=Mathf.Max(Progress,candidate);
            Arrived=Length-Progress<=.6f && Vector3.Distance(position,Route.Endpoint)<=.75f;
            return true;
        }
    }
}
