namespace SpatialAI
{
    // Geometry is only a prerequisite. A later, independently acquired image must
    // actually localize against the same unchanged map before authoring a route.
    public sealed class SpatialStartVerification
    {
        SpatialMapData source;
        int revision;
        double after;
        public bool Verified { get; private set; }
        public void Reset() { source=null;Verified=false; }
        public void Begin(SpatialMapData map,double now)
        { source=map;revision=map.transientRevision;after=now;Verified=false; }
        public bool Accept(SpatialMapData map,SpatialLocalizationResult result,double receipt)
        {
            if(source==null || !ReferenceEquals(source,map) || revision!=map.transientRevision
                || !SpatialFrameGeometry.Finite(receipt) || receipt<=after
                || !SpatialSavedAreaSearch.Verified(map.mapId,result))return false;
            Verified=true;return true;
        }
    }
}
