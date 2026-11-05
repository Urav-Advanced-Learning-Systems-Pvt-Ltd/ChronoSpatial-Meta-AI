using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    // Calibrated triangle-distance P3P fallback. Never uses an AR/session pose seed.
    // Final consensus and refinement remain in the existing production solver.
    public static class SpatialThreePointInitializer
    {
        struct D3
        {
            public double x,y,z;
            public D3(double x,double y,double z) { this.x=x; this.y=y; this.z=z; }
            public D3(Vector3 v) : this(v.x,v.y,v.z) { }
            public double Norm => Math.Sqrt(Dot(this,this));
            public D3 Unit => this*(1/Norm);
            public Vector3 Float => new Vector3((float)x,(float)y,(float)z);
            public static D3 operator +(D3 a,D3 b) => new D3(a.x+b.x,a.y+b.y,a.z+b.z);
            public static D3 operator -(D3 a,D3 b) => new D3(a.x-b.x,a.y-b.y,a.z-b.z);
            public static D3 operator *(D3 a,double s) => new D3(a.x*s,a.y*s,a.z*s);
            public static double Dot(D3 a,D3 b) => a.x*b.x+a.y*b.y+a.z*b.z;
            public static D3 Cross(D3 a,D3 b) => new D3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
        }
        public static bool TryInitialize(List<SpatialCorrespondence> points, SpatialCameraIntrinsics k, float threshold,
            out Vector3 position, out Quaternion rotation, out List<SpatialCorrespondence> inliers, SpatialInitializationTrace trace = null)
        {
            position=default; rotation=Quaternion.identity; inliers=new List<SpatialCorrespondence>();
            if (points == null || points.Count<8 || points.Count>300 || !k.IsValid) return false;
            var samples=new List<int[]>();
            if (points.Count<=11)
            {
                for (var a=0;a<points.Count-2;a++) for (var b=a+1;b<points.Count-1;b++) for (var c=b+1;c<points.Count;c++) samples.Add(new[]{a,b,c});
            }
            else
            {
                var random=new System.Random(918); var used=new HashSet<string>();
                while (samples.Count<192)
                {
                    var a=random.Next(points.Count); var b=random.Next(points.Count); var c=random.Next(points.Count);
                    if (a==b || a==c || b==c) continue;
                    var ids=new[]{a,b,c}; Array.Sort(ids);
                    if (used.Add(ids[0]+":"+ids[1]+":"+ids[2])) samples.Add(ids);
                }
            }
            var bestError=double.PositiveInfinity;
            for (var attempt=0;attempt<samples.Count;attempt++)
            {
                var ids=samples[attempt]; var x=new D3[3]; var rays=new D3[3];
                for (var i=0;i<3;i++)
                {
                    var point=points[ids[i]]; x[i]=new D3(point.worldPoint);
                    rays[i]=new D3((point.imagePoint.x-(double)k.cx)/k.fx,(k.cy-(double)point.imagePoint.y)/k.fy,1).Unit;
                }
                var candidates=Candidates(x,rays); var root=0;
                if (candidates.Count==0 && trace!=null) trace.Record(new SpatialHypothesisTrace { attempt=attempt, method="P3P", outcome="Degenerate triangle or no finite positive-depth roots" });
                foreach (var pose in candidates)
                {
                    var h=new SpatialHypothesisTrace { attempt=attempt, method="P3P root "+root++ };
                    foreach (var id in ids) h.sampleIds.Add(points[id].landmarkId);
                    h.reached.AddRange(new[]{"Triangle distances", "Positive depth polynomial roots", "Proper rigid camera-to-map pose", "Camera-space depth", "Reprojection", "Inlier consensus"});
                    var accepted=new List<SpatialCorrespondence>(); double error=0;
                    foreach (var point in points)
                    {
                        var p=PnPPoseSolver.Project(point.worldPoint,pose.position,pose.rotation,k);
                        var e=Vector2.Distance(p.pixel,point.imagePoint);
                        if (!p.valid) { h.invalidDepth++; continue; }
                        if (!SpatialFrameGeometry.Finite(e) || e>threshold) { h.reprojectionRejected++; continue; }
                        accepted.Add(point); error+=e*e;
                    }
                    h.inliers=accepted.Count;
                    h.outcome=accepted.Count>=8 && accepted.Count>=points.Count*.5f ? "Accepted consensus" : "Insufficient inliers";
                    trace?.Record(h);
                    if (accepted.Count<inliers.Count || accepted.Count==inliers.Count && error>=bestError) continue;
                    inliers=accepted; bestError=error; position=pose.position; rotation=pose.rotation;
                }
            }
            return inliers.Count>=8 && inliers.Count>=points.Count*.5f;
        }
        static List<SpatialPose> Candidates(D3[] x,D3[] ray)
        {
            var output=new List<SpatialPose>();
            var a2=D3.Dot(x[1]-x[2],x[1]-x[2]); var b2=D3.Dot(x[0]-x[2],x[0]-x[2]); var c2=D3.Dot(x[0]-x[1],x[0]-x[1]);
            if (Math.Min(a2,Math.Min(b2,c2))<1e-12 || D3.Cross(x[1]-x[0],x[2]-x[0]).Norm<1e-9) return output;
            var ca=D3.Dot(ray[1],ray[2]); var cb=D3.Dot(ray[0],ray[2]); var cc=D3.Dot(ray[0],ray[1]);
            // v=depth1/depth0, u=depth2/depth0; eliminate u from triangle-distance equations.
            var d=new[]{1d,-2*cc,1d}; var n=Add(Scale(d,(a2-b2)/c2),new[]{1d,0d,-1d}); var l=new[]{2*cb,-2*ca};
            var equation=Add(Add(Mul(n,n),Scale(Mul(n,l),-2*cb)),Mul(Add(new[]{1d},Scale(d,-b2/c2)),Mul(l,l)));
            foreach (var v in PositiveRoots(equation))
            {
                if (Math.Abs(Eval(l,v))<1e-10 || Eval(d,v)<=0) continue;
                var u=Eval(n,v)/Eval(l,v); if (u<=0) continue;
                var s=Math.Sqrt(c2/Eval(d,v)); var y=new[]{ray[0]*s,ray[1]*(s*v),ray[2]*(s*u)};
                if (!SpatialFrameGeometry.Finite(s) || !SpatialFrameGeometry.Finite(u)) continue;
                if (Math.Abs(D3.Dot(y[0]-y[1],y[0]-y[1])-c2)>1e-5*c2 || Math.Abs(D3.Dot(y[0]-y[2],y[0]-y[2])-b2)>1e-5*b2
                    || Math.Abs(D3.Dot(y[1]-y[2],y[1]-y[2])-a2)>1e-5*a2) continue;
                var wx=(x[1]-x[0]).Unit; var wz=D3.Cross(wx,x[2]-x[0]).Unit; var wy=D3.Cross(wz,wx);
                var cx=(y[1]-y[0]).Unit; var cz=D3.Cross(cx,y[2]-y[0]).Unit; var cy=D3.Cross(cz,cx);
                var forward=wx*cx.z+wy*cy.z+wz*cz.z; var up=wx*cx.y+wy*cy.y+wz*cz.y;
                var rotation=Quaternion.LookRotation(forward.Float,up.Float);
                var position=x[0].Float-rotation*y[0].Float;
                var pose=new SpatialPose(position,rotation,0);
                if (SpatialFrameGeometry.ValidPose(pose)) output.Add(pose);
            }
            return output;
        }
        static double[] Add(double[] a,double[] b) { var c=new double[Math.Max(a.Length,b.Length)]; for(var i=0;i<c.Length;i++) c[i]=(i<a.Length?a[i]:0)+(i<b.Length?b[i]:0); return c; }
        static double[] Scale(double[] a,double s) { var b=new double[a.Length]; for(var i=0;i<a.Length;i++) b[i]=a[i]*s; return b; }
        static double[] Mul(double[] a,double[] b) { var c=new double[a.Length+b.Length-1]; for(var i=0;i<a.Length;i++) for(var j=0;j<b.Length;j++) c[i+j]+=a[i]*b[j]; return c; }
        static double Eval(double[] a,double x) { double y=0; for(var i=a.Length-1;i>=0;i--) y=y*x+a[i]; return y; }
        // Isolate real roots between derivative roots, then bisect; degree at most four.
        static List<double> PositiveRoots(double[] source)
        {
            var result=new List<double>(); double scale=0; foreach(var c in source) scale=Math.Max(scale,Math.Abs(c));
            if (scale==0 || !SpatialFrameGeometry.Finite(scale)) return result;
            var degree=source.Length-1; while(degree>0 && Math.Abs(source[degree])<scale*1e-14) degree--;
            if (degree==0) return result;
            var a=new double[degree+1]; for(var i=0;i<=degree;i++) a[i]=source[i]/scale;
            if (degree==1) { var root=-a[0]/a[1]; if(root>0 && SpatialFrameGeometry.Finite(root)) result.Add(root); return result; }
            var derivative=new double[degree]; for(var i=1;i<=degree;i++) derivative[i-1]=i*a[i];
            var cuts=PositiveRoots(derivative); double bound=1; for(var i=0;i<degree;i++) bound=Math.Max(bound,1+Math.Abs(a[i]/a[degree]));
            cuts.RemoveAll(v=>v>=bound); cuts.Insert(0,0); cuts.Add(bound);
            foreach(var cut in cuts)
                if(cut>0 && Math.Abs(Eval(a,cut))<1e-12) AddRoot(result,cut);
            for(var i=1;i<cuts.Count;i++)
            {
                var lo=cuts[i-1]; var hi=cuts[i]; var fl=Eval(a,lo); var fh=Eval(a,hi);
                if(!SpatialFrameGeometry.Finite(fl) || !SpatialFrameGeometry.Finite(fh) || Math.Sign(fl)==Math.Sign(fh)) continue;
                for(var iteration=0;iteration<100;iteration++)
                {
                    var mid=(lo+hi)*.5; var fm=Eval(a,mid);
                    if(Math.Sign(fm)==Math.Sign(fl)) { lo=mid; fl=fm; } else hi=mid;
                }
                AddRoot(result,(lo+hi)*.5);
            }
            result.Sort(); return result;
        }
        static void AddRoot(List<double> roots,double v) { if(v>0 && !roots.Exists(r=>Math.Abs(r-v)<1e-7*Math.Max(1,v))) roots.Add(v); }
    }
}
