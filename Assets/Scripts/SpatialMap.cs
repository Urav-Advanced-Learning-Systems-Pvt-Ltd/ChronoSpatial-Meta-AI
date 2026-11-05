using System; using System.Collections.Generic; using UnityEngine;
namespace SpatialAI {
 [Serializable] public class SpatialMapData { [NonSerialized] public int transientRevision; public string mapId="local-map"; public string displayName; public List<SpatialNavigationRoute> routes=new List<SpatialNavigationRoute>(); public SpatialNavigationRoute route { get => routes!=null && routes.Count>0 ? routes[0] : null; set { if(routes==null)routes=new List<SpatialNavigationRoute>();routes.Clear();if(value!=null)routes.Add(value); } } public int version=SpatialMapSerializer.CurrentVersion; public string createdAt=""; public string coordinateSystem="map-origin"; public SpatialPose mapOrigin; public List<SpatialKeyframe> keyframes=new List<SpatialKeyframe>(); public List<SpatialFeatureTrack> tracks=new List<SpatialFeatureTrack>(); public List<SpatialLandmark> landmarks=new List<SpatialLandmark>(); public List<SpatialPlace> places=new List<SpatialPlace>(); public List<string> semanticObjects=new List<string>(); public List<SemanticEntity3D> semanticEntities=new List<SemanticEntity3D>(); }
 public partial class SpatialMap : MonoBehaviour {
 SpatialCaptureGate captureGate = new SpatialCaptureGate();
 readonly BinaryFeatureMatcher mappingMatcher = new BinaryFeatureMatcher();
 readonly SpatialFeatureSet mappingQuery = new SpatialFeatureSet(), mappingReference = new SpatialFeatureSet();
 System.Threading.CancellationTokenSource mappingCancellation = new System.Threading.CancellationTokenSource();
 bool mappingActive;
 long matchingTicks, extensionTicks, triangulationTicks;
 static long MappingClock => System.Diagnostics.Stopwatch.GetTimestamp();
 static double MappingMilliseconds(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
 public void CancelMappingWork() { try { mappingCancellation?.Cancel(); } catch (ObjectDisposedException) { } CancelAsyncMapping(); }
 void ResetMappingWork() { CancelMappingWork(); mappingCancellation?.Dispose(); mappingCancellation = new System.Threading.CancellationTokenSource(); }
 public string LastLoadFailure { get; private set; }
 public int VisualFrameAttempts { get; private set; }
 public int VisualGateEvaluations { get; private set; }
 public Dictionary<string, int> MappingDecisionCounts { get; } = new Dictionary<string, int>();
 void CountMapping(string reason) { MappingDecisionCounts.TryGetValue(reason, out var count); MappingDecisionCounts[reason] = count + 1; }
 string MappingSummary() { var values = new List<string>(); foreach (var pair in MappingDecisionCounts) values.Add(pair.Key + "=" + pair.Value); values.Sort(StringComparer.Ordinal); return string.Join(";", values); }
 void TraceMapMutation(string operation)
 {
  Debug.Log($"[SpatialAI CaptureMap] operation={operation} component={GetEntityId()} map={Data.mapId}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Data)} keyframes={Data.keyframes.Count} visual={VisualKeyframeCount} poseOnly={PoseOnlyKeyframeCount} features={FeatureCount} tracks={TrackCount} landmarks={LandmarkCount} visualEntryCalls={VisualFrameAttempts} visualGateEvaluations={VisualGateEvaluations}");
 }
 public int VisualKeyframeCount => Data.keyframes.FindAll(keyframe => keyframe.features != null && keyframe.features.Count > 0).Count;
 public int PoseOnlyKeyframeCount => KeyframeCount - VisualKeyframeCount;
 public SpatialPose? PreviousVisualPose => captureGate.PreviousVisualPose;
 public int FeatureCount { get { var count=0; if(Data!=null&&Data.keyframes!=null) foreach(var keyframe in Data.keyframes) if(keyframe!=null&&keyframe.features!=null) count+=keyframe.features.Count; return count; } }
 public SpatialMapData Data{get;private set;}=NewMap(); public int PoseCount=>Data.keyframes.Count; public int KeyframeCount=>Data.keyframes.Count; public int LandmarkCount=>Data.landmarks.Count; public int TrackCount=>Data.tracks==null?0:Data.tracks.Count; public int MaximumKeyframeCount=200; public int ObservationsCreated{get;private set;} public int TriangulationAttempts{get;private set;} public int TriangulationSuccesses{get;private set;} public int TriangulationFailures{get;private set;} public string TriangulationFailureReason{get;private set;}=""; public int NewTracks{get;private set;} public int TrackExtensionAttempts{get;private set;} public string LastKeyframeDecision{get;private set;}="No candidate evaluated"; public SpatialPose LastAcceptedPose{get{if(Data.keyframes.Count==0)return new SpatialPose();return Data.keyframes[Data.keyframes.Count-1].cameraPose;}} public SpatialMapValidationResult Validation { get { if(cachedValidation==null||Time.realtimeSinceStartup>=nextValidation) { cachedValidation=SpatialMapValidationResult.Validate(Data);nextValidation=Time.realtimeSinceStartup+.5f; } return cachedValidation; } } public SpatialMapReadiness Readiness=>Validation.readiness; public float MinimumKeyframeInterval=.5f; public float MinimumKeyframeTranslation=.1f; public float MinimumKeyframeRotation=10f; LineRenderer trajectory; Transform markerRoot; Material lineMaterial; Material markerMaterial;
  SpatialMapValidationResult cachedValidation; float nextValidation;
  public void InvalidateGeometryValidation() { cachedValidation=null; }
  bool captureVisualsHidden;
  public void HideCaptureVisuals() { captureVisualsHidden = true; ClearVisuals(); }
  static void ReleaseVisual(UnityEngine.Object value) { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
  void OnDestroy() { CancelMappingWork(); mappingWorker?.Dispose(); samWorker?.Dispose(); mappingCancellation?.Dispose(); mappingCancellation = null; if (lineMaterial != null) ReleaseVisual(lineMaterial); if (markerMaterial != null) ReleaseVisual(markerMaterial); }
  static SpatialMapData NewMap()=>new SpatialMapData{mapId=Guid.NewGuid().ToString("N"),createdAt=DateTime.UtcNow.ToString("O")}; public void CreateMap(){TraceMapMutation("CreateMap-before");Clear();Data=NewMap();TraceMapMutation("CreateMap-after");} public void BeginCapture(){TraceMapMutation("BeginCapture-before");Clear();Data=NewMap();captureVisualsHidden=false;EnsureVisuals();trajectory.enabled=true;TraceMapMutation("BeginCapture-after");}
  public bool RecordPose(Transform source) => source != null && RecordPose(new SpatialPose(source.position, source.rotation, Time.realtimeSinceStartup), SpatialARTracking.TryGetPoseValidity(source.GetComponent<Camera>(), out _));
  public bool RecordPose(SpatialPose pose, bool trackingValid)
  {
   if (!MappingIdle) { LastKeyframeDecision = "Deferred: mapping busy"; return false; }
   if (Data.keyframes.Count >= MaximumKeyframeCount) { LastKeyframeDecision = "Rejected: maximum keyframe count reached"; return false; }
   if (!captureGate.AcceptPose(pose, trackingValid, Time.realtimeSinceStartupAsDouble, MaximumKeyframeCount, MinimumKeyframeInterval, MinimumKeyframeTranslation, MinimumKeyframeRotation, out var reason)) { LastKeyframeDecision = "Pose-only rejected: " + reason; return false; }
   Data.keyframes.Add(new SpatialKeyframe { keyframeId = "kf-" + Data.keyframes.Count.ToString("D5"), timestamp = pose.timestamp, cameraPose = pose });
   Data.transientRevision++;
   LastKeyframeDecision = "Accepted pose-only keyframe"; if(!captureVisualsHidden) { UpdateTrajectory(); CreateMarker(pose.position); } return true;
  }
  public bool RecordVisualFrame(SpatialFrame frame, SpatialFeatureSet features)
  {
   VisualFrameAttempts++;
   if (!MappingIdle || mappingActive || (mappingCancellation?.IsCancellationRequested ?? false)) { LastKeyframeDecision = "Rejected: mapping busy or cancelled"; return false; }
   if (frame == null || !frame.hasImage || frame.image == null || features == null || features.features == null || features.features.Count == 0 || features.features.Count > 300
       || features.imageWidth != frame.image.width || features.imageHeight != frame.image.height) { LastKeyframeDecision = "Rejected: image/features unavailable or dimensions mismatch"; return false; }
   var schema = SpatialFeatureSchemas.ForMap(Data) ?? features.features[0]?.descriptorType;
   foreach (var feature in features.features) if (feature != null && (!BinaryFeatureMatcher.SupportedSchema(feature.descriptorType) || feature.descriptorType != schema)) { LastKeyframeDecision = BinaryPatchDescriptor.IncompatibleMessage; return false; }
   var featureIds = new HashSet<string>();
   foreach (var feature in features.features) if (!BinaryFeatureMatcher.Valid(feature) || !featureIds.Add(feature.featureId) || feature.imagePoint.x < 0 || feature.imagePoint.y < 0 || feature.imagePoint.x >= features.imageWidth || feature.imagePoint.y >= features.imageHeight) { LastKeyframeDecision = "Rejected: invalid or duplicate feature"; return false; }
   if (Data.keyframes.Count >= MaximumKeyframeCount) { LastKeyframeDecision = "Rejected: maximum keyframe count reached"; return false; }
   VisualGateEvaluations++;
   if (!captureGate.AcceptVisual(frame, Time.realtimeSinceStartupAsDouble, MaximumKeyframeCount, MinimumKeyframeInterval, MinimumKeyframeTranslation, MinimumKeyframeRotation, out var reason)) { LastKeyframeDecision = FormatCaptureDecision(reason); return false; }
   var keyframe = new SpatialKeyframe { keyframeId = "kf-" + Data.keyframes.Count.ToString("D5"), timestamp = frame.Timestamp,
       cameraPose = frame.ImagePose, intrinsics = frame.intrinsics, imageWidth = features.imageWidth, imageHeight = features.imageHeight,
       sourceFrame = frame.source, features = features.features.ConvertAll(feature => new SpatialFeature { featureId = feature.featureId,
           imagePoint = feature.imagePoint, descriptor = (byte[])feature.descriptor.Clone(), descriptorSize = feature.descriptorSize,
           descriptorType = feature.descriptorType, quality = feature.quality }) };
   var candidates = Data.keyframes.FindAll(candidate => candidate.features != null && candidate.features.Count > 0);
   Data.keyframes.Add(keyframe); LastKeyframeDecision = "Accepted visual keyframe " + keyframe.keyframeId;
   BuildMappingPartners(candidates, keyframe);
   Data.transientRevision++;
   SpatialImageEvidence.Keyframe(Data, keyframe, frame.image);
   Debug.Log($"[SpatialAI Accepted] map={Data.mapId}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Data)} keyframe={keyframe.keyframeId} visual={VisualKeyframeCount} poseOnly={PoseOnlyKeyframeCount} features={keyframe.features.Count} imageTime={frame.imageTimestamp:F6} receiptTime={frame.receivedTimestamp:F6} position={frame.arPose.position:F4} rotation={frame.arPose.rotation:F4} imagePoseRotation={keyframe.cameraPose.rotation:F4} imagePoseSource={frame.source} tracks={TrackCount} observations={ObservationsCreated} landmarks={LandmarkCount} triangulations={TriangulationAttempts}/{TriangulationSuccesses}/{TriangulationFailures} lastTriangulationFailure={TriangulationFailureReason} mappingDecisions=[{MappingSummary()}]");
   UpdateTrajectory(); CreateMarker(frame.arPose.position); return true;
  }
  void BuildMappingPartners(List<SpatialKeyframe> candidates, SpatialKeyframe current)
  {
   // Retained-frame replay recovered a query-relevant track five frames apart.
   // Cover a small local scan while bounding comparisons for larger maps.
   const int maximumPartners = MappingPartnerWindow;
   if (mappingActive) throw new InvalidOperationException("Mapping work must not overlap");
   mappingActive = true; matchingTicks = extensionTicks = triangulationTicks = 0;
   var started = MappingClock; var visited = 0;
   try
   {
    for (var index = candidates.Count - 1; index >= Mathf.Max(0, candidates.Count - maximumPartners); index--)
    {
     if (mappingCancellation.IsCancellationRequested) break;
     BuildTracksAndLandmarks(candidates[index], current); visited++;
    }
   }
   finally
   {
    mappingActive = false;
    var observations = 0; foreach (var keyframe in Data.keyframes) observations += keyframe.observations.Count;
    Debug.Log($"[SpatialAI MappingPerformance] build={Application.buildGUID} frame={current.keyframeId} candidatePartners={Math.Min(candidates.Count, maximumPartners)} visitedPartners={visited} matchingMs={MappingMilliseconds(matchingTicks):F3} trackExtensionMs={MappingMilliseconds(extensionTicks):F3} triangulationMs={MappingMilliseconds(triangulationTicks):F3} totalMappingMs={MappingMilliseconds(MappingClock-started):F3} cancelled={mappingCancellation.IsCancellationRequested} execution=main-thread-synchronous arSessionState={UnityEngine.XR.ARFoundation.ARSession.state} frameFeatures={current.features.Count} frameObservations={current.observations.Count} mapFeatures={FeatureCount} landmarks={LandmarkCount} observations={observations}");
   }
  }
  void BuildTracksAndLandmarks(SpatialKeyframe reference, SpatialKeyframe current)
  {
   var matchingStarted = MappingClock;
   mappingQuery.features = current.features; mappingReference.features = reference.features;
   var matches = mappingMatcher.Match(mappingQuery, mappingReference);
   matchingTicks += MappingClock - matchingStarted;
   foreach (var step in ApplyMatches(reference, current, matches)) { }
  }
  System.Collections.Generic.IEnumerable<int> ApplyMatches(SpatialKeyframe reference, SpatialKeyframe current, List<SpatialFeatureMatch> matches)
  {
   foreach (var match in matches)
   {
    yield return 0; // Cooperative boundary before each single track/triangulation mutation.
    if (mappingCancellation.IsCancellationRequested) break;
    var extensionStarted = MappingClock;
    SpatialFeature queryFeature, mapFeature;
    try
    {
    TrackExtensionAttempts++;
    if (current.observations.Exists(observation => observation.featureId == match.queryFeatureId)) { CountMapping("CurrentFeatureAssociated"); continue; }
    queryFeature = current.features.Find(feature => feature.featureId == match.queryFeatureId);
    mapFeature = reference.features.Find(feature => feature.featureId == match.mapFeatureId);
    var previousObservation = reference.observations.Find(observation => observation.featureId == match.mapFeatureId);
    if (previousObservation != null)
    {
     var landmark = Data.landmarks.Find(candidate => candidate.landmarkId == previousObservation.landmarkId);
     var track = Data.tracks.Find(candidate => candidate.landmarkId == previousObservation.landmarkId);
     if (landmark == null || track == null) { CountMapping("ExtensionMissingReference"); continue; }
     if (track.observations.Exists(observation => observation.keyframeId == current.keyframeId)) { CountMapping("ExtensionDuplicateFrame"); continue; }
     var projection = PnPPoseSolver.Project(landmark.position, current.cameraPose.position, current.cameraPose.rotation, current.intrinsics);
     if (!projection.valid) { CountMapping("ExtensionInvalidProjection"); continue; }
     if (Vector2.Distance(projection.pixel, queryFeature.imagePoint) > SpatialTriangulator.MaximumReprojectionError) { CountMapping("ExtensionReprojection"); continue; }
     var added = new SpatialFeatureObservation { keyframeId = current.keyframeId, featureId = queryFeature.featureId,
         landmarkId = landmark.landmarkId, x = queryFeature.imagePoint.x, y = queryFeature.imagePoint.y };
     current.observations.Add(added); track.observations.Add(added); landmark.observedKeyframes.Add(current.keyframeId); ObservationsCreated++;
     CountMapping("ExtensionAccepted");
     continue;
    }
    }
    finally { extensionTicks += MappingClock - extensionStarted; }
    var triangulationStarted = MappingClock;
    try
    {
    TriangulationAttempts++;
    var triangulated = SpatialTriangulator.Triangulate(reference, mapFeature.imagePoint, current, queryFeature.imagePoint);
    if (!triangulated.success) { TriangulationFailures++; TriangulationFailureReason = triangulated.failureReason; CountMapping(triangulated.failureReason); continue; }
    var id = "lm-" + Data.landmarks.Count.ToString("D5");
    var first = new SpatialFeatureObservation { keyframeId = reference.keyframeId, featureId = mapFeature.featureId,
        landmarkId = id, x = mapFeature.imagePoint.x, y = mapFeature.imagePoint.y };
    var second = new SpatialFeatureObservation { keyframeId = current.keyframeId, featureId = queryFeature.featureId,
        landmarkId = id, x = queryFeature.imagePoint.x, y = queryFeature.imagePoint.y };
    reference.observations.Add(first); current.observations.Add(second); ObservationsCreated += 2;
    Data.tracks.Add(new SpatialFeatureTrack { trackId = "track-" + Data.tracks.Count.ToString("D5"), landmarkId = id,
        quality = match.score, observations = new List<SpatialFeatureObservation> { first, second } });
    Data.landmarks.Add(new SpatialLandmark { landmarkId = id, position = triangulated.position,
        confidence = Mathf.Clamp01(1f / (1f + triangulated.reprojectionError)),
        observedKeyframes = new List<string> { reference.keyframeId, current.keyframeId } });
    NewTracks++; TriangulationSuccesses++; CountMapping("TriangulationAccepted");
    }
    finally { triangulationTicks += MappingClock - triangulationStarted; }
   }
  }



  public void Clear(){ResetMappingWork();Data.transientRevision++;TraceMapMutation("Clear-before");trackingSegments.Clear();TrackingSegment=0;CrossSegmentMatchCount=0;LastMappingError=null;MaximumApplyMs=0;ApplyBudgetOverruns=0;MappingDecisionCounts.Clear();VisualFrameAttempts=0;VisualGateEvaluations=0;cachedValidation=null;captureGate=new SpatialCaptureGate();ObservationsCreated=0;TriangulationAttempts=0;TriangulationSuccesses=0;TriangulationFailures=0;NewTracks=0;TrackExtensionAttempts=0;LastKeyframeDecision="No candidate evaluated";Data.keyframes.Clear();Data.tracks.Clear();Data.landmarks.Clear();Data.semanticObjects.Clear();Data.places.Clear();ClearVisuals();} public void AddLandmark(string label){if(!string.IsNullOrWhiteSpace(label)&&!Data.semanticObjects.Contains(label))Data.semanticObjects.Add(label);} public void SaveMap(){if(!MappingIdle) throw new InvalidOperationException("Finalizing mapping; wait before saving");if(LastMappingError!=null)throw new InvalidOperationException("Mapping failed: "+LastMappingError);SpatialMapStorage.Save(Data);TraceMapMutation("SaveMap-complete");} public bool LoadMap(string id){var loaded=SpatialMapStorage.Load(id,out var loadReason);LastLoadFailure=loadReason;if(loaded==null)return false;AdoptLoadedMap(loaded);return true;}
  void AdoptLoadedMap(SpatialMapData loaded) {
   ResetMappingWork();TraceMapMutation("LoadMap-before");Data=loaded;
   trackingSegments.Clear();TrackingSegment=0;CrossSegmentMatchCount=0;LastMappingError=null;
   foreach(var k in Data.keyframes)trackingSegments[k.keyframeId]=0;
   captureGate=new SpatialCaptureGate();TraceMapMutation("LoadMap-after");cachedValidation=null;HideCaptureVisuals();
  }

  void ClearVisuals(){if(trajectory!=null){trajectory.positionCount=0;trajectory.enabled=false;}if(markerRoot!=null)for(var i=markerRoot.childCount-1;i>=0;i--)ReleaseVisual(markerRoot.GetChild(i).gameObject);}
  void EnsureVisuals(){if(trajectory==null){var o=new GameObject("Spatial Trajectory");o.transform.SetParent(transform,false);trajectory=o.AddComponent<LineRenderer>();trajectory.useWorldSpace=true;trajectory.widthMultiplier=.025f;trajectory.numCapVertices=4;trajectory.numCornerVertices=4;lineMaterial=new Material(Shader.Find("Sprites/Default"));lineMaterial.color=new Color(.1f,.9f,1f,1f);trajectory.sharedMaterial=lineMaterial;trajectory.startColor=lineMaterial.color;trajectory.endColor=lineMaterial.color;trajectory.sortingOrder=10;}if(markerRoot==null){markerRoot=new GameObject("Spatial Keyframes").transform;markerRoot.SetParent(transform,false);}}
  void UpdateTrajectory(){EnsureVisuals();trajectory.positionCount=Data.keyframes.Count;for(var i=0;i<Data.keyframes.Count;i++)trajectory.SetPosition(i,Data.keyframes[i].cameraPose.position);trajectory.enabled=Data.keyframes.Count>0;} void CreateMarker(Vector3 p){EnsureVisuals();var m=GameObject.CreatePrimitive(PrimitiveType.Sphere);m.name="Spatial Keyframe";m.transform.SetParent(markerRoot,true);m.transform.position=p;m.transform.localScale=Vector3.one*.06f;var r=m.GetComponent<Renderer>();if(markerMaterial==null){markerMaterial=new Material(lineMaterial);markerMaterial.color=new Color(1f,.7f,.1f,1f);}r.sharedMaterial=markerMaterial;var c=m.GetComponent<SphereCollider>();if(c!=null)ReleaseVisual(c);}
 }
}
