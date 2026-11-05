namespace SpatialAI
{
    // Observe only completed mapping results. Readiness belongs to this map, not
    // the fleeting idle interval before the controller queues another frame.
    public sealed class SpatialScanHandoff
    {
        SpatialMapData source;
        public bool Ready { get; private set; }
        public string Hint { get; private set; }
        public void Reset() { source=null;Ready=false;Hint=null; }
        public bool Observe(SpatialMapData map)
        {
            if(!ReferenceEquals(source,map)) { source=map;Ready=false; }
            if(Ready) return false;
            Ready=SpatialScanReadiness.Evaluate(map,out var hint);Hint=hint;
            return Ready;
        }
    }
}
