using System;
using System.Collections.Generic;
namespace SpatialAI { [Serializable] public class SpatialFeatureTrack { public string trackId; public string landmarkId; public List<SpatialFeatureObservation> observations=new List<SpatialFeatureObservation>(); public float quality; } }
