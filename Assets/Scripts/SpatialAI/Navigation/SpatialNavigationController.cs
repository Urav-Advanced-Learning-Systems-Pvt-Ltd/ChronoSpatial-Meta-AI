using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialAI
{
    [DefaultExecutionOrder(1100)]
    public sealed class SpatialNavigationController : MonoBehaviour
    {
        public enum Flow { Idle, Armed, Recording, Recovering, Finalizing, Navigating, ConfirmingName, SearchingSaved }
        public Flow Mode { get; private set; }
        public string ReadinessHint { get; private set; }
        public bool BlocksMapActions => Mode!=Flow.Idle || (ui?.ModalOpen ?? false);
        public bool HidesLegacyOverlay => Mode!=Flow.Idle || (ui?.ModalOpen ?? false);
        SpatialAIController controller;
        SpatialNavigationUI ui;
        SpatialSpeech speech;
        SpatialRouteArrows arrows;
        SpatialRouteRecorder recorder;
        SpatialRouteFollower follower;
        ARPlaneManager planes;
        readonly SpatialFloorProbe floorProbe=new SpatialFloorProbe();
        float nextStartCheck,nextFloorTrace,nextStartSpeech;
        string lastFloorWait;
        bool ready, paused, waitingAttempt, autoLocate, arrivedSpoken, startedSpoken, startRequested, guidanceAligned;
        readonly SpatialScanHandoff handoff=new SpatialScanHandoff();
        readonly SpatialStartVerification startVerification=new SpatialStartVerification();
        SpatialLandmarkRefinement refinement;
        IEnumerator refinementSteps;
        bool refinementDone;
        SpatialSavedAreaSearch areaSearch;
        IEnumerator searchRoutine;
        int searchOffset;
        readonly List<SpatialMapStorage.Entry> areaMatches=new List<SpatialMapStorage.Entry>();
        float floorY, startHeight, nextReadiness, nextAttempt, nextPrompt;
        int failedAttempts, turnIndex;
        bool approachSpoken, turnSpoken;
        string destination, instruction;
        Action afterDialog;
        void Awake()
        {
            controller=GetComponent<SpatialAIController>();
            ui=gameObject.AddComponent<SpatialNavigationUI>();speech=gameObject.AddComponent<SpatialSpeech>();
            var arrowsObject=new GameObject("Navigation arrows");arrowsObject.transform.SetParent(transform,false);arrows=arrowsObject.AddComponent<SpatialRouteArrows>();
            ui.PathPressed+=PreparePath;ui.StartPressed+=StartPath;ui.EndPressed+=End;
            ReadinessHint="Scan the starting area to prepare a navigation path.";
        }
        public void NewScan()
        {
            areaMatches.Clear();
            CancelRefinement();refinementDone=false;
            CancelSearch();handoff.Reset();startVerification.Reset();waitingAttempt=false;startRequested=false;floorProbe.Reset();
            Mode=Flow.Idle;recorder=null;follower=null;autoLocate=false;ready=false;
            ReadinessHint="Look around this starting point at fixed details. Move the phone a little sideways; you do not need to scan the whole room.";
            nextReadiness=0;arrows.Hide();speech.Stop();
            speech.Speak("Look around this starting point at fixed details. Move the phone a little sideways. You do not need to scan the whole room.");
        }
        public void ShowSaveDialog(Action after=null)
        {
            if(BlocksMapActions) return;
            if(controller.CaptureInProgress || controller.CapturePending) { controller.ShowMessage("Stop scanning and wait for mapping to finish before saving.");return; }
            if(controller.Map==null || controller.Map.KeyframeCount==0) { controller.ShowMessage("Scan an area before saving.");return; }
            ui.NameDialog("Save Map",controller.Map.Data.displayName,value=>
            {
                var failure=controller.SaveNamedMap(value);
                if(failure==null) { speech.Speak("Map saved.");afterDialog=after; }
                return failure;
            });
        }
        public void OfferSaveBeforeScan() { ui.UnsavedChanges(()=>ShowSaveDialog(FindSavedArea),FindSavedArea); }
        public void FindSavedArea()
        { StartSavedAreaSearch(false); }
        void LookAgainSavedArea() { CancelAreaSearch();StartSavedAreaSearch(true); }
        void StartSavedAreaSearch(bool keepMatches)
        {
            if(Mode!=Flow.Idle || ui.ModalOpen || controller.CaptureInProgress || controller.CapturePending)return;
            List<SpatialMapStorage.Entry> entries;
            try { entries=SpatialMapStorage.ListMaps(); }
            catch(Exception e) { controller.ShowMessage("Saved maps could not be checked: "+e.Message);return; }
            entries.RemoveAll(e=>e.failure!=null);
            // Offer existing destination routes before standalone area maps.
            entries.Sort((a,b)=> { int priority=string.IsNullOrEmpty(a.destination).CompareTo(string.IsNullOrEmpty(b.destination));return priority!=0?priority:string.CompareOrdinal(a.id,b.id); });
            if(entries.Count==0) { controller.StartNewScan();return; }
            if(!keepMatches || !SpatialARTracking.TryGetPoseValidity(controller.Origin?.Camera,out _))areaMatches.Clear();
            areaMatches.RemoveAll(match=>!entries.Exists(entry=>entry.id==match.id));
            autoLocate=false;controller.CancelNavigationLocalization();Mode=Flow.SearchingSaved;
            ui.SearchDialog(ScanNewArea,CancelAreaSearch);
            foreach(var match in areaMatches)ui.SearchMatch(match,controller.LoadMapById);
            speech.Speak("Look around. Checking saved maps and paths. Choose your destination from the matches shown.");
            areaSearch=new SpatialSavedAreaSearch();
            searchRoutine=RunSavedSearch(entries);StartCoroutine(searchRoutine);
        }
        IEnumerator RunSavedSearch(List<SpatialMapStorage.Entry> entries)
        {
            var steps=areaSearch.Run(entries,searchOffset%entries.Count,controller.TryAcquireSearchFrame,
                ()=>!paused && SpatialARTracking.TryGetPoseValidity(controller.Origin?.Camera,out _),
                message=>ui.SearchProgress((areaMatches.Count>0?areaMatches.Count+" choices available. ":"")+message),result=>
                {
                    searchOffset=result.nextIndex;if(Mode!=Flow.SearchingSaved)return;
                    if(result.cancelled)areaMatches.Clear();
                    var message=result.cancelled?"Camera tracking was interrupted. Look around and choose Look again."
                        :areaMatches.Count>0?areaMatches.Count+" saved choices found. Select your destination."+(result.incomplete?" More maps remain unchecked. Choose Look again to continue.":"")
                        :result.incomplete?"The search is not complete. Look around and try again, or scan a new area."
                        :"No saved area was verified from this view. Look around and try again, or scan a new area.";
                    ui.SearchResult(message,areaMatches,controller.LoadMapById,ScanNewArea,LookAgainSavedArea,CancelAreaSearch);
                    speech.Speak(message);
                },matched:entry=>
                {
                    if(Mode!=Flow.SearchingSaved || areaMatches.Exists(match=>match.id==entry.id))return;
                    areaMatches.Add(entry);ui.SearchMatch(entry,controller.LoadMapById);
                    if(areaMatches.Count==1)speech.Speak("A saved destination is available. Checking for other paths. Choose your destination on screen.");
                });
            try { while(steps.MoveNext())yield return steps.Current; }
            finally { (steps as IDisposable)?.Dispose();searchRoutine=null; }
        }
        void CancelSearch()
        {
            areaSearch?.Cancel();
            if(searchRoutine==null)return;
            var previous=searchRoutine;searchRoutine=null;StopCoroutine(previous);(previous as IDisposable)?.Dispose();
        }
        void CancelAreaSearch() { CancelSearch();Mode=Flow.Idle;ui.CloseModal();speech.Stop(); }
        void ScanNewArea() { CancelAreaSearch();controller.StartNewScan(); }
        public void ShowMapList()
        {
            if(BlocksMapActions) return;
            if(controller.CaptureInProgress || controller.CapturePending) { controller.ShowMessage("Stop scanning before loading another map.");return; }
            if(controller.HasUnsavedMap) { ui.UnsavedChanges(()=>ShowSaveDialog(OpenMapList),OpenMapList);return; }
            OpenMapList();
        }
        void OpenMapList()
        {
            try { ui.MapList(SpatialMapStorage.ListMaps(),controller.LoadMapById,DeleteSavedEntry,OpenMapList); }
            catch(Exception e) { controller.ShowMessage("Cannot list saved maps: "+e.Message); }
        }
        public void MapLoaded()
        {
            areaMatches.Clear();
            CancelRefinement();refinementDone=false;
            CancelSearch();handoff.Reset();handoff.Observe(controller.Map.Data);startVerification.Begin(controller.Map.Data,Time.realtimeSinceStartupAsDouble);startRequested=false;
            ready=false;recorder=null;arrows.Hide();speech.Stop();
            autoLocate=true;failedAttempts=0;waitingAttempt=false;nextAttempt=Time.realtimeSinceStartup+1;
            follower=controller.Map.Data.route==null?null:new SpatialRouteFollower(controller.Map.Data.route);
            ReadinessHint=follower==null?"Localize this map before preparing a navigation path.":"Map & Path to "+controller.Map.Data.route.destination+" loaded.";
            Mode=follower==null?Flow.Idle:Flow.Navigating;
            arrivedSpoken=startedSpoken=approachSpoken=turnSpoken=false;turnIndex=0;
            instruction="Look around the saved starting area to localize.";
            speech.Speak("Map loaded. Look around the saved starting area to localize.");
        }
        string DeleteSavedEntry(string id,bool pathOnly)
        {
            if(Mode!=Flow.Idle || controller.CaptureInProgress || controller.CapturePending)return "End navigation or recording before deleting a saved entry.";
            try
            {
                bool deleted=pathOnly?SpatialMapStorage.DeletePath(id):SpatialMapStorage.Delete(id);
                if(!deleted)return "This saved entry or path no longer exists. Cancel and reopen the list.";
                if(controller.ClearDeletedMap(id))
                {
                    CancelRefinement();CancelSearch();handoff.Reset();startVerification.Reset();areaMatches.Clear();
                    ready=autoLocate=waitingAttempt=startRequested=false;recorder=null;follower=null;destination=null;floorProbe.Reset();
                    arrows.Hide();speech.Stop();ReadinessHint="Load a saved map or scan a starting area.";
                }
                var message=pathOnly?"Path deleted. The scanned map is retained.":"Saved map and path deleted.";
                controller.ShowMessage(message);speech.Speak(message);
                SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=SavedEntryDeleted map="+id+" pathOnly="+pathOnly);
                return null;
            }
            catch(Exception e) { return "Could not delete the saved entry: "+e.Message; }
        }
        bool FindFloor(out float floor)
        {
            floor=0;
            var camera=controller.Origin?.Camera;if(camera==null)return false;
            if(planes==null)planes=controller.Origin.GetComponent<ARPlaneManager>()??controller.Origin.gameObject.AddComponent<ARPlaneManager>();
            if(!planes.enabled)planes.enabled=true;
            if((planes.requestedDetectionMode & PlaneDetectionMode.Horizontal)==0)
                planes.requestedDetectionMode|=PlaneDetectionMode.Horizontal;
            if(!floorProbe.TryFind(planes,camera,Time.realtimeSinceStartupAsDouble,out var point)
                || !controller.TryWorldToCapturePosition(point,out var mapFloor))return false;
            floor=mapFloor.y;return true;
        }
        void PreparePath()
        {
            if(BlocksMapActions || !handoff.Ready || !startVerification.Verified || !controller.CanContinueCapture) return;
            SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=BeginningNameDialog");
            ui.NameDialog("Name destination",destination??"",name=>
            {
                destination=name;Mode=Flow.Armed;instruction="Path to "+destination+". Stand at the starting point, then press Start.";
                controller.StopScan();
                startRequested=false;floorProbe.Reset();nextStartCheck=nextFloorTrace=nextStartSpeech=0;lastFloorWait=null;
                SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=PathArmed destination="+destination);
                speech.Speak("Stand at the starting point, then press Start.");return null;
            });
        }
        void StartPath()
        {
            if(Mode!=Flow.Armed)return;
            startRequested=true;
            if(Time.realtimeSinceStartup<nextStartCheck)return;
            nextStartCheck=Time.realtimeSinceStartup+.15f;
            if(controller.CapturePending) { instruction="Finishing the starting scan. Recording will start when ready.";return; }
            if(!controller.CanContinueCapture)
            {
                floorProbe.Reset();
                WaitForStart("Camera tracking or map alignment is unavailable. Look at the fixed details around your starting point to recover.");return;
            }
            if(!FindFloor(out floorY))
            { WaitForStart(floorProbe.Status??"Waiting for the floor detector.");return; }
            if((!controller.ResumeCurrentCapture() && !controller.ResumeNavigationCapture()) || !controller.TryCapturePosition(out var position))
            { instruction="Wait for camera tracking before starting.";return; }
            recorder=new SpatialRouteRecorder(destination,floorY);startHeight=position.y-floorY;
            recorder.Sample(position,Time.realtimeSinceStartupAsDouble,true);
            Mode=Flow.Recording;controller.Map.HideCaptureVisuals();
            startRequested=false;SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=FloorReady floor="+floorY+" height="+startHeight+" trackedPlanes="+floorProbe.TrackedPlanes);
            SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=PathRecording destination="+destination+" map="+controller.Map.Data.mapId);
            instruction="Walk to "+destination+" with the camera facing your surroundings. Press End at the destination.";
            speech.Speak("Path recording started. Walk slowly to "+destination+". Press End when you arrive.");
        }
        void WaitForStart(string message)
        {
            instruction=message+" Recording starts automatically when ready.";
            if(lastFloorWait!=message && Time.realtimeSinceStartup>=nextStartSpeech)
            { lastFloorWait=message;nextPrompt=0;nextStartSpeech=Time.realtimeSinceStartup+3; }
            Prompt(instruction);
            if(Time.realtimeSinceStartup<nextFloorTrace)return;
            nextFloorTrace=Time.realtimeSinceStartup+5;
            SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=StartWaiting reason="+message+" trackedPlanes="+floorProbe.TrackedPlanes+" horizontalPlanes="+floorProbe.HorizontalPlanes);
        }
        public void TrackingLost()
        {
            areaMatches.Clear();
            ready=false;floorProbe.Reset();
            if(handoff.Ready && controller.Map!=null)startVerification.Begin(controller.Map.Data,Time.realtimeSinceStartupAsDouble);
            speech.Stop();guidanceAligned=false;
            if(Mode==Flow.SearchingSaved) { areaSearch?.Cancel();ui.SearchDialog(ScanNewArea,CancelAreaSearch);ui.SearchProgress("Camera tracking lost. Look around, then choose Look again."); }
            if(Mode==Flow.Idle && handoff.Ready) { controller.StopScan();autoLocate=true;nextAttempt=Time.realtimeSinceStartup+2; }
            if(Mode==Flow.Recording)
            {
                recorder.Pause("Tracking lost. Return to the last recorded point.");BeginRecovery();
            }
            else if(Mode==Flow.Armed)
            { autoLocate=true;nextAttempt=Time.realtimeSinceStartup+2;instruction="Tracking lost. Look around the starting area to recover. Your destination name is retained.";controller.ShowMessage(instruction);speech.Speak(instruction); }
            if(Mode==Flow.Navigating) { follower.Suspend();arrows.Hide();nextAttempt=Time.realtimeSinceStartup+2;Prompt("Tracking lost. Stop and look around to localize again."); }
        }
        void BeginRecovery()
        {
            controller.PauseNavigationCapture();Mode=Flow.Recovering;waitingAttempt=false;failedAttempts=0;nextAttempt=Time.realtimeSinceStartup+2;
            instruction=recorder.Reason+" Look around to recover the map.";speech.Speak(instruction);
        }
        void End()
        {
            if(Mode==Flow.Navigating)
            { Mode=Flow.Idle;autoLocate=false;arrows.Hide();speech.Stop();controller.CancelNavigationLocalization();controller.ShowMessage("Navigation ended.");return; }
            if(Mode==Flow.Armed) { Mode=Flow.Idle;startRequested=false;speech.Stop();return; }
            if(Mode!=Flow.Recording && Mode!=Flow.Recovering) return;
            CancelRefinement();refinementDone=false;
            if(Mode==Flow.Recording && controller.TryCapturePosition(out var point)) recorder.Sample(point,Time.realtimeSinceStartupAsDouble,true,true);
            controller.CancelNavigationLocalization();controller.StopScan();Mode=Flow.Finalizing;autoLocate=false;instruction="Finishing the path. You can edit the destination name before saving.";
            SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=PathEnd points="+recorder.Route.points.Count);
        }
        void FinalizePath()
        {
            if(controller.CapturePending) return;
            var failure=refinement!=null || refinementDone?null:SpatialNavigationRoute.Validate(recorder.Route);
            if(failure==null && refinement==null && !refinementDone && !SpatialNavigationRoute.HasVisualCoverage(controller.Map.Data,recorder.Route))
                failure="Some of the path lacks visual detail. Return along the path slowly with the camera facing textured surroundings, then press End.";
            if(failure!=null)
            {
                Mode=Flow.Recovering;recorder.Pause(failure);instruction=failure;nextAttempt=float.PositiveInfinity;Prompt(failure);
                ui.UnfinishedPath(failure,()=>
                {
                    if(controller.ResumeCurrentCapture() && controller.TryCapturePosition(out var position) && recorder.Resume(position)) Mode=Flow.Recording;
                    else { controller.PauseNavigationCapture();nextAttempt=Time.realtimeSinceStartup+2; }
                },()=>
                {
                    recorder=null;Mode=Flow.Idle;ready=false;controller.ShowMessage("Path discarded. The scan is retained; save it with a map name.");
                    afterDialog=()=>ShowSaveDialog();
                });return;
            }
            if(!PrepareLandmarks())return;
            Mode=Flow.ConfirmingName;instruction="Keep or edit the destination name, then save Map & Path.";
            SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=EndNameDialog");
            ui.NameDialog("Save Map & Path",destination,SaveFinishedPath,()=>
            { Mode=Flow.Recovering;recorder.Pause("Path not saved. Press End to edit the name and save.");instruction=recorder.Reason;nextAttempt=float.PositiveInfinity; },"Save Map & Path");
            speech.Speak("Path finished. Keep or edit the destination name, then save map and path.");
        }
        bool PrepareLandmarks()
        {
            if(refinementDone)return true;
            if(refinement==null)
            {
                refinement=new SpatialLandmarkRefinement(controller.Map.Data);
                refinementSteps=refinement.Run();
                instruction="Preparing map and path. Your destination name can be edited next.";
                speech.Speak("Preparing map and path.");
            }
            try { if(refinementSteps.MoveNext())return false; }
            catch(Exception e)
            {
                // The refiner rolls back a failed commit; keep the existing save flow.
                UnityEngine.Debug.LogWarning("[SpatialAI Refinement] Original geometry retained: "+e.Message);
            }
            var report=refinement.Result;
            if(report.changed>0)controller.Map.InvalidateGeometryValidation();
            SpatialNavigationTrace.Record("[SpatialAI Refinement] map="+controller.Map.Data.mapId+" considered="+report.considered
                +" eligible="+report.eligible+" proposed="+report.proposed+" changed="+report.changed+" workMs="+report.workMs.ToString("F2")
                +" maxSliceMs="+report.maximumSliceMs.ToString("F2")+" validationMs="+report.validationMs.ToString("F2")+" elapsedMs="+report.elapsedMs.ToString("F2")
                +" budget="+report.budgetReached+" cancelled="+report.cancelled+" squaredBefore="+report.squaredErrorBefore.ToString("F3")
                +" squaredAfter="+report.squaredErrorAfter.ToString("F3")+" reason="+report.reason);
            (refinementSteps as IDisposable)?.Dispose();refinementSteps=null;refinement=null;refinementDone=true;
            return true;
        }
        void CancelRefinement()
        {
            bool wasRefining=refinementSteps!=null;
            refinement?.Cancel();(refinementSteps as IDisposable)?.Dispose();refinementSteps=null;refinement=null;
            // Pausing never publishes partial proposals or requires repeating End.
            if(Mode==Flow.Finalizing && wasRefining)refinementDone=true;
        }
        string SaveFinishedPath(string name)
        {
            var map=controller.Map.Data;var previousRoute=map.route;var previousName=map.displayName;var previousDestination=recorder.Route.destination;
            recorder.Route.destination=name;map.route=recorder.Route;if(string.IsNullOrWhiteSpace(map.displayName)) map.displayName=name;
            map.transientRevision++;
            if(!controller.TrySaveSpatialMap(out var failure))
            {
                map.route=previousRoute;map.displayName=previousName;recorder.Route.destination=previousDestination;map.transientRevision++;
                return failure+". The path is retained; retry Save Map & Path.";
            }
            destination=name;
            Mode=Flow.Idle;ready=false;ReadinessHint="Map & path to "+destination+" saved. Load it near its starting point to navigate.";
            controller.ShowMessage(ReadinessHint);speech.Speak("Map and path to "+destination+" saved.");
            SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=PathSaved map="+map.mapId+" destination="+destination+" points="+map.route.points.Count);
            recorder=null;return null;
        }
        public void MappingCompleted()
        {
            if(Mode!=Flow.Idle || controller.Map.Data.route!=null || controller.Map.LastMappingError!=null)return;
            if(handoff.Observe(controller.Map.Data))
            {
                controller.StopScan();ready=false;
                startVerification.Begin(controller.Map.Data,Time.realtimeSinceStartupAsDouble);
                autoLocate=true;waitingAttempt=false;failedAttempts=0;nextAttempt=Time.realtimeSinceStartup+.6f;
                ReadinessHint="Checking this starting area. Hold steady and look back at the fixed details you just scanned.";
                controller.ShowMessage(ReadinessHint);speech.Speak(ReadinessHint);
                SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=StartingAreaGeometryReady map="+controller.Map.Data.mapId+" frames="+controller.Map.KeyframeCount+" landmarks="+controller.Map.LandmarkCount);
            }
            else if(!handoff.Ready)ReadinessHint=handoff.Hint;
        }
        void LateUpdate()
        {
            if(paused || controller?.Map==null) return;
            if(afterDialog!=null && !ui.ModalOpen) { var action=afterDialog;afterDialog=null;action(); }
            float now=Time.realtimeSinceStartup;
            bool tracking=SpatialARTracking.TryGetPoseValidity(controller.Origin?.Camera,out _);
            if(Mode==Flow.Idle && !ui.ModalOpen && controller.Map.Data.route==null && now>=nextReadiness && !controller.CapturePending)
            {
                nextReadiness=now+1;
                MappingCompleted();
            }
            ready=handoff.Ready && startVerification.Verified && controller.CanContinueCapture;
            // The mapper may pause at the name/Start controls beyond the SDK's
            // visual-alignment lifetime. Recover automatically instead of leaving
            // Path disabled or a queued Start waiting forever.
            if(handoff.Ready && (Mode==Flow.Idle || Mode==Flow.Armed) && controller.Map.Data.route==null
                && !controller.CanContinueCapture && !autoLocate)
            { autoLocate=true;nextAttempt=now; }
            ui.SetPathReady(ready && controller.Map.Data.route==null && Mode==Flow.Idle && !ui.ModalOpen);
            if(Mode==Flow.Armed && startRequested)StartPath();
            if(Mode==Flow.Recording)
            {
                if(!controller.TryCapturePosition(out var position)) { recorder.Pause("Tracking lost. Return to the last recorded point.");BeginRecovery(); }
                else
                {
                    if(Mathf.Abs(position.y-floorY-startHeight)>.65f) recorder.Pause("This path supports one floor. Return to the last recorded point and keep the phone at the same height.");
                    recorder.Sample(position,Time.realtimeSinceStartupAsDouble,tracking);
                    if(recorder.Paused) BeginRecovery();
                    else if(controller.Map.KeyframeCount>=controller.Map.MaximumKeyframeCount) { End();speech.Speak("Map capacity reached. Saving the path recorded so far."); }
                    else instruction="Recording path to "+destination+". Keep the camera facing your surroundings.\nPress End at the destination.";
                }
            }
            if(Mode==Flow.Finalizing) FinalizePath();
            if(Mode==Flow.Recovering || Mode==Flow.Navigating || (autoLocate && (Mode==Flow.Idle || Mode==Flow.Armed))) UpdateLocalization(now,tracking);
            if(Mode==Flow.Navigating) Guide(tracking);
            if(Mode==Flow.Recovering && !ui.ModalOpen && !float.IsPositiveInfinity(nextAttempt) && controller.Session.TryGetMapPose(out var recovered))
            {
                if(Mathf.Abs(recovered.position.y-floorY-startHeight)<=.65f && recorder.Resume(recovered.position) && controller.ResumeNavigationCapture())
                { Mode=Flow.Recording;speech.Speak("Map recovered. Continue walking."); }
                else instruction="Map recovered. Return to within half a metre of the last recorded point to continue, or press End to save the recorded section.";
            }
            ui.SetFocus(Mode!=Flow.Idle && Mode!=Flow.SearchingSaved,instruction+(speech.Available?"":"\n"+speech.Status),Mode==Flow.Armed,Mode==Flow.Navigating?"End navigation":Mode==Flow.Armed?"Cancel":"End");
        }
        void UpdateLocalization(float now,bool tracking)
        {
            if(waitingAttempt && !controller.LocalizationBusy)
            {
                waitingAttempt=false;
                bool success=controller.Session.TryGetMapPose(out _);
                failedAttempts=success?0:Mathf.Min(failedAttempts+1,3);
                nextAttempt=now+RealignmentDelay(Mode,success,failedAttempts);
                if(success && (Mode==Flow.Idle || Mode==Flow.Armed))
                {
                    if(Mode==Flow.Idle && controller.Map.Data.route==null && handoff.Ready)
                    {
                        if(startVerification.Accept(controller.Map.Data,controller.LastLocalizationResult,controller.LastLocalizationReceipt))
                        {
                            autoLocate=false;
                            ReadinessHint="Enough details captured and starting area verified. Tap Navigation Path to continue.";
                            controller.ShowMessage(ReadinessHint);speech.Speak(ReadinessHint);
                            SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=StartingAreaReady map="+controller.Map.Data.mapId+" inliers="+controller.LastLocalizationResult.diagnostics.inlierCount+" error="+controller.LastLocalizationResult.diagnostics.reprojectionError);
                        }
                    }
                    else { autoLocate=false;speech.Speak("Map localized."); }
                }
                else if(!success)
                {
                    var result=controller.LastLocalizationResult;
                    var hint=result?.failureReason==SpatialFailureReason.NoFeatures
                        ? "Not enough visible detail. Hold the phone steady and look at fixed, textured surroundings."
                        : result?.failureReason==SpatialFailureReason.InvalidMap || result?.failureReason==SpatialFailureReason.NoMap
                        ? "This map could not be validated. Use Scan Environment to scan the starting point again."
                        : "Look back at the fixed details around the mapped starting point. Checking again shortly.";
                    if(Mode==Flow.Idle && handoff.Ready)
                    {
                        ReadinessHint=hint+(failedAttempts>=3?" If this area still cannot be verified, use Scan Environment to try the starting scan again.":"");
                        controller.ShowMessage(ReadinessHint);
                    }
                    Prompt(hint);
                    SpatialNavigationTrace.Record("[SpatialAI Navigation] stage=VisualCheckRetry mode="+Mode+" features="+(result?.diagnostics.queryFeatureCount ?? 0)+" reason="+result?.failureReason);
                }
            }
            if(now<nextAttempt || !tracking || controller.CapturePending || controller.CaptureInProgress || controller.LocalizationBusy
                || ui.ModalOpen) return;
            nextAttempt=now+4;waitingAttempt=true;controller.Localize();
        }
        public static float RealignmentDelay(Flow mode,bool success,int failedAttempts)
        {
            if(success) return mode==Flow.Navigating?8f:20f;
            return Mathf.Min(12,4*Mathf.Max(1,failedAttempts));
        }
        public static bool ShouldExpediteRealignment(bool tracking,bool arrived,bool localizationBusy,bool modalOpen)
            => tracking && !arrived && !localizationBusy && !modalOpen;
        void Guide(bool tracking)
        {
            SpatialMapPose pose=default;
            bool aligned=tracking && controller.Session.TryGetMapPose(out pose);
            if(!aligned)
            {
                if(guidanceAligned) { speech.Stop();nextPrompt=0;Prompt("Checking map alignment. Please pause."); }guidanceAligned=false;
                follower.Suspend();arrows.Hide();
                instruction=controller.LocalizationBusy?"Checking map alignment. Please pause.":"Stop and look around the mapped surroundings to localize.";
                if(!controller.LocalizationBusy) Prompt(instruction);
                // A lost SDK alignment must not wait for the periodic revalidation.
                // Expedite the next visual verification so walking resumes without stalling.
                if(follower!=null && ShouldExpediteRealignment(tracking,follower.Arrived,controller.LocalizationBusy,ui.ModalOpen))
                    nextAttempt=Mathf.Min(nextAttempt,Time.realtimeSinceStartup);
                return;
            }
            guidanceAligned=true;
            if(!follower.Update(pose.position,true))
            {
                arrows.Hide();instruction=follower.Started?"You are away from the recorded route. Return to the last route section.":"Go to the saved starting point to begin navigation.";
                Prompt(instruction);return;
            }
            if(follower.Arrived)
            {
                arrows.Hide();instruction="You have reached "+follower.Route.destination+".";
                if(!arrivedSpoken) { arrivedSpoken=true;speech.Speak(instruction); }
                autoLocate=false;nextAttempt=float.PositiveInfinity;return;
            }
            arrows.Render(follower,controller.Session);
            var direction=follower.PointAt(follower.Progress+1)-follower.PointAt(follower.Progress);
            float bearing=Vector3.SignedAngle(Vector3.ProjectOnPlane(pose.rotation*Vector3.forward,Vector3.up),direction,Vector3.up);
            if(!startedSpoken && Mathf.Abs(bearing)>60)
            { instruction="Turn "+(bearing<0?"left":"right")+" to face the route.";Prompt(instruction);return; }
            if(!startedSpoken) { startedSpoken=true;speech.Speak("Map localized. Follow the arrows to "+follower.Route.destination+"."); }
            while(turnIndex<follower.Turns.Count && follower.Progress>follower.Turns[turnIndex].distance+1)
            { turnIndex++;approachSpoken=turnSpoken=false; }
            instruction="Continue towards "+follower.Route.destination+". "+Mathf.CeilToInt(follower.Length-follower.Progress)+" metres remaining.";
            if(turnIndex<follower.Turns.Count)
            {
                var turn=follower.Turns[turnIndex];float remaining=turn.distance-follower.Progress;var side=turn.left?"left":"right";
                if(remaining<=.85f)
                { instruction="Turn "+side+".";if(!turnSpoken){turnSpoken=true;approachSpoken=true;speech.Speak(instruction);} }
                else if(remaining<=3)
                { instruction="In "+Mathf.CeilToInt(remaining)+" metres, turn "+side+".";if(!approachSpoken){approachSpoken=true;speech.Speak(instruction);} }
            }
        }
        void Prompt(string message)
        { if(Time.realtimeSinceStartup<nextPrompt)return;nextPrompt=Time.realtimeSinceStartup+12;speech.Speak(message); }
        void OnApplicationPause(bool value)
        {
            paused=value;
            if(value){CancelRefinement();TrackingLost();CancelSearch();arrows.Hide();speech.Stop();}
            else
            {
                waitingAttempt=false;nextAttempt=Time.realtimeSinceStartup+2;
                if(Mode==Flow.SearchingSaved)ui.SearchResult("Search paused. Look around and choose Look again.",null,controller.LoadMapById,ScanNewArea,LookAgainSavedArea,CancelAreaSearch);
            }
        }
        void OnDisable() { CancelRefinement();CancelSearch();arrows?.Hide();speech?.Stop(); }
    }
}
