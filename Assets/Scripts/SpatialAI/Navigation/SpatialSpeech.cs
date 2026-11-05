using UnityEngine;
using UnityEngine.Scripting;

namespace SpatialAI
{
    public sealed class SpatialSpeech : MonoBehaviour
    {
        public bool Available { get; private set; }
        public string Status { get; private set; } = "Preparing spoken guidance";
        string pending;
        bool paused;
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject engine;
        volatile int initialization = int.MinValue;
        bool configured, disposed;
        [Preserve]
        sealed class Listener : AndroidJavaProxy
        {
            readonly SpatialSpeech owner;
            public Listener(SpatialSpeech owner) : base("android.speech.tts.TextToSpeech$OnInitListener") { this.owner=owner; }
            [Preserve] public void onInit(int status) { owner.initialization=status; }
        }
        Listener listener;
        void Start()
        {
            listener=new Listener(this);
            try
            {
                using(var player=new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity=player.GetStatic<AndroidJavaObject>("currentActivity");
                    activity.Call("runOnUiThread",new AndroidJavaRunnable(()=>
                    {
                        if(disposed) return;
                        try { engine=new AndroidJavaObject("android.speech.tts.TextToSpeech",activity,listener); }
                        catch { initialization=-1; }
                    }));
                }
            }
            catch { initialization=-1; }
        }
        void Update()
        {
            if(configured || initialization==int.MinValue || (initialization==0 && engine==null)) return;
            configured=true;
            try
            {
                if(initialization!=0) { Status="Spoken guidance unavailable; follow the text and arrows"; return; }
                using(var locale=new AndroidJavaClass("java.util.Locale"))
                using(var english=locale.GetStatic<AndroidJavaObject>("ENGLISH"))
                    Available=engine.Call<int>("setLanguage",english)>=0;
                Status=Available ? "Spoken guidance ready" : "English voice unavailable; follow the text and arrows";
                if(Available) { engine.Call<int>("setSpeechRate",.9f); if(pending!=null && !paused) Speak(pending); }
            }
            catch { Available=false; Status="Spoken guidance unavailable; follow the text and arrows"; }
            pending=null;
            Debug.Log("[SpatialAI Speech] "+Status);
        }
#else
        void Start() { Status="Spoken guidance is available in the Android build"; }
#endif
        public void Speak(string message)
        {
            if(paused || string.IsNullOrWhiteSpace(message)) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if(!configured) { pending=message; return; }
            if(!Available || engine==null) return;
            try
            {
                using(var options=new AndroidJavaObject("android.os.Bundle"))
                    engine.Call<int>("speak",message,0,options,"spatial-"+Time.frameCount);
                Debug.Log("[SpatialAI Speech] "+message);
            }
            catch { Available=false; Status="Spoken guidance unavailable; follow the text and arrows"; }
#endif
        }
        public void Stop()
        {
            pending=null;
#if UNITY_ANDROID && !UNITY_EDITOR
            try { engine?.Call<int>("stop"); } catch { }
#endif
        }
        void OnApplicationPause(bool value) { paused=value; if(value) Stop(); }
        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            disposed=true; Stop();
            try { engine?.Call("shutdown"); engine?.Dispose(); } catch { }
#endif
        }
    }
}
