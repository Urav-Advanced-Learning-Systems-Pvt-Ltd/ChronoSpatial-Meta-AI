using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SpatialAI
{
    // Local Wi-Fi Living Map sync: lightweight peer-to-peer map sharing
    // between two phones on the same subnet (hackathon thesis demo).
    // Share mode hosts the active map JSON over HTTP (:8080) and emits a
    // UDP beacon so guests can find it with no typing. Sync mode listens
    // for that beacon (or takes a direct LAN IP), downloads the map over
    // HTTP via UnityWebRequest, and feeds it into
    // SpatialMapReconciler.Merge on the Unity thread.
    //
    // Main-thread rule: Unity APIs run only on Update-pumped callbacks.
    // The HTTP listener and beacon loops run on background tasks and touch
    // only the snapshotted JSON string and the action queue.
    public sealed class SpatialLanSync : MonoBehaviour
    {
        public const int HttpPort = 8080;
        public const int BeaconPort = 50080;
        public const string BeaconPrefix = "SpatialAI_Sync:";
        public const string MapPath = "/spatialai/map/";
        public const float BeaconIntervalSeconds = 2f;
        public const float DefaultDiscoverTimeoutSeconds = 12f;

        public bool IsHosting { get; private set; }
        public bool IsSyncing { get; private set; }
        public string LastError { get; private set; }
        public string HostUrl { get; private set; }
        public string LastPeer { get; private set; }

        public event Action<string> SyncCompleted;

        readonly Queue<Action> mainThread = new Queue<Action>();
        HttpListener listener;
        CancellationTokenSource hostCts;
        Task hostTask;
        Task beaconTask;
        string snapshotJson = "";
        float nextSnapshot;

        public static SpatialLanSync GetOrCreate()
        {
            var existing = FindAnyObjectByType<SpatialLanSync>();
            if (existing != null) return existing;
            return new GameObject("Spatial LAN Sync").AddComponent<SpatialLanSync>();
        }

        void Update()
        {
            lock (mainThread)
            {
                while (mainThread.Count > 0) mainThread.Dequeue()?.Invoke();
            }
            if (IsHosting && Time.realtimeSinceStartup >= nextSnapshot)
            {
                nextSnapshot = Time.realtimeSinceStartup + 1f;
                Snapshot();
            }
        }

        void OnDestroy()
        {
            StopHosting();
        }

        void Enqueue(Action action)
        {
            lock (mainThread) mainThread.Enqueue(action);
        }

        // First non-loopback IPv4 on an up interface, for the beacon body.
        public static string LocalIp()
        {
            try
            {
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(unicast.Address)) continue;
                        return unicast.Address.ToString();
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[SpatialAI Sync] local IP scan failed: " + e.Message); }
            return "127.0.0.1";
        }

        static List<IPAddress> BroadcastAddresses()
        {
            var broadcasts = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(unicast.Address) || unicast.IPv4Mask == null) continue;
                        byte[] ip = unicast.Address.GetAddressBytes();
                        byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                        var address = new IPAddress(broadcast);
                        if (!broadcasts.Contains(address)) broadcasts.Add(address);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[SpatialAI Sync] broadcast scan failed: " + e.Message); }
            return broadcasts;
        }

        void Snapshot()
        {
            try
            {
                var map = FindAnyObjectByType<SpatialMap>();
                if (map == null || map.Data == null) return;
                snapshotJson = SpatialMapSerializer.Serialize(map.Data);
            }
            catch (Exception e) { LastError = "Snapshot failed: " + e.Message; }
        }

        public bool StartHosting()
        {
            if (IsHosting) return true;
            LastError = null;
            try
            {
                Snapshot();
                if (string.IsNullOrEmpty(snapshotJson))
                {
                    LastError = "No active map to share. Scan an area first.";
                    return false;
                }
                listener = new HttpListener();
                listener.Prefixes.Add("http://*:" + HttpPort + "/");
                listener.Prefixes.Add("http://localhost:" + HttpPort + "/");
                listener.Start();
                hostCts = new CancellationTokenSource();
                var token = hostCts.Token;
                hostTask = Task.Run(() => ServeLoop(token));
                beaconTask = Task.Run(() => BeaconLoop(token));
                IsHosting = true;
                HostUrl = "http://" + LocalIp() + ":" + HttpPort + MapPath;
                Debug.Log("[SpatialAI Sync] sharing map at " + HostUrl);
                return true;
            }
            catch (Exception e)
            {
                LastError = "Share failed: " + e.Message;
                Debug.LogWarning("[SpatialAI Sync] " + LastError);
                StopHosting();
                return false;
            }
        }

        public void StopHosting()
        {
            IsHosting = false;
            try { if (hostCts != null) hostCts.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                if (listener != null && listener.IsListening) listener.Stop();
            }
            catch (Exception) { }
            try
            {
                if (listener != null) listener.Close();
            }
            catch (Exception) { }
            listener = null;
            if (hostCts != null) { hostCts.Dispose(); hostCts = null; }
            Debug.Log("[SpatialAI Sync] sharing stopped.");
        }

        void ServeLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context = null;
                try { context = listener.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    Debug.LogWarning("[SpatialAI Sync] accept failed: " + e.Message);
                    continue;
                }
                try
                {
                    string path = context.Request.Url != null ? context.Request.Url.AbsolutePath : "";
                    // Snapshot the peer before Close(): Request/Response are
                    // unusable afterwards and re-reading them throws.
                    string peer;
                    try { peer = context.Request.RemoteEndPoint != null ? context.Request.RemoteEndPoint.ToString() : "?"; }
                    catch (Exception) { peer = "?"; }
                    if (!path.StartsWith(MapPath, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        continue;
                    }
                    byte[] body = Encoding.UTF8.GetBytes(snapshotJson ?? "");
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    context.Response.OutputStream.Write(body, 0, body.Length);
                    context.Response.Close();
                    Debug.Log("[SpatialAI Sync] served map (" + body.Length + " bytes) to " + peer);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SpatialAI Sync] serve failed: " + e.Message);
                    try { context.Response.Close(); } catch (Exception) { }
                }
            }
        }

        void BeaconLoop(CancellationToken token)
        {
            string beacon = BeaconPrefix + LocalIp() + ":" + HttpPort;
            var endpoints = new List<IPEndPoint>();
            foreach (var address in BroadcastAddresses())
                endpoints.Add(new IPEndPoint(address, BeaconPort));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var sender = new UdpClient())
                    {
                        sender.EnableBroadcast = true;
                        byte[] body = Encoding.UTF8.GetBytes(beacon);
                        foreach (var endpoint in endpoints)
                            sender.Send(body, body.Length, endpoint);
                    }
                }
                catch (Exception e) { Debug.LogWarning("[SpatialAI Sync] beacon failed: " + e.Message); }
                try { Task.Delay(TimeSpan.FromSeconds(BeaconIntervalSeconds), token).Wait(token); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Listens for a host beacon, then downloads and merges on the
        // Unity thread. Completion (success or failure) arrives through
        // SyncCompleted so the UI can notify.
        public void DiscoverAndSync(float timeoutSeconds = DefaultDiscoverTimeoutSeconds)
        {
            if (IsSyncing) return;
            LastError = null;
            IsSyncing = true;
            string self = LocalIp();
            Task.Run(() =>
            {
                string host = null;
                int port = HttpPort;
                var deadline = DateTime.UtcNow.AddSeconds(Mathf.Max(3f, timeoutSeconds));
                try
                {
                    using (var receiver = new UdpClient(BeaconPort))
                    {
                        receiver.Client.ReceiveTimeout = 1000;
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        while (DateTime.UtcNow < deadline)
                        {
                            byte[] body;
                            try { body = receiver.Receive(ref remote); }
                            catch (SocketException) { continue; }
                            if (TryParseBeacon(Encoding.UTF8.GetString(body), out string ip, out int parsedPort)
                                && !string.Equals(ip, self, StringComparison.Ordinal))
                            {
                                host = ip;
                                port = parsedPort;
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Enqueue(() => FinishSync(false, "Sync failed: " + e.Message, null));
                    return;
                }
                if (host == null)
                {
                    Enqueue(() => FinishSync(false, "Sync failed: no shared map found nearby.", null));
                    return;
                }
                string found = host;
                int foundPort = port;
                Enqueue(() => StartCoroutine(DownloadRoutine(found, foundPort)));
            });
        }

        public void SyncFromIp(string ip)
        {
            if (IsSyncing) return;
            if (string.IsNullOrWhiteSpace(ip))
            {
                FinishSync(false, "Sync failed: enter a local IP.", null);
                return;
            }
            LastError = null;
            IsSyncing = true;
            StartCoroutine(DownloadRoutine(ip.Trim(), HttpPort));
        }

        public static bool TryParseBeacon(string beacon, out string ip, out int port)
        {
            ip = null;
            port = HttpPort;
            if (string.IsNullOrEmpty(beacon) || !beacon.StartsWith(BeaconPrefix, StringComparison.Ordinal))
                return false;
            string[] parts = beacon.Substring(BeaconPrefix.Length).Split(':');
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) return false;
            if (!IPAddress.TryParse(parts[0], out _)) return false;
            ip = parts[0];
            if (parts.Length >= 2 && (!int.TryParse(parts[1], out port) || port <= 0 || port > 65535))
                return false;
            return true;
        }

        IEnumerator DownloadRoutine(string ip, int port)
        {
            string url = "http://" + ip + ":" + port + MapPath;
            Debug.Log("[SpatialAI Sync] downloading map from " + url);
            string json = null;
            string failure = null;
            using (var request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 20;
                yield return request.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                if (request.result != UnityWebRequest.Result.Success)
#else
                if (request.isNetworkError || request.isHttpError)
#endif
                    failure = "Sync failed: " + request.error;
                else
                    json = request.downloadHandler != null ? request.downloadHandler.text : null;
            }
            if (failure != null)
            {
                FinishSync(false, failure, null);
                yield break;
            }
            var data = SpatialMapSerializer.Deserialize(json);
            if (data == null)
            {
                FinishSync(false, "Sync failed: downloaded map is invalid.", null);
                yield break;
            }
            var reconciler = FindAnyObjectByType<SpatialMapReconciler>();
            if (reconciler == null)
            {
                FinishSync(false, "Sync failed: reconciler unavailable.", null);
                yield break;
            }
            var report = reconciler.Merge(data);
            if (!report.applied)
            {
                FinishSync(false, "Sync failed: " + report.failure, null);
                yield break;
            }
            LastPeer = ip;
            string message = "Living Map Synced: " + report.newEntities + " new entities, "
                + report.movedObjects + " moved objects detected.";
            if (report.obstructions > 0) message += " " + report.obstructions + " on route.";
            if (report.adoptedKeyframes > 0 || report.adoptedLandmarks > 0)
                message += " (+" + report.adoptedKeyframes + " frames, +" + report.adoptedLandmarks + " points)";
            Debug.Log("[SpatialAI Sync] " + message);
            FinishSync(true, message, report);
        }

        void FinishSync(bool ok, string message, object report)
        {
            IsSyncing = false;
            if (!ok) LastError = message;
            var handler = SyncCompleted;
            if (handler != null) handler.Invoke(message);
        }
    }
}
