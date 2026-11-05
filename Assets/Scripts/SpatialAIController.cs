using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using System.Text;
using System.Linq;
using System.Collections;

namespace SpatialAI {
    public class SpatialAIController : MonoBehaviour {
        public enum ScanState { Idle, Scanning, ScanComplete, MapLoaded, Searching, Localized, Tracking, TrackingLost, PausedNativeLost, Finalizing }
        public ScanState State { get; private set; }
        XROrigin origin; SpatialMap spatialMap; Text statusText; float nextSample;
        SpatialSDKSession sdkSession; ISpatialFrameProvider frameProvider;
        public SpatialRuntimeStatus RuntimeStatus { get; } = new SpatialRuntimeStatus();
        readonly SpatialCaptureHint captureHint = new SpatialCaptureHint();
        public bool CapturePending => spatialMap != null && !spatialMap.MappingIdle;
        public bool CaptureInProgress => State == ScanState.Scanning || State == ScanState.PausedNativeLost || State == ScanState.Finalizing;
        Button saveControl;
        double acquisitionMilliseconds;
        void ClearCaptureMessages() { captureFailure = "None"; captureHint.Clear(); spatialMap?.ClearCaptureHint(); }
        void OnMappingCompleted(string decision)
        {
            if (State == ScanState.Scanning) captureHint.Note(decision, Time.realtimeSinceStartupAsDouble);
            if (spatialMap?.LastMappingError != null)
            {
                StopScan(); SetStatus("Mapping stopped: " + spatialMap.LastMappingError + ". Start a new scan.");
            }
            else Navigation?.MappingCompleted();
        }
        public SpatialMap Map => spatialMap;
        public SpatialSDKSession Session => sdkSession;
        public XROrigin Origin => origin;
        public bool CaptureOriginValid { get; private set; }
        public SpatialNavigationController Navigation { get; private set; }
        Matrix4x4 captureFromWorld = Matrix4x4.identity;
        bool resumedCaptureAligned;
        public bool LocalizationBusy => activeLocalization != null || (sdkSession?.LocalizationRunning ?? false);
        public bool HasUnsavedMap => !mapSaved && spatialMap != null && spatialMap.KeyframeCount > 0;
        public bool CanContinueCapture => SpatialARTracking.TryGetPoseValidity(origin?.Camera,out _)
            && (CaptureOriginValid || resumedCaptureAligned || (sdkSession?.TryGetMapPose(out _) ?? false));
        public bool TryWorldToCapturePosition(Vector3 world,out Vector3 position)
        {
            position=default;
            if(!SpatialARTracking.TryGetPoseValidity(origin?.Camera,out _))return false;
            if(CaptureOriginValid || resumedCaptureAligned) { position=captureFromWorld.MultiplyPoint3x4(world);return true; }
            return sdkSession!=null && sdkSession.TryWorldToMap(world,out position);
        }
        public bool TryAcquireSearchFrame(out SpatialFrame frame)
        {
            frame=null;
            if(CaptureInProgress || CapturePending || LocalizationBusy || !SpatialARTracking.TryGetPoseValidity(origin?.Camera,out _))return false;
            if(frameProvider==null)frameProvider=FindAnyObjectByType<ARFoundationFrameProvider>();
            return frameProvider!=null && frameProvider.TryAcquireFrame(out frame,out _,out _);
        }
        public void ShowMessage(string message) { SetStatus(message); }
        public bool TryCapturePosition(out Vector3 position)
        {
            position=default;
            if ((!CaptureOriginValid && !resumedCaptureAligned) || !SpatialARTracking.TryGetPoseValidity(origin?.Camera,out _)) return false;
            position=captureFromWorld.MultiplyPoint3x4(origin.Camera.transform.position);return true;
        }
        public void PauseNavigationCapture()
        {
            StopScan(); resumedCaptureAligned=false; CaptureOriginValid=false;
        }
        public bool ResumeCurrentCapture()
        {
            if(CapturePending || (!CaptureOriginValid && !resumedCaptureAligned) || !SpatialARTracking.TryGetPoseValidity(origin?.Camera,out _)) return false;
            State=ScanState.Scanning;RuntimeStatus.BeginScan();nextSample=0;mapSaved=false;return true;
        }
        public bool ResumeNavigationCapture()
        {
            if(CapturePending || !SpatialARTracking.TryGetPoseValidity(origin?.Camera,out _)
                || !sdkSession.TryGetCaptureTransform(out captureFromWorld)) return false;
            resumedCaptureAligned=true; CaptureOriginValid=false; State=ScanState.Scanning;
            RuntimeStatus.BeginScan(); RuntimeStatus.InvalidateAlignment();
            (frameProvider as ARFoundationFrameProvider)?.ResetFrameHistory();nextSample=0;
            spatialMap.HideCaptureVisuals();mapSaved=false;return true;
        }
        float nextDiagnosticsTime;
        int scanRequestCount;
        int lifetimeRuntimeUpdates;
        int framePollingAttempts;
        int trackingValidFrames;
        int starvationWatchAttempts;
        int starvationWatchAcquired;
        string captureStage = "Not sampled";
        double extractionMilliseconds = double.NaN;
        double lastReceiptAge = double.NaN;
        SpatialMapData scanTargetMap;
        SpatialMapData lastCapturedMap;
        float lastIdleTrace;
        int visualCandidates;
        string captureFailure = "Not sampled";
        string eventMessage="Ready to scan"; int runtimeFrameCount; int processedFrameCount; int poseSampleCount; float lastScanLogTime;
        bool mapSaved;
        IEnumerator activeLocalization;
        public SpatialLocalizationResult LastLocalizationResult { get; private set; }
        public double LastLocalizationReceipt { get; private set; }
        void CancelLocalization()
        {
            sdkSession?.StopLocalization();
            if (activeLocalization == null) return;
            var cancelled = activeLocalization;
            activeLocalization = null;
            StopCoroutine(cancelled);
            (cancelled as System.IDisposable)?.Dispose();
        }
        public void PlaceMarked() { mapSaved=false; SetStatus("Place marked — Save Map to persist it."); }
        public void CancelNavigationLocalization() { CancelLocalization();RuntimeStatus.InvalidateAlignment(); }
        void Awake() {
            SpatialARInputSetup.EnsureInputManager(FindAnyObjectByType<ARSession>());
            if (origin == null) origin = GetComponent<XROrigin>();
            SpatialARInputSetup.RepairLegacyCameraBindings(origin == null ? null : origin.Camera);
            if (spatialMap == null) spatialMap = FindAnyObjectByType<SpatialMap>();
            sdkSession = new SpatialSDKSession();
            frameProvider = FindAnyObjectByType<ARFoundationFrameProvider>();
            if (statusText == null) { var statusObject = GameObject.Find("Scan Status"); if (statusObject != null) statusText = statusObject.GetComponent<Text>(); }
#if UNITY_EDITOR
            if (origin != null && origin.Camera != null && origin.Camera.GetComponent<SpatialSimulationCameraController>() == null) origin.Camera.gameObject.AddComponent<SpatialSimulationCameraController>();
#endif
            if (GetComponent<SpatialPlaceUI>() == null) gameObject.AddComponent<SpatialPlaceUI>();
            var layout = GetComponent<SpatialReferenceLayout>();
            if (layout == null) layout = gameObject.AddComponent<SpatialReferenceLayout>();
            layout.Configure(statusText);
            TraceCaptureEvent("Awake", "controller initialized");
            SetStatus("Ready to scan");
            var scanButton = GameObject.Find("Scan Environment")?.GetComponent<Button>(); if (scanButton != null) { scanButton.onClick.RemoveListener(ScanEnvironment); scanButton.onClick.AddListener(ScanEnvironment); }
            var stopButton = GameObject.Find("Stop")?.GetComponent<Button>(); if (stopButton != null) { stopButton.onClick.RemoveListener(StopScan); stopButton.onClick.AddListener(StopScan); }
            var saveButton = GameObject.Find("Save Map")?.GetComponent<Button>(); saveControl = saveButton; if (saveButton != null) { saveButton.onClick.RemoveListener(SaveSpatialMap); saveButton.onClick.AddListener(SaveSpatialMap); }
            var loadButton = GameObject.Find("Load Spatial Map")?.GetComponent<Button>(); if (loadButton != null) { loadButton.onClick.RemoveListener(LoadSpatialMap); loadButton.onClick.AddListener(LoadSpatialMap); }
            // Localize button removed: auto-localize in SpatialNavigationController covers retries.
            var askButton = GameObject.Find("Ask Spatial AI")?.GetComponent<Button>(); if (askButton != null) { askButton.onClick.RemoveListener(AskSpatialAI); askButton.onClick.AddListener(AskSpatialAI); var label = askButton.GetComponentInChildren<Text>(); if (label != null) label.text = "Ask AI"; }
            if(Application.isPlaying) Navigation=gameObject.AddComponent<SpatialNavigationController>();
        }
        void OnEnable() {
            ARSession.stateChanged += OnSessionStateChanged;
            if (spatialMap != null) spatialMap.MappingCompleted += OnMappingCompleted;
        }
        void OnDisable()
        {
            TraceCaptureEvent("OnDisable", "controller disabled");
            ARSession.stateChanged -= OnSessionStateChanged;
            if (spatialMap != null) spatialMap.MappingCompleted -= OnMappingCompleted;
            spatialMap?.CancelMappingWork(); RuntimeStatus.InvalidateAlignment();
            CancelLocalization();
            StopScan(); spatialMap?.HideCaptureVisuals(); CaptureOriginValid = false; sdkSession?.InvalidateAlignment("Controller disabled");
        }
        void OnSessionStateChanged(ARSessionStateChangedEventArgs args)
        {
            TraceCaptureEvent("ARSessionChanged", args.state.ToString());
            PollNativeTracking();
        }
        void PollNativeTracking()
        {
            var native = SpatialARTracking.TryGetPoseValidity(origin?.Camera, out _)
                ? SpatialRuntimeStatus.NativeStatus.Tracking
                : ARSession.state == ARSessionState.SessionTracking ? SpatialRuntimeStatus.NativeStatus.Limited
                : SpatialRuntimeStatus.NativeStatus.NotTracking;
            SetNativeTracking(native);
        }
        void SetNativeTracking(SpatialRuntimeStatus.NativeStatus native)
        {
            var previous = RuntimeStatus.Native;
            if (previous == native) return;
            var segment = RuntimeStatus.TrackingSegment;
            RuntimeStatus.SetNative(native);
            Debug.Log($"[SpatialAI Tracking] native={previous}->{native} reason={ARSession.notTrackingReason} scan={RuntimeStatus.Scan} alignment={RuntimeStatus.Alignment}");
            if (native != SpatialRuntimeStatus.NativeStatus.Tracking)
            {
                CancelLocalization(); CaptureOriginValid = false; resumedCaptureAligned=false; sdkSession?.NotifySessionReset();
                spatialMap?.HideCaptureVisuals();
                if (State == ScanState.Scanning) State = ScanState.PausedNativeLost;
                ClearCaptureMessages();
                (frameProvider as ARFoundationFrameProvider)?.ResetFrameHistory();
                Navigation?.TrackingLost();
            }
            else
            {
                if (RuntimeStatus.TrackingSegment != segment) spatialMap?.NotifyTrackingRecovered();
                if (State == ScanState.PausedNativeLost) { State = ScanState.Scanning; nextSample = 0; }
                else if (State == ScanState.TrackingLost) State = sdkSession?.LoadedMap != null ? ScanState.MapLoaded : ScanState.ScanComplete;
                ClearCaptureMessages();
                SetStatus(previous == SpatialRuntimeStatus.NativeStatus.Unknown ? "Ready to scan" : "AR tracking recovered. Saved-map alignment requires visual localization.");
            }
        }
        void OnApplicationPause(bool paused)
        {
            TraceCaptureEvent("ApplicationPause", paused.ToString());
            if (!paused) { PollNativeTracking(); return; }
            SetNativeTracking(SpatialRuntimeStatus.NativeStatus.NotTracking);
            spatialMap?.CancelMappingWork();
            CancelLocalization();
            StopScan(); spatialMap?.HideCaptureVisuals(); CaptureOriginValid = false; sdkSession?.NotifySessionReset();
            if (sdkSession?.LoadedMap != null) State = ScanState.TrackingLost;
            (frameProvider as ARFoundationFrameProvider)?.ResetFrameHistory();
        }
        public void SetReferences(XROrigin sceneOrigin, SpatialMap sceneMap, Text status) { origin = sceneOrigin; spatialMap = sceneMap; statusText = status; if (sdkSession == null) sdkSession=new SpatialSDKSession(); frameProvider=FindAnyObjectByType<ARFoundationFrameProvider>(); State = ScanState.Idle; SetStatus("Ready to scan\nMap: PoseOnly"); }
        public void ScanEnvironment() {
            if(Navigation!=null && Navigation.BlocksMapActions) return;
            if(Navigation!=null && HasUnsavedMap && !CaptureInProgress) { Navigation.OfferSaveBeforeScan();return; }
            if(Navigation!=null && !CaptureInProgress) { Navigation.FindSavedArea();return; }
            StartNewScan();
        }
        internal void StartNewScan() {
            scanRequestCount++; TraceCaptureEvent("ScanRequest", "button invoked controller");
            if (State == ScanState.Scanning || State == ScanState.PausedNativeLost) { StopScan(); return; }
            if (CapturePending) { SetStatus("Finalizing mapping; wait before starting a new scan"); return; }
            if (spatialMap == null) { captureFailure="Spatial map unavailable"; TraceCaptureEvent("ScanRejected", captureFailure); SetStatus(captureFailure); return; }
            var camera = origin == null ? null : origin.Camera;
            if (camera == null) { captureFailure="AR camera unavailable"; TraceCaptureEvent("ScanRejected", captureFailure); SetStatus(captureFailure); return; }
            if (ARSession.state != ARSessionState.SessionTracking) { captureFailure="Wait for AR tracking before scanning"; TraceCaptureEvent("ScanRejected", captureFailure); SetStatus(captureFailure); return; }
            PollNativeTracking();
            if (RuntimeStatus.Native != SpatialRuntimeStatus.NativeStatus.Tracking) { SetStatus("Wait for valid camera tracking before scanning"); return; }
            RuntimeStatus.BeginScan(); ClearCaptureMessages();
            CaptureOriginValid = true; mapSaved=false;
            resumedCaptureAligned=false; captureFromWorld=Matrix4x4.identity;
            CancelLocalization();
            sdkSession.UnloadMap(); spatialMap.BeginCapture(); visualCandidates=0; (frameProvider as ARFoundationFrameProvider)?.ResetFrameHistory(true); State = ScanState.Scanning; scanTargetMap=spatialMap.Data;lastCapturedMap=null;framePollingAttempts=0;trackingValidFrames=0;captureFailure="None";captureStage="ScanStarted";TraceCaptureEvent("ScanStarted", "new capture map"); nextSample = 0f; runtimeFrameCount=0;processedFrameCount=0;poseSampleCount=0;lastScanLogTime=-1f;SetStatus("Scanning...");
            Navigation?.NewScan();
        }
        public void StopScan() {
            if (State != ScanState.Scanning && State != ScanState.PausedNativeLost) return;
            TraceCaptureEvent("StopScan", "capture stopping; accepted mapping work will drain");
            ClearCaptureMessages(); RuntimeStatus.Stop(CapturePending);
            State = CapturePending ? ScanState.Finalizing : ScanState.ScanComplete;
            SetStatus(CapturePending ? "Finalizing mapping..." : "Scan complete");
        }
        public void SaveSpatialMap() { if(Navigation!=null) Navigation.ShowSaveDialog();else TrySaveSpatialMap(out _); }
        internal string SaveNamedMap(string name)
        {
            if(spatialMap==null || !SpatialNavigationRoute.ValidName(name)) return "Enter a valid map name";
            var previous=spatialMap.Data.displayName;spatialMap.Data.displayName=name.Trim();
            if(TrySaveSpatialMap(out var failure)) return null;
            spatialMap.Data.displayName=previous;return failure;
        }
        internal bool TrySaveSpatialMap(out string failure)
        {
            failure = null;
            if (CaptureInProgress || CapturePending) failure = "Stop scanning and wait for mapping to finish before saving";
            else if (spatialMap == null || spatialMap.KeyframeCount == 0) failure = "Scan the environment before saving a map";
            if (failure != null) { SetStatus(failure); return false; }
            try
            {
                spatialMap.SaveMap(); mapSaved = true;
                SetStatus($"Map saved: {SpatialMapStorage.DisplayName(spatialMap.Data)}\nReadiness: {spatialMap.Readiness}");
                return true;
            }
            catch (System.Exception exception) { failure = "Save failed: " + exception.Message; SetStatus(failure); return false; }
        }
        public void LoadSpatialMap()
        {
            if(Navigation!=null) { Navigation.ShowMapList();return; }
            LoadMapById("local-map");
        }
        internal string LoadMapById(string id)
        {
            if (CaptureInProgress || CapturePending) return "Stop scanning and wait for mapping to finish before loading";
            if (spatialMap == null) return "Spatial map unavailable";
            try
            {
                CancelLocalization();
                if (!spatialMap.LoadMap(id)) return spatialMap.LastLoadFailure ?? "Map could not be loaded";
                CaptureOriginValid = false; resumedCaptureAligned=false; mapSaved=true; RuntimeStatus.Loaded(); ClearCaptureMessages(); sdkSession.LoadMap(spatialMap.Data);
                State = ScanState.MapLoaded; SetStatus("Map loaded; overlays hidden until visual localization. Readiness: " + spatialMap.Readiness);
                Navigation?.MapLoaded();return null;
            }
            catch (System.Exception exception) { var message="Load failed: " + exception.Message;SetStatus(message);return message; }
        }
        public void Localize() {
            if (activeLocalization != null || (sdkSession != null && sdkSession.LocalizationRunning)) { SetStatus("Localizing"); return; }
            if (CaptureInProgress || CapturePending) { SetStatus("Stop scanning and wait for mapping to finish before localization"); return; }
            if (sdkSession == null) sdkSession=new SpatialSDKSession();
            if (spatialMap != null && sdkSession.LoadedMap != spatialMap.Data) sdkSession.LoadMap(spatialMap.Data);
            RuntimeStatus.BeginLocalization();
            sdkSession.StartLocalization();
            CaptureOriginValid=false; resumedCaptureAligned=false;LastLocalizationResult=null;SetStatus("Choosing a detailed camera view. Hold the phone steady.");
            if (frameProvider == null) frameProvider=FindAnyObjectByType<ARFoundationFrameProvider>();
            if (frameProvider == null) { RuntimeStatus.EndLocalization(false, "Camera image provider unavailable"); State=ScanState.Searching; SetStatus("Localization failed\nCameraImageUnavailable: no AR Foundation frame provider."); return; }
            State = ScanState.Searching;
            activeLocalization = RunLocalization();
            StartCoroutine(activeLocalization);
        }
        IEnumerator RunLocalization()
        {
            SpatialLocalizationResult result = null;
            SpatialFrame frame=null;
            IEnumerator steps=null;
            var window=new SpatialQueryWindow();
            var source=spatialMap?.Data;var revision=source?.transientRevision ?? 0;
            double started=Time.realtimeSinceStartupAsDouble,next=started;
            int acquired=0;
            string detail="No fresh tracked camera image. Look around the mapped surroundings.";
            try
            {
                // Up to three independent camera images, at most 1.2 seconds.
                // Winners older than the SDK's 0.5-second freshness limit expire.
                yield return null;
                while(acquired<3 && Time.realtimeSinceStartupAsDouble-started<1.2)
                {
                    double now=Time.realtimeSinceStartupAsDouble;
                    if(source!=spatialMap?.Data || source?.transientRevision!=revision)yield break;
                    if(now>=next)
                    {
                        next=now+.12;
                        if(frameProvider.TryAcquireFrame(out var candidate,out _,out var message))
                        {
                            acquired++;
                            try
                            {
                                if(SpatialFrameGeometry.Fresh(candidate,Time.realtimeSinceStartupAsDouble,out _) && candidate.hasImage && candidate.image!=null)
                                {
                                    var features=sdkSession.ExtractQuery(candidate);
                                    Debug.Log($"[SpatialAI QuerySelection] candidate={acquired} features={features?.features?.Count ?? 0} score={SpatialQueryWindow.Score(features)}");
                                    window.Offer(candidate,features,Time.realtimeSinceStartupAsDouble);candidate=null;
                                }
                            }
                            catch(System.Exception e) { detail="Camera detail extraction failed: "+e.Message; }
                            finally { SpatialQueryWindow.Release(candidate); }
                        }
                        else detail=message;
                    }
                    if(acquired<3)yield return null;
                }
                frame=window.Take(Time.realtimeSinceStartupAsDouble,out var selected);
                if(frame==null)
                {
                    RuntimeStatus.EndLocalization(false,detail);SetStatus(detail);yield break;
                }
                Debug.Log($"[SpatialAI QuerySelection] selectedFeatures={selected.features.Count} frames={acquired} age={Time.realtimeSinceStartupAsDouble-frame.receivedTimestamp:F3}");
                steps=sdkSession.LocalizePreparedRoutine(frame,selected,value=>result=value);
                while (steps.MoveNext()) yield return steps.Current;
                LastLocalizationResult=result;LastLocalizationReceipt=frame.receivedTimestamp;
                TraceLocalizationResult(result, frame);
                RuntimeStatus.EndLocalization(result.success, result.failureReason + ": " + result.message);
                State = result.success ? ScanState.Localized : ScanState.Searching;
                if (result.success) CaptureOriginValid = false;
                SetStatus(result.success ? "Localization succeeded" : $"Localization failed\n{result.failureReason}: {result.message}");
            }
            finally
            {
                (steps as System.IDisposable)?.Dispose();
                window.Dispose();SpatialQueryWindow.Release(frame);
                activeLocalization = null;
            }
        }
        public void AskSpatialAI() { if(Navigation!=null && Navigation.BlocksMapActions) return; FindAnyObjectByType<SpatialNavigationUI>()?.ShowAskDialog(); }
        void LateUpdate() {
            lifetimeRuntimeUpdates++;
            PollNativeTracking();
            RuntimeStatus.Drain(!CapturePending);
            if (State == ScanState.Finalizing && !CapturePending) { State = ScanState.ScanComplete; ClearCaptureMessages(); SetStatus("Scan complete"); }
            if (saveControl != null) saveControl.interactable = !CaptureInProgress && !CapturePending && spatialMap != null && spatialMap.KeyframeCount > 0;
            // Localize gating removed with the button; Localize() still guards busy/capture internally.
            if (State != ScanState.Scanning && Time.realtimeSinceStartup - lastIdleTrace >= 1)
            { lastIdleTrace = Time.realtimeSinceStartup; TraceCaptureEvent("Heartbeat", "not scanning"); }
            if (sdkSession != null && State != ScanState.Scanning && origin != null && origin.Camera != null && sdkSession.TrackingState == SpatialTrackingState.Tracking) {
                sdkSession.UpdateTracking(new SpatialPose(origin.Camera.transform.position,origin.Camera.transform.rotation,Time.realtimeSinceStartup),SpatialARTracking.TryGetPoseValidity(origin.Camera, out _));
            }
            SynchronizeAlignmentStatus();
            // Navigation schedules bounded attempts; each Localize call remains one job.
            RefreshDiagnostics();
            if (State == ScanState.Scanning && spatialMap == null && Time.realtimeSinceStartup - lastIdleTrace >= 1)
            { lastIdleTrace = Time.realtimeSinceStartup; TraceCaptureEvent("MapUnavailable", "scanning state has no capture map; polling cannot proceed"); }
            if (State != ScanState.Scanning || spatialMap == null) return;
            var camera = origin == null ? null : origin.Camera;
            if (camera == null) { spatialMap?.CancelMappingWork(); State = ScanState.ScanComplete; SetStatus("Scan stopped: AR camera unavailable"); return; }
            runtimeFrameCount++;
            if (CapturePending) { captureHint.Note("Deferred: mapping busy", Time.realtimeSinceStartupAsDouble); return; }
            if (Time.realtimeSinceStartup < nextSample) return;
            nextSample = Time.realtimeSinceStartup + .2f;
            framePollingAttempts++; extractionMilliseconds=double.NaN;lastReceiptAge=double.NaN;captureStage="FramePoll";
            if (frameProvider == null) frameProvider = camera.GetComponent<ARFoundationFrameProvider>();
            var pose = new SpatialPose(camera.transform.position, camera.transform.rotation, Time.realtimeSinceStartup);
            var previous = spatialMap.PreviousVisualPose;
            var tracking = SpatialARTracking.TryGetPoseValidity(origin.Camera, out var poseInputStatus);
            poseSampleCount++;
            var currentFeatures = "N/A";
            var imageTimestamp = "N/A";
            var poseTimestamp = pose.timestamp.ToString("F3");
            var cameraIdentity = camera.name + "#" + camera.GetEntityId();
            var decision = "No visual candidate";
            var acquired = false;
            var reason = SpatialFailureReason.CameraImageUnavailable;
            var message = "Provider unavailable";
            SpatialFrame frame = null;
            try
            {
                var acquireStarted = System.Diagnostics.Stopwatch.StartNew();
                acquired = frameProvider != null && frameProvider.TryAcquireFrame(out frame, out reason, out message);
                acquisitionMilliseconds = acquireStarted.Elapsed.TotalMilliseconds;
                if (acquired)
                {
                    var snapProvider = frameProvider as ARFoundationFrameProvider;
                    if (snapProvider != null) { starvationWatchAttempts = snapProvider.AcquisitionAttempts; starvationWatchAcquired = snapProvider.ImageFramesAcquired; }
                    if(resumedCaptureAligned)
                    {
                        frame.arPose=TransformCapturePose(frame.arPose);
                        if(frame.hasImagePose) frame.imagePose=TransformCapturePose(frame.imagePose);
                    }
                    processedFrameCount++;
                    if (frame.trackingValid) trackingValidFrames++;
                    imageTimestamp = frame.imageTimestamp.ToString("F6");
                    poseTimestamp = frame.receivedTimestamp.ToString("F3");
                    cameraIdentity = frame.cameraIdentity;
                    visualCandidates++;
                    captureStage="QueueVisualFrame"; lastCapturedMap=spatialMap.Data;
                    lastReceiptAge=Time.realtimeSinceStartupAsDouble-frame.receivedTimestamp;
                    spatialMap.QueueVisualFrame(frame);
                    decision = spatialMap.LastKeyframeDecision;
                    captureHint.Note(decision, Time.realtimeSinceStartupAsDouble);
                    captureStage="VisualQueueReturned";
                    captureFailure = decision.StartsWith("Rejected:") ? decision : "None";
                }
                else
                {
                    captureStage="ProviderRejected"; captureFailure = reason + ": " + message;
                    captureHint.Note(reason == SpatialFailureReason.NoFrame ? "Skipped: acquisition interval" : captureFailure, Time.realtimeSinceStartupAsDouble);
                    var starveProvider = frameProvider as ARFoundationFrameProvider;
                    if (starveProvider != null && reason == SpatialFailureReason.CameraImageUnavailable
                        && CameraStarved(starveProvider.AcquisitionAttempts, starveProvider.ImageFramesAcquired, starvationWatchAttempts, starvationWatchAcquired))
                    {
                        int gap = starveProvider.AcquisitionAttempts - starvationWatchAttempts;
                        starvationWatchAttempts = starveProvider.AcquisitionAttempts;
                        starvationWatchAcquired = starveProvider.ImageFramesAcquired;
                        Debug.LogWarning("[SpatialAI Camera] STARVED while scanning: no new camera image in " + gap +
                            " attempts (acquired " + starveProvider.ImageFramesAcquired + "/" + starveProvider.AcquisitionAttempts +
                            ", stage=" + starveProvider.LastStage + ", failure=" + starveProvider.LastFailure +
                            "). ARCore is not delivering frames (check Unity-ARCore hardware-buffer errors); SLAM, SAM and boxes are all starved. Force-stop the app and scan again.");
                    }
                    if (reason == SpatialFailureReason.CameraImageUnavailable && tracking)
                    {
                        spatialMap.RecordPose(resumedCaptureAligned ? TransformCapturePose(pose) : pose, true);
                        decision = spatialMap.LastKeyframeDecision;
                    }
                }
            }
            catch (System.Exception exception) { captureFailure = exception.GetType().Name + ": " + exception.Message; captureHint.Note(captureFailure, Time.realtimeSinceStartupAsDouble); }
            finally { if (frame != null && frame.image != null) Destroy(frame.image); }
            if (Time.realtimeSinceStartup - lastScanLogTime >= 1f)
            {
                lastScanLogTime = Time.realtimeSinceStartup;
                var provider = frameProvider as ARFoundationFrameProvider;
                var distance = previous.HasValue ? Vector3.Distance(pose.position, previous.Value.position).ToString("F3") : "N/A";
                var angle = previous.HasValue ? Quaternion.Angle(pose.rotation, previous.Value.rotation).ToString("F2") : "N/A";
                var tag = $"[SpatialAI Scan] poll={framePollingAttempts}";
                Debug.Log($"{tag} buildGUID={Application.buildGUID} controller={GetEntityId()} state={State} sdkState={sdkSession?.TrackingState} runtimeFrames={runtimeFrameCount} processed={processedFrameCount} acquired={provider?.ImageFramesAcquired}/{provider?.AcquisitionAttempts} converted={provider?.ImageFramesConverted}/{provider?.ConversionAttempts} conversionFailures={provider?.ImageConversionFailures} providerStage={provider?.LastStage} stage={captureStage} trackingValidFrames={trackingValidFrames} permission={CameraPermission()}");
                Debug.Log($"{tag} camera={cameraIdentity} originCamera={origin.Camera.name}#{origin.Camera.GetEntityId()} worldPosition={pose.position:F4} worldRotation={pose.rotation:F4} localPosition={camera.transform.localPosition:F4} localRotation={camera.transform.localRotation:F4} parent={camera.transform.parent?.name} parentPosition={camera.transform.parent?.position} originPosition={origin.transform.position} originRotation={origin.transform.rotation} tracking={ARSession.state} {poseInputStatus}");
                Debug.Log($"{tag} nativeSeconds={imageTimestamp} realtimeSeconds={poseTimestamp} clocks=XRCpuImage/UnityRealtime receiptAge={Metric(lastReceiptAge)} acquisitionMs={acquisitionMilliseconds:F3} enqueueMs={spatialMap.LastEnqueueMs:F3} maxApplyMs={spatialMap.MaximumApplyMs:F3} applyOverruns={spatialMap.ApplyBudgetOverruns} extractionMs={Metric(extractionMilliseconds)} extracted={currentFeatures} geometry={provider?.ImageGeometry} intrinsics={(frame==null?"N/A":$"{frame.intrinsics.fx:F2},{frame.intrinsics.fy:F2},{frame.intrinsics.cx:F2},{frame.intrinsics.cy:F2}")} previousPosition={(previous.HasValue?previous.Value.position.ToString("F4"):"N/A")} previousRealtime={(previous.HasValue?previous.Value.timestamp.ToString("F3"):"N/A")} translation={distance}/{spatialMap.MinimumKeyframeTranslation} rotation={angle}/{spatialMap.MinimumKeyframeRotation} interval={spatialMap.MinimumKeyframeInterval} first={!previous.HasValue}");
                Debug.Log($"{tag} captureMap={MapIdentity(lastCapturedMap)} uiMap={MapIdentity(spatialMap.Data)} scanTarget={MapIdentity(scanTargetMap)} sameMap={(lastCapturedMap==null?"N/A":ReferenceEquals(lastCapturedMap,spatialMap.Data).ToString())} visualEntries={spatialMap.VisualFrameAttempts} gateEvaluations={spatialMap.VisualGateEvaluations} visual={spatialMap.VisualKeyframeCount} poseOnly={spatialMap.PoseOnlyKeyframeCount} maximum={spatialMap.MaximumKeyframeCount} decision={decision} failure={captureFailure}");
            }
            SetStatus($"Scanning: {spatialMap.VisualKeyframeCount} visual / {spatialMap.PoseOnlyKeyframeCount} pose-only keyframes");
        }

        static string Metric(double value) => SpatialFrameGeometry.Finite(value) ? value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "N/A";

        // Camera-starvation tripwire predicate, kept pure for checks: while
        // scanning, attempts advancing with zero new images means the device
        // delivers no frames (ARCore/gralloc failure such as ENOMEM -12),
        // never a SAM/SLAM tuning bug. SLAM, SAM and boxes are all starved.
        public const int CameraStarvationAttemptGap = 25;
        public static bool CameraStarved(int acquisitionAttempts, int imagesAcquired, int watchAttempts, int watchAcquired)
        {
            return acquisitionAttempts - watchAttempts >= CameraStarvationAttemptGap
                && imagesAcquired == watchAcquired;
        }
        SpatialPose TransformCapturePose(SpatialPose pose) => new SpatialPose(captureFromWorld.MultiplyPoint3x4(pose.position),captureFromWorld.rotation*pose.rotation,pose.timestamp);
        void TraceLocalizationResult(SpatialLocalizationResult result, SpatialFrame frame)
        {
            var d = sdkSession.Diagnostics; var l = result.diagnostics;
            var tag = $"[SpatialAI Localize] imageTime={frame.imageTimestamp:F6}";
            Debug.Log($"{tag} buildGUID={Application.buildGUID} map={MapIdentity(spatialMap?.Data)} readiness={d.mapReadiness} frames={d.keyframes} features={d.features} tracks={d.tracks} landmarks={d.landmarks} acquired={d.frameAvailable} image={d.imageWidth}x{d.imageHeight} intrinsics={d.intrinsicsAvailable} queryAttempted={d.featureExtractionAttempted} queryFeatures={l.queryFeatureCount} candidates={l.candidateKeyframeCount}");
            Debug.Log($"{tag} rawCandidates={(l.rawMatchCountAvailable?l.rawMatchCount.ToString():"N/A")} distanceRejected={d.distanceRejected} ratioRejected={d.ratioRejected} ambiguousRejected={d.ambiguousRejected} invalidFeatureInputs={d.invalidRejected} duplicateRejected={d.duplicateRejected} returnedMatches={l.acceptedMatchCount} observationMatches={l.observationMatchCount} trackMatches={l.trackMatchCount} landmarkBackedMatches={l.landmarkBackedMatchCount} associationDuplicates={l.associationDuplicates} geometryRejected={l.geometryRejected} invalidReferences={l.invalidReferences} uniqueCorrespondences={l.correspondenceCount}");
            Debug.Log($"{tag} solverAttempted={d.poseSolverAttempted} inliers={l.inlierCount} inlierRatio={(l.correspondenceCount>0?Metric(l.inlierCount/(double)l.correspondenceCount):"N/A")} reprojectionPixels={Metric(l.reprojectionError)} success={result.success} poseValid={result.pose.valid} alignmentValid={d.mapAlignmentValid} sdkState={sdkSession.TrackingState} visualAgeSeconds={Metric(sdkSession.LastVisualVerificationAge)} failure={result.failureReason} detail={result.message}");
            if (result.pose.valid) Debug.Log($"{tag} mapPosition={result.pose.position:F5} mapRotation={result.pose.rotation:F5} arPosition={frame.arPose.position:F5} arRotation={frame.arPose.rotation:F5}");
        }
        static string MapIdentity(SpatialMapData data) => data == null ? "N/A" : data.mapId + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(data);
        static string CameraPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera).ToString();
#else
            return "N/A";
#endif
        }
        void TraceCaptureEvent(string phase, string detail)
        {
            Debug.Log($"[SpatialAI Capture] schema=run3 event={phase} detail={detail} buildGUID={Application.buildGUID} appId={Application.identifier} appVersion={Application.version} controller={GetEntityId()} state={State} sdkState={sdkSession?.TrackingState.ToString() ?? "N/A"} scanRequests={scanRequestCount} runtimeUpdates={lifetimeRuntimeUpdates} framePollingAttempts={framePollingAttempts} mapComponent={(spatialMap==null?"N/A":spatialMap.GetEntityId().ToString())} uiMap={MapIdentity(spatialMap?.Data)} scanTargetMap={MapIdentity(scanTargetMap)} lastCaptureMap={MapIdentity(lastCapturedMap)} keyframes={(spatialMap==null?"N/A":spatialMap.KeyframeCount.ToString())} trackingState={ARSession.state} cameraPermission={CameraPermission()} lastFailure={captureFailure}");
        }
        void SetStatus(string message) { eventMessage=message; RefreshDiagnostics(); }
        void SynchronizeAlignmentStatus()
        {
            if (CaptureOriginValid || RuntimeStatus.Alignment != SpatialRuntimeStatus.AlignmentStatus.VisuallyVerified) return;
            if (sdkSession != null && sdkSession.TryGetMapPose(out _)) return;
            RuntimeStatus.InvalidateAlignment();
            State = sdkSession?.LoadedMap != null ? ScanState.MapLoaded : ScanState.ScanComplete;
            eventMessage = "Map alignment is no longer valid. Look around to re-localize - checking again automatically.";
        }
        internal bool ClearDeletedMap(string id)
        {
            if(spatialMap?.Data.mapId!=id)return false;
            CancelLocalization();sdkSession?.UnloadMap();
            spatialMap.CreateMap();spatialMap.HideCaptureVisuals();
            CaptureOriginValid=false;resumedCaptureAligned=false;mapSaved=false;
            LastLocalizationResult=null;LastLocalizationReceipt=0;RuntimeStatus.Loaded();
            State=ScanState.Idle;ClearCaptureMessages();return true;
        }
        string BuildLocalizationDiagnostics(SpatialLocalizationResult result){var d=sdkSession.Diagnostics;var l=result.diagnostics;var pose=result.pose;var ratio=l.correspondenceCount>0?l.inlierCount/(float)l.correspondenceCount:float.NaN;var observations=spatialMap==null?0:spatialMap.Data.keyframes.FindAll(k=>k!=null&&k.observations!=null).ConvertAll(k=>k.observations.Count).Sum();return $"===== SPATIALAI LOCALIZATION DIAGNOSTICS =====\nSDK state: {sdkSession.TrackingState}\nTracking state: {d.trackingState}\nMap ID: {d.mapId}\nMap readiness: {d.mapReadiness}\nReadiness reason: {d.mapReason}\n\nFRAME\nFrame available: {d.frameAvailable}\nImage: {d.imageWidth}x{d.imageHeight}\nIntrinsics available: {d.intrinsicsAvailable}\nIntrinsics reason: {d.intrinsicsReason}\n\nSCAN/MAP BUILDING\nKeyframes: {d.keyframes}\nFeatures: {d.features}\nFeature observations: {observations}\nTracks: {d.tracks}\nTrack match attempts: {d.trackExtensionAttempts}\nNew tracks: {d.newTracks}\nObservations created: {d.observationsCreated}\nTriangulation attempts: {d.triangulationAttempts}\nTriangulation successes: {d.triangulationSuccesses}\nTriangulation failures: {d.triangulationFailures}\nTriangulation reason: {d.triangulationFailureReason}\nLandmarks: {d.landmarks}\n\nQUERY LOCALIZATION\nQuery features: {l.queryFeatureCount}\nCandidate keyframes: {l.candidateKeyframeCount}\nRaw query matches: {l.rawMatchCount}\nRatio rejected: {d.ratioRejected}\nDistance rejected: {d.distanceRejected}\nInvalid rejected: {d.invalidRejected}\nAmbiguous rejected: {d.ambiguousRejected}\nDuplicate rejected: {d.duplicateRejected}\nAccepted query matches: {l.acceptedMatchCount}\nLandmark-backed matches: {l.landmarkBackedMatchCount}\n2D-to-3D correspondences: {l.correspondenceCount}\nInliers: {l.inlierCount}\nInlier ratio: {ratio:0.000}\nReprojection error (pixels): {(float.IsInfinity(l.reprojectionError)?"N/A":l.reprojectionError.ToString("F3"))}\nHeuristic quality (not probability): {result.confidence:0.000}\nMapPose valid: {pose.valid}\nMapPose position: {pose.position}\nMapPose rotation: {pose.rotation}\nFailure reason: {result.failureReason}: {result.message}\n===== END SPATIALAI LOCALIZATION DIAGNOSTICS =====";}
        void RefreshDiagnostics()
        {
            if(statusText==null || Time.realtimeSinceStartup<nextDiagnosticsTime) return;
            nextDiagnosticsTime=Time.realtimeSinceStartup+.25f;
            SynchronizeAlignmentStatus();
            var tracking=SpatialARTracking.TryGetPoseValidity(origin?.Camera,out _);
            var aligned=tracking && (CaptureOriginValid || (sdkSession!=null && sdkSession.TryGetMapPose(out _)));
            var state=State==ScanState.Scanning ? "Mapping" : State==ScanState.PausedNativeLost ? "Scan paused: camera tracking unavailable; map retained"
                : State==ScanState.Finalizing ? "Finalizing mapping..." : State==ScanState.TrackingLost ? "Map alignment lost"
                : aligned && !CaptureOriginValid ? "Map alignment from prior visual verification"
                : sdkSession!=null && sdkSession.LocalizationRunning ? "Localizing"
                : State==ScanState.MapLoaded ? "Map loaded — not localized" : mapSaved ? "Map saved — not localized"
                : State==ScanState.ScanComplete ? "Mapping stopped" : "Not localized";
            var d=sdkSession==null?new SpatialLocalizationDiagnostics():sdkSession.Diagnostics.localization;
            var age=sdkSession==null?double.PositiveInfinity:sdkSession.LastVisualVerificationAge;
            var b=new StringBuilder(); b.AppendLine(state);
            b.AppendLine("AR tracking: " + RuntimeStatus.Native + (RuntimeStatus.Native == SpatialRuntimeStatus.NativeStatus.Tracking ? "" : " (" + ARSession.notTrackingReason + ")"));
            b.AppendLine("Map alignment: " + (RuntimeStatus.Alignment == SpatialRuntimeStatus.AlignmentStatus.CaptureOrigin ? "current scan origin (not visual localization)"
                : RuntimeStatus.Alignment == SpatialRuntimeStatus.AlignmentStatus.VisuallyVerified ? "prior visual verification" : RuntimeStatus.Alignment.ToString()));
            b.AppendLine("Localization: " + RuntimeStatus.Localization);
            b.AppendLine("Map: " + (spatialMap == null || spatialMap.KeyframeCount == 0 ? "no scan" : SpatialMapStorage.DisplayName(spatialMap.Data) + (mapSaved ? " (saved)" : " (unsaved)")));
            if(Navigation!=null && !string.IsNullOrEmpty(Navigation.ReadinessHint)) b.AppendLine(Navigation.ReadinessHint);
            if (spatialMap?.LastMappingError != null) b.AppendLine("Mapping error: " + spatialMap.LastMappingError);
            b.AppendLine("Build: "+Application.buildGUID);
            b.AppendLine($"Visual keyframes: {spatialMap?.VisualKeyframeCount ?? 0}   Landmarks: {spatialMap?.LandmarkCount ?? 0}");
            b.AppendLine($"Last attempt — Correspondences: {d.correspondenceCount}   Inliers: {d.inlierCount}");
            b.AppendLine("Reprojection: "+(d.inlierCount>0?d.reprojectionError.ToString("F2")+" px":"N/A"));
            b.AppendLine("Alignment valid: "+aligned);
            b.AppendLine("Last visual validation: "+(double.IsInfinity(age)?"never":age.ToString("F1")+" s ago"));
            b.AppendLine("Entry point available: "+(spatialMap!=null && (spatialMap.Data.places.Exists(p=>p.name==SpatialPlaceUI.EntryPointPlaceName)||spatialMap.Data.places.Exists(p=>p.name=="Main Destination")||spatialMap.Data.places.Exists(p=>p.name=="Demo Place"))));
            if (State != ScanState.PausedNativeLost && State != ScanState.Finalizing) b.AppendLine(eventMessage);
            var hint = captureHint.Text(Time.realtimeSinceStartupAsDouble, State == ScanState.Scanning);
            if (!string.IsNullOrEmpty(hint)) b.AppendLine(hint);
            b.AppendLine($"Pending: {spatialMap?.PendingMappingJobs ?? 0}  Worker: {spatialMap?.LastWorkerMs:F1} ms  Apply: {spatialMap?.LastApplyMs:F1} ms  Acquire: {acquisitionMilliseconds:F1} ms");
            if(!aligned && RuntimeStatus.Localization == SpatialRuntimeStatus.LocalizationStatus.Failed && !string.IsNullOrEmpty(RuntimeStatus.LocalizationFailure)) b.AppendLine("Reason: "+RuntimeStatus.LocalizationFailure);
            if(aligned && !CaptureOriginValid) b.AppendLine("AR tracking propagates the last visual alignment.");
            statusText.text=b.ToString();
        }
    }
}
