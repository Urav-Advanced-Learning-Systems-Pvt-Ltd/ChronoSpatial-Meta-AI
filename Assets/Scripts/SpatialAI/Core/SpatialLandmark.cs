using System; using System.Collections.Generic; using UnityEngine;
namespace SpatialAI { [Serializable] public class SpatialLandmark { public string landmarkId; public Vector3 position; public float confidence; public string semanticLabel; public List<string> observedKeyframes = new List<string>(); } }
