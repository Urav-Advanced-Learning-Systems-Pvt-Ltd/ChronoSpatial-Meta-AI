using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SpatialAI
{
    // Opt-in, local, bounded image evidence. Never contributes solver evidence.
    public static class SpatialImageEvidence
    {
        [Serializable] public sealed class LandmarkQuery
        {
            public string schema = "spatialai-landmark-query-v1", buildGuid, mapId, mapCreatedAt, conventions, imagePoseEvidence, clockDomain, cameraIdentity;
            public double imageTimestamp, receivedTimestamp;
            public bool hasImagePose;
            public int minimumCorrespondences;
            public SpatialCameraIntrinsics intrinsics;
            public SpatialPose imagePose, arPose;
            public SpatialFeatureSet query;
            public SpatialLocalizationCandidates candidates;
        }
        const string Request = "capture-image-evidence.request";
        const string Token = "bounded-image-evidence-v1";
        static string directory;
        static double deadline;
        static int keyframes, queries;
        static long bytes;
        static bool Active()
        {
            if (directory != null) return Time.realtimeSinceStartupAsDouble < deadline;
            var request = Path.Combine(SpatialLocalizationReplay.DirectoryPath, Request);
            if (!File.Exists(request)) return false;
            if (new FileInfo(request).Length > 64 || File.ReadAllText(request).Trim() != Token) return false;
            File.Delete(request);
            directory = Path.Combine(SpatialLocalizationReplay.DirectoryPath, "image-evidence-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); deadline=Time.realtimeSinceStartupAsDouble+90;
            Write("IMAGE_LAYOUT.txt",Encoding.UTF8.GetBytes("Native feature pixels: x right, y indexes CPU row order. Unity GetPixels32 index is y*width+x. PNG decoded top-down row = height-1-featureY; flip PNG vertically before drawing feature pixels. Intrinsics scale matches texture size. No image resampling or distortion correction. Limits: 32 accepted keyframes, 3 queries, 90 seconds, 64 MiB total. Local diagnostic only.\nBuild="+Application.buildGUID));
            Debug.Log("[SpatialAI ImageEvidence] armed directory="+directory);
            return true;
        }
        static void Write(string name,byte[] data)
        {
            if(bytes+data.Length>64L*1024*1024) throw new IOException("Image evidence byte limit");
            using(var file=new FileStream(Path.Combine(directory,name),FileMode.CreateNew,FileAccess.Write,FileShare.None)) file.Write(data,0,data.Length);
            bytes+=data.Length;
        }
        static void Json(string name,object value) => Write(name,Encoding.UTF8.GetBytes(JsonUtility.ToJson(value,true)));
        public static void Keyframe(SpatialMapData map,SpatialKeyframe keyframe,Texture2D image)
        {
            try
            {
                if(!Active() || keyframes>=32) return;
                var prefix="keyframe-"+(keyframes++).ToString("D2");
                Write(prefix+".png",image.EncodeToPNG()); Json(prefix+".json",keyframe);
                Write(prefix+"-map.txt",Encoding.UTF8.GetBytes(map.mapId+"\n"+map.createdAt));
            }
            catch(Exception e) { deadline=0; Debug.LogWarning("[SpatialAI ImageEvidence] stopped: "+e.GetType().Name); }
        }
        public static void KeyframePixels(SpatialMapData map, SpatialKeyframe keyframe, Color32[] pixels)
        {
            Texture2D image = null;
            try
            {
                if (!Active() || keyframes >= 32) return;
                image = new Texture2D(keyframe.imageWidth, keyframe.imageHeight, TextureFormat.RGBA32, false);
                image.SetPixels32(pixels); image.Apply();
                Keyframe(map, keyframe, image);
            }
            catch (Exception e) { deadline = 0; Debug.LogWarning("[SpatialAI ImageEvidence] stopped: " + e.GetType().Name); }
            finally
            {
                if (image != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(image); else UnityEngine.Object.DestroyImmediate(image); }
            }
        }
        public static void Query(SpatialMapData map,SpatialFrame frame,SpatialFeatureSet query,List<SpatialKeyframe> candidates,SpatialSDKConfiguration configuration,SpatialMatchingReport matching)
        {
            try
            {
                if(!Active() || queries>=3) return;
                var prefix="query-"+(queries++).ToString("D2");
                Write(prefix+".png",frame.image.EncodeToPNG());
                Write(prefix+"-replay.json",Encoding.UTF8.GetBytes(SpatialLocalizationReplay.Serialize(SpatialLocalizationReplay.Create(query,candidates,map,frame,configuration,matching))));
                Write(prefix+"-map.json",Encoding.UTF8.GetBytes(SpatialMapSerializer.Serialize(map)));
                Debug.Log($"[SpatialAI ImageEvidence] query={prefix} correspondences={matching.correspondences.Count} keyframeImages={keyframes} bytes={bytes}");
            }
            catch(Exception e) { deadline=0; Debug.LogWarning("[SpatialAI ImageEvidence] stopped: "+e.GetType().Name); }
        }
        public static void QueryCandidates(SpatialMapData map, SpatialFrame frame, SpatialFeatureSet query, SpatialLocalizationCandidates candidates, int minimumCorrespondences)
        {
            try
            {
                if (!Active() || queries >= 3) return;
                var prefix = "query-" + (queries++).ToString("D2");
                Write(prefix + ".png", frame.image.EncodeToPNG());
                Json(prefix + "-landmark-query.json", new LandmarkQuery { buildGuid = Application.buildGUID, mapId = map.mapId,
                    mapCreatedAt = map.createdAt, imageTimestamp = frame.imageTimestamp, intrinsics = frame.intrinsics,
                    imagePose = frame.ImagePose, arPose = frame.arPose, query = query, candidates = candidates,
                    receivedTimestamp = frame.receivedTimestamp, imagePoseEvidence = frame.imagePoseEvidence,
                    clockDomain = frame.clockDomain, cameraIdentity = frame.cameraIdentity, hasImagePose = frame.hasImagePose,
                    minimumCorrespondences = minimumCorrespondences,
                    conventions = "native CPU pixels x-right/y-down; Unity optical +z-forward/+y-up; map metres; v=cy-fy*y/z; PNG vertically flipped for native rows" });
                Write(prefix + "-map.json", Encoding.UTF8.GetBytes(SpatialMapSerializer.Serialize(map)));
                Debug.Log($"[SpatialAI ImageEvidence] landmarkQuery={prefix} candidates={candidates.correspondences.Count} keyframeImages={keyframes} bytes={bytes}");
            }
            catch (Exception e) { deadline = 0; Debug.LogWarning("[SpatialAI ImageEvidence] stopped: " + e.GetType().Name); }
        }
    }
}
