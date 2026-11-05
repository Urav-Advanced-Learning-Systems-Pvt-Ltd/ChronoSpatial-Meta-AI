using System; using System.Collections.Generic; using UnityEngine;
namespace SpatialAI {
 [Serializable] public class SpatialFeature { public string featureId; public Vector2 imagePoint; public byte[] descriptor; public int descriptorSize; public string descriptorType; public float quality; }
 [Serializable] public class SpatialFeatureSet { public int imageWidth; public int imageHeight; public List<SpatialFeature> features=new List<SpatialFeature>(); }
 [Serializable] public class SpatialFeatureMatch { public string queryFeatureId; public string mapFeatureId; public float score; }
 public interface ISpatialFeatureExtractor { SpatialFeatureSet Extract(Texture2D frame); }
 public interface ISpatialFeatureMatcher { List<SpatialFeatureMatch> Match(SpatialFeatureSet query, SpatialFeatureSet reference); }
}
