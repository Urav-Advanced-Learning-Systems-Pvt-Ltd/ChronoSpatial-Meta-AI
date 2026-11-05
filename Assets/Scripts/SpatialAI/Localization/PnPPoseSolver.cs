using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    /// Nonplanar DLT hypotheses with a calibrated P3P fallback, bounded consensus and local refinement.
    /// Planar/degenerate initialization fails explicitly; no saved pose or AR pose seeds this solver.
    public sealed class PnPPoseSolver : IPoseSolver
    {
        public int Iterations = 30; public float InlierThreshold = 5f; public int MinimumInliers = 6;
        public SpatialPoseEstimate Solve(IReadOnlyList<SpatialCorrespondence> points, SpatialCameraIntrinsics k)
            => Solve(points, k, null);
        public SpatialPoseEstimate Solve(IReadOnlyList<SpatialCorrespondence> points, SpatialCameraIntrinsics k, SpatialInitializationTrace trace)
        {
            trace = trace ?? new SpatialInitializationTrace { captureHypotheses = false };
            if (!k.IsValid || points == null || points.Count < MinimumInliers) { trace.inputFailure = "Insufficient correspondences or invalid intrinsics"; return Fail(trace); }
            var valid=new List<SpatialCorrespondence>(); foreach(var p in points) if(p!=null&&Finite(p.worldPoint)&&Finite(p.imagePoint)) valid.Add(p);
            if(valid.Count<MinimumInliers || points.Count>300) { trace.inputFailure = "Insufficient finite correspondences or resource bound"; return Fail(trace); }
            var imageIds = new HashSet<string>(); var landmarkIds = new HashSet<string>();
            foreach (var point in valid) if (string.IsNullOrEmpty(point.imageFeatureId) || string.IsNullOrEmpty(point.landmarkId) || !imageIds.Add(point.imageFeatureId) || !landmarkIds.Add(point.landmarkId)) { trace.inputFailure = "Missing or duplicate correspondence IDs"; return Fail(trace); }
            if (!SpatialPoseInitializer.TryInitialize(valid, k, InlierThreshold, out var position, out var rotation, out var consensus, trace)) return Fail(trace);
            var allPoints = valid; valid = consensus;
            var bestPosition = position; var bestRotation = rotation; float last=float.PositiveInfinity;
            for(var iteration=0;iteration<Iterations;iteration++) {
                var j=new double[6,6]; var b=new double[6]; var error=0f; var usable=0;
                foreach(var p in valid) { var projected=Project(p.worldPoint,position,rotation,k); if(!projected.valid) continue; var residual=new Vector2(p.imagePoint.x-projected.pixel.x,p.imagePoint.y-projected.pixel.y); error+=residual.sqrMagnitude; usable++; var d=new double[6,2]; for(var axis=0;axis<6;axis++){var pp=position;var rr=rotation; Apply(ref pp,ref rr,axis,0.0001f); var q=Project(p.worldPoint,pp,rr,k); if(!q.valid) continue; d[axis,0]=(q.pixel.x-projected.pixel.x)/0.0001; d[axis,1]=(q.pixel.y-projected.pixel.y)/0.0001;} for(var a=0;a<6;a++){b[a]+=d[a,0]*residual.x+d[a,1]*residual.y;for(var c=0;c<6;c++)j[a,c]+=d[a,0]*d[c,0]+d[a,1]*d[c,1];} }
                if(usable<MinimumInliers||float.IsNaN(error)||error>last) { position=bestPosition; rotation=bestRotation; break; } last=error; bestPosition=position; bestRotation=rotation;
                var step=Solve(j,b); if(step==null) break; position+=new Vector3((float)step[0],(float)step[1],(float)step[2]); var axisVec=new Vector3((float)step[3],(float)step[4],(float)step[5]); var angle=axisVec.magnitude; if(angle>0) rotation=Quaternion.AngleAxis(angle*Mathf.Rad2Deg,axisVec/angle)*rotation;
            }
            var inliers=0;var sum=0f;foreach(var p in allPoints){var q=Project(p.worldPoint,position,rotation,k);if(!q.valid)continue;var e=Vector2.Distance(q.pixel,p.imagePoint);if(e<=InlierThreshold){inliers++;sum+=e*e;}}
            if(inliers<Math.Max(8,MinimumInliers)||inliers<allPoints.Count*.5f||!SpatialFrameGeometry.ValidPose(new SpatialPose(position,rotation,0))) return Fail(trace); var pose=new SpatialMapPose{position=position,rotation=rotation,confidence=Mathf.Clamp01(inliers/(float)allPoints.Count*(1f-(sum/inliers)/(InlierThreshold*InlierThreshold))),valid=true,inlierCount=inliers,reprojectionError=Mathf.Sqrt(sum/inliers)}; return new SpatialPoseEstimate{success=true,bestHypothesisInliers=trace.bestHypothesisInliers,pose=pose,inlierCount=inliers,reprojectionError=Mathf.Sqrt(sum/inliers)};
        }
        static double[] Solve(double[,] a,double[] b){var n=6;var m=new double[n,n+1];for(var i=0;i<n;i++){for(var c=0;c<n;c++)m[i,c]=a[i,c];m[i,n]=b[i];}for(var c=0;c<n;c++){var pivot=c;for(var r=c+1;r<n;r++)if(Math.Abs(m[r,c])>Math.Abs(m[pivot,c]))pivot=r;if(Math.Abs(m[pivot,c])<1e-9)return null;for(var x=c;x<=n;x++){var t=m[c,x];m[c,x]=m[pivot,x];m[pivot,x]=t;}var d=m[c,c];for(var x=c;x<=n;x++)m[c,x]/=d;for(var r=0;r<n;r++)if(r!=c){var f=m[r,c];for(var x=c;x<=n;x++)m[r,x]-=f*m[c,x];}}var s=new double[n];for(var i=0;i<n;i++)s[i]=m[i,n];return s;}
        static void Apply(ref Vector3 p,ref Quaternion r,int axis,float d){if(axis<3){if(axis==0)p.x+=d;if(axis==1)p.y+=d;if(axis==2)p.z+=d;}else{var v=axis==3?Vector3.right:axis==4?Vector3.up:Vector3.forward;r=Quaternion.AngleAxis(d*Mathf.Rad2Deg,v)*r;}}
        public static (bool valid,Vector2 pixel,float depth) Project(Vector3 world,Vector3 position,Quaternion rotation,SpatialCameraIntrinsics k){var c=Quaternion.Inverse(rotation)*(world-position);if(!k.IsValid||!Finite(c)||c.z<=0.0001f)return(false,Vector2.zero,c.z);return(true,new Vector2(k.fx*c.x/c.z+k.cx,k.cy-k.fy*c.y/c.z),c.z);}
        static bool Finite(Vector3 v)=>!float.IsNaN(v.x)&&!float.IsNaN(v.y)&&!float.IsNaN(v.z)&&!float.IsInfinity(v.x)&&!float.IsInfinity(v.y)&&!float.IsInfinity(v.z); static bool Finite(Vector2 v)=>!float.IsNaN(v.x)&&!float.IsNaN(v.y)&&!float.IsInfinity(v.x)&&!float.IsInfinity(v.y);
        static SpatialPoseEstimate Fail(SpatialInitializationTrace trace) => new SpatialPoseEstimate
        {
            success = false, reprojectionError = float.PositiveInfinity,
            bestHypothesisInliers = trace.bestHypothesisInliers,
            failureReason = trace.inputFailure ?? trace.failureReason ?? "Pose refinement did not meet the inlier or rigid-pose requirements."
        };
    }
}
