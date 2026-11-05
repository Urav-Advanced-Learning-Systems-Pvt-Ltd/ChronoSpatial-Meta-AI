using System;
using System.IO;
using UnityEngine;

namespace SpatialAI
{
    // Small local development trace so scan counts and Start blockers survive
    // app closure or a cleared Android log buffer. No images are recorded.
    public static class SpatialNavigationTrace
    {
        public static void Record(string message)
        {
            Debug.Log(message);
#if UNITY_ANDROID && !UNITY_EDITOR
            if(!Debug.isDebugBuild)return;
            try
            {
                var folder=Path.Combine(Application.persistentDataPath,"SpatialAI","Diagnostics");
                Directory.CreateDirectory(folder);
                var path=Path.Combine(folder,"navigation-events.log");
                if(File.Exists(path) && new FileInfo(path).Length>65536)
                { File.Copy(path,path+".previous",true);File.WriteAllText(path,""); }
                File.AppendAllText(path,DateTime.UtcNow.ToString("O")+" "+message.Replace('\n',' ').Replace('\r',' ')+"\n");
            }
            catch(Exception) { /* Diagnostics must never block mapping or saving. */ }
#endif
        }
    }
}
