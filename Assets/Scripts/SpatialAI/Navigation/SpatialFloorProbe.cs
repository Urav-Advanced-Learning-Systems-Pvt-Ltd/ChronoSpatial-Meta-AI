using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialAI
{
    // Intersect actual tracked plane polygons, including the patch the user is
    // looking at. No infinite-plane, guessed camera-height or feature-point floor.
    public sealed class SpatialFloorProbe
    {
        readonly List<Vector2> boundary=new List<Vector2>();
        readonly SpatialFloorStability stability=new SpatialFloorStability();
        public int TrackedPlanes { get; private set; }
        public int HorizontalPlanes { get; private set; }
        public string Status { get; private set; }
        public void Reset() { stability.Reset(); }

        public bool TryFind(ARPlaneManager planes,Camera camera,double now,out Vector3 point)
        {
            point=default;TrackedPlanes=HorizontalPlanes=0;
            bool found=false;Vector3 candidate=default;
            if(planes!=null && planes.enabled && camera!=null)
            {
                var eye=camera.transform.position;
                var aimed=camera.ViewportPointToRay(new Vector3(.5f,.5f,0));
                var down=new Ray(eye,Vector3.down);
                foreach(var plane in planes.trackables)
                {
                    if(plane==null || plane.subsumedBy!=null || plane.trackingState!=TrackingState.Tracking)continue;
                    TrackedPlanes++;
                    if(plane.alignment!=PlaneAlignment.HorizontalUp || !AllowedClassification(plane.classifications))continue;
                    HorizontalPlanes++;
                    var polygon=plane.boundary;if(!polygon.IsCreated || polygon.Length<3)continue;
                    boundary.Clear();for(int i=0;i<polygon.Length;i++)boundary.Add(polygon[i]);
                    var matrix=plane.transform.localToWorldMatrix;
                    if(!TryHit(down,eye,matrix,boundary,out var hit)
                        && !TryHit(aimed,eye,matrix,boundary,out hit))continue;
                    // If more than one bounded patch qualifies, prefer the lower
                    // observed surface rather than a higher unclassified table.
                    if(!found || hit.y<candidate.y) { found=true;candidate=hit; }
                }
            }
            if(!found)
            {
                stability.Reset();
                Status="Aim at visible floor about a metre in front of you. Move the phone a little sideways while keeping floor detail in view.";
                return false;
            }
            if(!stability.Observe(candidate.y,now))
            { Status="Floor detected. Hold the phone steady for a moment.";return false; }
            point=candidate;Status="Floor ready.";return true;
        }

        public static bool AllowedClassification(PlaneClassifications value) =>
            (value & ~(PlaneClassifications.Floor | PlaneClassifications.Other))==0;

        public static bool TryHit(Ray ray,Vector3 eye,Matrix4x4 localToWorld,IReadOnlyList<Vector2> polygon,out Vector3 hit)
        {
            hit=default;
            if(polygon==null || polygon.Count<3 || ray.direction.y>=-.15f)return false;
            var normal=localToWorld.MultiplyVector(Vector3.up).normalized;
            if(Vector3.Dot(normal,Vector3.up)<.98f)return false;
            var plane=new Plane(normal,localToWorld.MultiplyPoint3x4(Vector3.zero));
            if(!plane.Raycast(ray,out float distance) || distance<0 || distance>4)return false;
            var point=ray.GetPoint(distance);
            if(!SpatialFrameGeometry.Finite(point.x) || !SpatialFrameGeometry.Finite(point.y) || !SpatialFrameGeometry.Finite(point.z))return false;
            float height=eye.y-point.y;
            if(height<.5f || height>2.3f || Vector3.ProjectOnPlane(point-eye,Vector3.up).sqrMagnitude>6.25f)return false;
            var local=localToWorld.inverse.MultiplyPoint3x4(point);
            if(!Contains(polygon,new Vector2(local.x,local.z)))return false;
            hit=point;return true;
        }
        static bool Contains(IReadOnlyList<Vector2> polygon,Vector2 point)
        {
            bool inside=false;
            for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
            {
                var a=polygon[j];var b=polygon[i];var edge=b-a;
                if(!SpatialFrameGeometry.Finite(a.x) || !SpatialFrameGeometry.Finite(a.y))return false;
                if(edge.sqrMagnitude>0)
                {
                    float t=Mathf.Clamp01(Vector2.Dot(point-a,edge)/edge.sqrMagnitude);
                    if((point-(a+t*edge)).sqrMagnitude<.000001f)return true;
                }
                if((a.y>point.y)!=(b.y>point.y) && point.x<(b.x-a.x)*(point.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
            }
            return inside;
        }
    }

    public sealed class SpatialFloorStability
    {
        double since,last=double.NegativeInfinity;
        float height;
        int samples;
        public void Reset() { samples=0;last=double.NegativeInfinity; }
        public bool Observe(float value,double now)
        {
            if(!SpatialFrameGeometry.Finite(value) || double.IsNaN(now) || double.IsInfinity(now)) { Reset();return false; }
            if(samples==0 || now<last || now-last>.45 || Mathf.Abs(value-height)>.1f)
            { height=value;since=now;samples=1;last=now;return false; }
            if(now>last)samples++;
            last=now;return samples>=3 && now-since>=.5;
        }
    }
}
