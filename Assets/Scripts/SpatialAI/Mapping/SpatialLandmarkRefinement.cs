using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SpatialAI
{
    // Point-only robust reprojection refinement. Camera poses, observations,
    // descriptors, map origin, places and route coordinates are never optimized.
    // Proposals are staged until a single validated commit; disposal cannot leave
    // half-refined geometry. Intended only for idle authoring after route End.
    public sealed class SpatialLandmarkRefinement
    {
        [Serializable] public sealed class Report
        {
            public int considered, eligible, proposed, changed;
            public bool completed, cancelled, budgetReached;
            public double workMs, maximumSliceMs, validationMs, elapsedMs;
            public double squaredErrorBefore, squaredErrorAfter;
            public string reason;
        }
        struct View { public SpatialKeyframe frame; public Vector2 pixel; }
        struct Change { public SpatialLandmark landmark; public Vector3 before,after; public double oldError,newError; }
        readonly SpatialMapData map;
        readonly int revision;
        readonly Func<double> clock;
        readonly double maximumSeconds;
        readonly List<Change> changes=new List<Change>();
        bool cancelled, started;
        public Report Result { get; }=new Report();
        public SpatialLandmarkRefinement(SpatialMapData map,double maximumSeconds=2,Func<double> clock=null)
        {
            this.map=map;revision=map?.transientRevision ?? 0;
            this.maximumSeconds=Math.Max(0,Math.Min(2,maximumSeconds));
            this.clock=clock??(()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency);
        }
        public void Cancel() { cancelled=true; }
        bool Current() => !cancelled && map!=null && map.transientRevision==revision;
        void Slice(Stopwatch timer)
        { Result.workMs+=timer.Elapsed.TotalMilliseconds;Result.maximumSliceMs=Math.Max(Result.maximumSliceMs,timer.Elapsed.TotalMilliseconds);timer.Reset(); }
        public IEnumerator Run()
        {
            if(started)throw new InvalidOperationException("Refinement jobs are single-use");started=true;
            var wall=Stopwatch.StartNew();var slice=new Stopwatch();double deadline=clock()+maximumSeconds;
            IEnumerator validationSteps=null;
            try
            {
                yield return null; // Present preparation text before any geometry work.
                slice.Start();
                if(!Current()) { Result.cancelled=true;Result.reason="Map changed or preparation cancelled";yield break; }
                if(map.keyframes==null || map.tracks==null || map.landmarks==null || map.keyframes.Count>200 || map.tracks.Count>60000 || map.landmarks.Count>60000)
                { Result.reason="Unsupported geometry collections; original retained";yield break; }
                var frames=new Dictionary<string,SpatialKeyframe>();var landmarks=new Dictionary<string,SpatialLandmark>();
                foreach(var frame in map.keyframes)
                {
                    if(frame==null || string.IsNullOrEmpty(frame.keyframeId) || frames.ContainsKey(frame.keyframeId)) { Result.reason="Invalid frames; original retained";yield break; }
                    frames.Add(frame.keyframeId,frame);
                }
                foreach(var landmark in map.landmarks)
                {
                    if(!Current()) { Result.cancelled=true;yield break; }
                    if(clock()>=deadline) { Result.budgetReached=true;Result.reason="Index preparation budget reached; original retained";yield break; }
                    if(landmark==null || string.IsNullOrEmpty(landmark.landmarkId) || landmarks.ContainsKey(landmark.landmarkId)) { Result.reason="Invalid landmarks; original retained";yield break; }
                    landmarks.Add(landmark.landmarkId,landmark);
                    if(slice.Elapsed.TotalMilliseconds>=2) { Slice(slice);yield return null;slice.Start(); }
                }
                var views=new List<View>(200);var seen=new HashSet<string>();var visited=new HashSet<string>();
                int observations=0;
                foreach(var track in map.tracks)
                {
                    if(!Current()) { Result.cancelled=true;yield break; }
                    if(clock()>=deadline || changes.Count>=2048) { Result.budgetReached=true;break; }
                    Result.considered++;
                    if(track?.observations!=null)
                    {
                        observations+=track.observations.Count;
                        if(observations>60000) { Result.reason="Observation limit exceeded; original retained";yield break; }
                        if(track.observations.Count>=3 && track.observations.Count<=200 && !string.IsNullOrEmpty(track.landmarkId)
                            && visited.Add(track.landmarkId) && landmarks.TryGetValue(track.landmarkId,out var landmark))
                        {
                            views.Clear();seen.Clear();bool valid=true;
                            foreach(var observation in track.observations)
                            {
                                if(observation==null || observation.landmarkId!=track.landmarkId || string.IsNullOrEmpty(observation.keyframeId)
                                    || !seen.Add(observation.keyframeId) || !frames.TryGetValue(observation.keyframeId,out var frame)
                                    || !frame.intrinsics.IsValid || !SpatialFrameGeometry.ValidPose(frame.cameraPose)
                                    || !SpatialFrameGeometry.Finite(observation.x) || !SpatialFrameGeometry.Finite(observation.y)) { valid=false;break; }
                                views.Add(new View{frame=frame,pixel=new Vector2(observation.x,observation.y)});
                            }
                            if(valid)
                            {
                                Result.eligible++;
                                if(Refine(views,landmark.position,out var point,out var before,out var after))
                                    changes.Add(new Change{landmark=landmark,before=landmark.position,after=point,oldError=before,newError=after});
                            }
                        }
                    }
                    // Work slices yield between landmarks, including skip-only maps.
                    if(slice.Elapsed.TotalMilliseconds>=2 || Result.considered%32==0) { Slice(slice);yield return null;slice.Start(); }
                }
                Result.proposed=changes.Count;
                Slice(slice);yield return null;slice.Start(); // Last cancellation boundary before atomic commit.
                if(!Current()) { Result.cancelled=true;Result.reason="Map changed or preparation cancelled";yield break; }
                if(changes.Count>0)
                {
                    var proposed=new Dictionary<string,Vector3>();
                    foreach(var change in changes)proposed.Add(change.landmark.landmarkId,change.after);
                    double validationStartWork=Result.workMs,validationDeadline=clock()+2;
                    SpatialMapValidationResult validation=null;
                    validationSteps=SpatialMapValidationResult.ValidateRoutine(map,value=>validation=value,proposed);
                    while(validationSteps.MoveNext())
                    {
                        if(!Current()) { Result.cancelled=true;yield break; }
                        if(clock()>=validationDeadline) { Result.budgetReached=true;Result.reason="Validation budget reached; original retained";yield break; }
                        if(slice.Elapsed.TotalMilliseconds>=2) { Slice(slice);yield return null;slice.Start(); }
                    }
                    Result.validationMs=Result.workMs-validationStartWork+slice.Elapsed.TotalMilliseconds;
                    if(validation==null || !validation.valid) { Result.reason="Refinement validation failed; original retained: "+validation?.message;yield break; }
                    if(!Current()) { Result.cancelled=true;yield break; }
                    Commit();
                }
                Result.reason=Result.reason??(Result.changed>0?"Validated point refinement applied":"Original geometry retained");
            }
            finally
            {
                if(slice.IsRunning)Slice(slice);
                (validationSteps as IDisposable)?.Dispose();
                Result.elapsedMs=wall.Elapsed.TotalMilliseconds;Result.completed=true;
                if(cancelled)Result.cancelled=true;
                changes.Clear();
            }
        }
        void Commit()
        {
            if(changes.Count==0)return;
            foreach(var change in changes)if(change.landmark.position!=change.before) { Result.cancelled=true;Result.reason="Geometry changed; proposals discarded";return; }
            // All proposed coordinates passed the shared validator without changing
            // the map. No yield or external call inside this bounded publication.
            foreach(var change in changes)change.landmark.position=change.after;
            map.transientRevision++;
            Result.changed=changes.Count;
            foreach(var change in changes) { Result.squaredErrorBefore+=change.oldError;Result.squaredErrorAfter+=change.newError; }
        }
        static bool Error(List<View> views,Vector3 point,out double robust,out double squared,out double maximum)
        {
            robust=squared=maximum=0;
            foreach(var view in views)
            {
                var p=PnPPoseSolver.Project(point,view.frame.cameraPose.position,view.frame.cameraPose.rotation,view.frame.intrinsics);
                if(!p.valid || p.depth>SpatialTriangulator.MaximumDepth)return false;
                double e=Vector2.Distance(p.pixel,view.pixel);if(!SpatialFrameGeometry.Finite(e))return false;
                squared+=e*e;robust+=e<=1.5?e*e:3*e-2.25;maximum=Math.Max(maximum,e);
            }
            return true;
        }
        static bool Refine(List<View> views,Vector3 original,out Vector3 point,out double oldSquared,out double newSquared)
        {
            point=original;oldSquared=newSquared=0;
            if(!Error(views,original,out var oldCost,out oldSquared,out var oldMax) || oldMax>SpatialTriangulator.MaximumReprojectionError)return false;
            var first=views[0];var firstRay=Ray(first);bool baseline=false;
            foreach(var view in views)
                if(Vector3.Distance(first.frame.cameraPose.position,view.frame.cameraPose.position)>=SpatialTriangulator.MinimumBaseline
                    && Vector3.Angle(firstRay,Ray(view))>=SpatialTriangulator.MinimumParallaxDegrees)baseline=true;
            if(!baseline)return false;
            float limit=Mathf.Min(.25f,Vector3.Distance(original,first.frame.cameraPose.position)*.05f);
            double cost=oldCost;
            for(int iteration=0;iteration<8;iteration++)
            {
                double a=0,b=0,c=0,d=0,e=0,f=0,g=0,h=0,i=0;
                foreach(var view in views)
                {
                    var frame=view.frame;var inverse=Quaternion.Inverse(frame.cameraPose.rotation);
                    var camera=inverse*(point-frame.cameraPose.position);if(camera.z<=.0001f)return false;
                    var k=frame.intrinsics;
                    var r0=frame.cameraPose.rotation*Vector3.right;var r1=frame.cameraPose.rotation*Vector3.up;var r2=frame.cameraPose.rotation*Vector3.forward;
                    var u=(k.fx/camera.z)*(r0-r2*(camera.x/camera.z));
                    var v=(-k.fy/camera.z)*(r1-r2*(camera.y/camera.z));
                    double rx=k.fx*camera.x/camera.z+k.cx-view.pixel.x,ry=k.cy-k.fy*camera.y/camera.z-view.pixel.y;
                    double norm=Math.Sqrt(rx*rx+ry*ry),w=norm<=1.5?1:1.5/norm;
                    a+=w*(u.x*u.x+v.x*v.x);b+=w*(u.x*u.y+v.x*v.y);c+=w*(u.x*u.z+v.x*v.z);
                    d+=w*(u.y*u.y+v.y*v.y);e+=w*(u.y*u.z+v.y*v.z);f+=w*(u.z*u.z+v.z*v.z);
                    g-=w*(u.x*rx+v.x*ry);h-=w*(u.y*rx+v.y*ry);i-=w*(u.z*rx+v.z*ry);
                }
                double determinant=a*(d*f-e*e)-b*(b*f-c*e)+c*(b*e-c*d);
                if(a*d*f<=0 || determinant/(a*d*f)<1e-6)return false;
                double damping=(a+d+f)*1e-6;a+=damping;d+=damping;f+=damping;
                determinant=a*(d*f-e*e)-b*(b*f-c*e)+c*(b*e-c*d);
                var step=new Vector3((float)(((d*f-e*e)*g+(c*e-b*f)*h+(b*e-c*d)*i)/determinant),
                    (float)(((c*e-b*f)*g+(a*f-c*c)*h+(b*c-a*e)*i)/determinant),
                    (float)(((b*e-c*d)*g+(b*c-a*e)*h+(a*d-b*b)*i)/determinant));
                if(!SpatialFrameGeometry.Finite(step.sqrMagnitude))return false;
                if(step.sqrMagnitude<1e-12)break;
                bool improved=false;
                for(int trial=0;trial<5;trial++)
                {
                    var candidate=point+step;
                    if(Vector3.Distance(original,candidate)<=limit && Error(views,candidate,out var next,out _,out _) && next<cost)
                    { point=candidate;cost=next;improved=true;break; }
                    step*=.5f;
                }
                if(!improved)break;
            }
            return Vector3.Distance(point,original)>1e-5f && Error(views,point,out var finalCost,out newSquared,out var maximum)
                && maximum<=Math.Min(SpatialTriangulator.MaximumReprojectionError-.01,oldMax+.0001)
                && newSquared<oldSquared-.001 && finalCost<oldCost-.001;
        }
        static Vector3 Ray(View view) => (view.frame.cameraPose.rotation*new Vector3(
            (view.pixel.x-view.frame.intrinsics.cx)/view.frame.intrinsics.fx,
            (view.frame.intrinsics.cy-view.pixel.y)/view.frame.intrinsics.fy,1)).normalized;
    }
}
