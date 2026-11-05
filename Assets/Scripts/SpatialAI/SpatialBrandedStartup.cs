using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;

namespace SpatialAI
{
    // Presentation only: the normal scene and AR session initialize underneath.
    // Never starts scanning/localization and never waits for tracking or mapping.
    public sealed class SpatialBrandedStartup : MonoBehaviour
    {
        static SpatialBrandedStartup instance;
        public static bool Visible => instance != null;
        CanvasGroup group;
        RectTransform safe;
        ARCameraManager cameraManager;
        float began, fadeBegan = -1;
        bool sceneReady, cameraReady;
        Rect lastSafe;
        int lastWidth, lastHeight;
        const float StartupLimit = 6f, FadeDuration = .3f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Show()
        {
            if (Application.isEditor) return;
            var root = new GameObject("SpatialAI startup", typeof(RectTransform));
            DontDestroyOnLoad(root);
            root.AddComponent<SpatialBrandedStartup>();
        }

        void Awake()
        {
            instance = this;
            began = Time.realtimeSinceStartup;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080,1920);
            scaler.matchWidthOrHeight = .5f;
            gameObject.AddComponent<GraphicRaycaster>();
            group = gameObject.AddComponent<CanvasGroup>();
            var background = gameObject.AddComponent<Image>();
            background.color = Color.black;
            safe = Rect("Safe area", transform);
            Text("App name", "SpatialAI", new Vector2(.08f,.80f), new Vector2(.92f,.92f), 82);
            Artwork("Original URAV logo", safe, new Vector2(.05f,.18f), new Vector2(.95f,.77f), "Logo");
            Text("Company", "Urav Advanced Learning Systems Pvt Ltd", new Vector2(.06f,.055f), new Vector2(.94f,.125f), 30);
            SceneManager.sceneLoaded += Loaded;
            UpdateSafeArea();
            Debug.Log("[SpatialAI Branding] stage=Shown app=SpatialAI");
        }

        void Loaded(Scene scene, LoadSceneMode mode)
        {
            sceneReady = true;
            cameraManager = FindAnyObjectByType<ARCameraManager>();
            if (cameraManager != null) cameraManager.frameReceived += CameraFrame;
        }
        void CameraFrame(ARCameraFrameEventArgs args) { cameraReady = true; }

        void Update()
        {
            UpdateSafeArea();
            float now = Time.realtimeSinceStartup;
            if (fadeBegan < 0 && sceneReady && (cameraReady || now-began >= StartupLimit))
            {
                fadeBegan = now;
                Debug.Log("[SpatialAI Branding] stage=Ready reason=" + (cameraReady ? "CameraFrame" : "StartupLimit") + " elapsed=" + (now-began).ToString("F2"));
            }
            if (fadeBegan < 0) return;
            group.alpha = 1-Mathf.Clamp01((now-fadeBegan)/FadeDuration);
            if (group.alpha <= 0) Destroy(gameObject);
        }

        void UpdateSafeArea()
        {
            var area = Screen.safeArea;
            if (Screen.width <= 0 || Screen.height <= 0) return;
            if (area != lastSafe || lastWidth != Screen.width || lastHeight != Screen.height)
            {
                lastSafe = area; lastWidth = Screen.width; lastHeight = Screen.height;
                safe.anchorMin = new Vector2(area.xMin/lastWidth,area.yMin/lastHeight);
                safe.anchorMax = new Vector2(area.xMax/lastWidth,area.yMax/lastHeight);
                safe.offsetMin = safe.offsetMax = Vector2.zero;
            }
        }

        static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent,false); return rect;
        }
        static void Anchors(RectTransform rect, Vector2 min, Vector2 max)
        { rect.anchorMin=min; rect.anchorMax=max; rect.offsetMin=rect.offsetMax=Vector2.zero; }
        void Artwork(string name, Transform parent, Vector2 min, Vector2 max, string resource)
        {
            var box = Rect(name,parent); Anchors(box,min,max);
            var texture = Resources.Load<Texture2D>("SpatialBranding/"+resource);
            if (texture == null) return;
            var rect = Rect("Artwork",box); Anchors(rect,Vector2.zero,Vector2.one);
            var image = rect.gameObject.AddComponent<RawImage>(); image.texture=texture;image.raycastTarget=false;
            var fit = rect.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode=AspectRatioFitter.AspectMode.FitInParent;fit.aspectRatio=(float)texture.width/texture.height;
        }
        void Text(string name,string value,Vector2 min,Vector2 max,int size)
        {
            var rect=Rect(name,safe);Anchors(rect,min,max);
            var label=rect.gameObject.AddComponent<Text>();label.text=value;
            label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");label.fontSize=size;
            label.alignment=TextAnchor.MiddleCenter;label.color=Color.white;label.raycastTarget=false;
            label.resizeTextForBestFit=true;label.resizeTextMinSize=22;label.resizeTextMaxSize=size;
            label.horizontalOverflow=HorizontalWrapMode.Wrap;
        }
        void OnDestroy()
        {
            if (instance == this) instance = null;
            SceneManager.sceneLoaded -= Loaded;
            if (cameraManager != null) cameraManager.frameReceived -= CameraFrame;
            Debug.Log("[SpatialAI Branding] stage=Dismissed");
        }
    }
}
