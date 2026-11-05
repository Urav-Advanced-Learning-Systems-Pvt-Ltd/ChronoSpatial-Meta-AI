using System;
using UnityEngine;

namespace SpatialAI
{
    // Owns at most one camera image. Feature count and image coverage rank views;
    // they never substitute for geometric pose validation. Stale winners expire.
    public sealed class SpatialQueryWindow : IDisposable
    {
        public SpatialFrame Frame { get; private set; }
        public SpatialFeatureSet Features { get; private set; }
        float score;
        public static float Score(SpatialFeatureSet features)
        {
            if(features?.features==null || features.imageWidth<=0 || features.imageHeight<=0)return -1;
            int cells=0,count=0;
            foreach(var f in features.features)
            {
                if(!BinaryFeatureMatcher.Valid(f) || f.imagePoint.x<0 || f.imagePoint.y<0
                    || f.imagePoint.x>=features.imageWidth || f.imagePoint.y>=features.imageHeight)continue;
                int x=(int)(f.imagePoint.x*3/features.imageWidth),y=(int)(f.imagePoint.y*3/features.imageHeight);
                cells|=1<<(y*3+x);count++;
            }
            int covered=0;for(int i=0;i<9;i++)if((cells&(1<<i))!=0)covered++;
            return count+covered*12;
        }
        public void Offer(SpatialFrame frame,SpatialFeatureSet features,double now)
        {
            if(Frame!=null && !SpatialFrameGeometry.Fresh(Frame,now,out _))Dispose();
            float candidate=Score(features);
            if(frame==null || !SpatialFrameGeometry.Fresh(frame,now,out _) || candidate<0
                || (Frame!=null && candidate<score)) { Release(frame);return; }
            Dispose();Frame=frame;Features=features;score=candidate;
        }
        public SpatialFrame Take(double now,out SpatialFeatureSet features)
        {
            if(Frame!=null && !SpatialFrameGeometry.Fresh(Frame,now,out _))Dispose();
            var result=Frame;features=Features;Frame=null;Features=null;return result;
        }
        public static void Release(SpatialFrame frame)
        { if(frame?.image!=null) { if(Application.isPlaying)UnityEngine.Object.Destroy(frame.image);else UnityEngine.Object.DestroyImmediate(frame.image); } }
        public void Dispose() { Release(Frame);Frame=null;Features=null;score=0; }
    }
}
