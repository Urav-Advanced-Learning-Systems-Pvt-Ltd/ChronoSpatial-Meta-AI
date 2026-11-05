using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    // Searches separate sessions: a candidate never replaces the active map or
    // grants navigation alignment. The user must select a verified match.
    public sealed class SpatialSavedAreaSearch
    {
        public delegate bool Acquire(out SpatialFrame frame);
        public sealed class Result
        {
            public SpatialMapStorage.Entry found;
            public readonly List<SpatialMapStorage.Entry> matches=new List<SpatialMapStorage.Entry>();
            public int checkedMaps, nextIndex;
            public bool incomplete, cancelled;
        }
        readonly Func<string,SpatialMapData> load;
        readonly Func<SpatialSDKSession> createSession;
        readonly Func<double> clock;
        SpatialSDKSession active;
        bool cancelled;
        public SpatialSavedAreaSearch(Func<string,SpatialMapData> load=null,Func<SpatialSDKSession> createSession=null,Func<double> clock=null)
        {
            this.load=load??(id=>SpatialMapStorage.Load(id));
            this.createSession=createSession??(()=>new SpatialSDKSession());
            this.clock=clock??(()=>Time.realtimeSinceStartupAsDouble);
        }
        public void Cancel() { cancelled=true;active?.StopLocalization(); }
        public static bool Verified(string id,SpatialLocalizationResult result) => result!=null && result.success && result.pose.valid
            && result.mapId==id && result.pose.mapId==id && result.diagnostics.correspondenceCount>=8
            && result.diagnostics.inlierCount>=8 && result.diagnostics.inlierCount>=result.diagnostics.correspondenceCount*.5f
            && SpatialFrameGeometry.Finite(result.diagnostics.reprojectionError) && result.diagnostics.reprojectionError<=5;

        public IEnumerator Run(IReadOnlyList<SpatialMapStorage.Entry> entries,int start,Acquire acquire,
            Func<bool> tracking,Action<string> progress,Action<Result> complete,double totalSeconds=40,double perMapSeconds=14,
            Action<SpatialMapStorage.Entry> matched=null)
        {
            cancelled=false;var result=new Result();double deadline=clock()+totalSeconds;
            for(int offset=0;offset<entries.Count;offset++)
            {
                int index=(start+offset)%entries.Count;result.nextIndex=index;
                if(cancelled || clock()>=deadline) { result.incomplete=true;break; }
                var entry=entries[index];if(entry.failure!=null)continue;
                progress("Checking "+entry.Label+". Slowly look around this area.");
                yield return null;
                if(cancelled || clock()>=deadline) { result.incomplete=true;break; }
                result.nextIndex=(index+1)%entries.Count;
                SpatialMapData map=null;
                try { map=load(entry.id); }
                catch(Exception e) { Debug.LogWarning("[SpatialAI Navigation] stage=SearchMapRejected reason="+e.Message); }
                if(map==null || map.mapId!=entry.id)continue;
                active=createSession();active.LoadMap(map);
                SpatialFrame frame=null;IEnumerator steps=null;double attemptDeadline=Math.Min(deadline,clock()+perMapSeconds);
                try
                {
                    // Wait for a fresh native-tracked frame; never search from a
                    // pose-only or cached previous-session observation.
                    while(!cancelled && clock()<attemptDeadline && (!tracking() || !acquire(out frame)))yield return null;
                    if(cancelled)break;
                    if(frame==null) { result.incomplete=true;continue; }
                    SpatialLocalizationResult match=null;steps=active.LocalizeRoutine(frame,value=>match=value);
                    while(!cancelled && clock()<attemptDeadline && tracking())
                    {
                        bool more=false;Exception error=null;
                        try { more=steps.MoveNext(); }catch(Exception e){error=e;}
                        if(error!=null) { Debug.LogWarning("[SpatialAI Navigation] stage=SearchAttemptFailed reason="+error.Message);break; }
                        if(!more)break;
                        yield return steps.Current;
                    }
                    result.checkedMaps++;
                    if(!cancelled && tracking() && clock()<attemptDeadline && Verified(entry.id,match))
                    {
                        if(result.found==null)result.found=entry; // Compatibility: first match, never an automatic selection.
                        if(!result.matches.Exists(value=>value.id==entry.id)) { result.matches.Add(entry);matched?.Invoke(entry); }
                        Debug.Log("[SpatialAI Navigation] stage=SavedAreaVerified map="+entry.id+" inliers="+match.diagnostics.inlierCount);
                    }
                    if(match==null)result.incomplete=true;
                }
                finally
                {
                    (steps as IDisposable)?.Dispose();active?.StopLocalization();active=null;
                    if(frame?.image!=null) { if(Application.isPlaying)UnityEngine.Object.Destroy(frame.image);else UnityEngine.Object.DestroyImmediate(frame.image); }
                }
            }
            result.cancelled=cancelled;
            if(cancelled){result.found=null;result.matches.Clear();}
            complete(result);
        }
    }
}
