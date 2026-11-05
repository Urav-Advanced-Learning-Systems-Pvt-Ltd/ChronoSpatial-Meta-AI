using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace SpatialAI
{
    // One owned request through extraction, matching AND result consumption. No FIFO backlog.
    // The owner additionally keeps capture deferred until all map mutations are applied.
    public sealed class SpatialMappingWorker : IDisposable
    {
        public sealed class Request
        {
            public int epoch, width, height;
            public string descriptorSchema = BinaryPatchDescriptor.Type;
            public Color32[] pixels;
            public SpatialFeatureSet features; // Optional owned pre-extracted data for deterministic replay.
            public List<SpatialKeyframe> references;
        }
        public sealed class Partner
        {
            public string keyframeId;
            public List<SpatialFeatureMatch> matches;
        }
        public sealed class Result
        {
            public Request request;
            public SpatialFeatureSet features;
            public List<Partner> partners = new List<Partner>();
            public double extractMs, matchMs;
            public Exception error;
        }

        readonly object sync = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        Request pending;
        Result completed;
        CancellationTokenSource cancellation;
        bool occupied, disposed;
        public bool Busy { get { lock (sync) return occupied; } }

        public SpatialMappingWorker()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "SpatialAI.Mapping", Priority = System.Threading.ThreadPriority.BelowNormal };
            thread.Start();
        }
        public bool TryEnqueue(Request request)
        {
            lock (sync)
            {
                if (disposed || occupied) return false;
                pending = request; occupied = true; cancellation = new CancellationTokenSource();
                wake.Set(); return true;
            }
        }
        public void Cancel() { lock (sync) cancellation?.Cancel(); }
        public bool TryTake(out Result result)
        {
            lock (sync)
            {
                result = completed;
                if (result == null) return false;
                completed = null; occupied = false;
                cancellation.Dispose(); cancellation = null;
                return true;
            }
        }
        void Run()
        {
            try
            {
                var extractor = new LocalBinaryFeatureExtractor();
                var matcher = new BinaryFeatureMatcher();
                while (true)
                {
                    wake.WaitOne();
                    Request request; CancellationToken token;
                    lock (sync)
                    {
                        if (disposed) return;
                        request = pending; pending = null; token = cancellation.Token;
                    }
                    var result = new Result { request = request };
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var clock = Stopwatch.StartNew();
                        result.features = request.features ?? (request.descriptorSchema == SpatialFeatureSchemas.OrbDistinct
                            ? new OrbFeatureExtractor().Extract(request.pixels, request.width, request.height, token)
                            : request.descriptorSchema == BinaryPatchDescriptor.Type
                                ? extractor.Extract(request.pixels, request.width, request.height, token)
                                : throw new InvalidOperationException("Unsupported mapping descriptor schema"));
                        result.extractMs = clock.Elapsed.TotalMilliseconds;
                        clock.Restart();
                        foreach (var reference in request.references)
                        {
                            token.ThrowIfCancellationRequested();
                            result.partners.Add(new Partner { keyframeId = reference.keyframeId,
                                matches = matcher.Match(result.features, new SpatialFeatureSet { features = reference.features }, token) });
                        }
                        result.matchMs = clock.Elapsed.TotalMilliseconds;
                    }
                    catch (Exception e) { result.error = e; }
                    lock (sync)
                    {
                        if (disposed) return;
                        completed = result;
                    }
                }
            }
            finally
            {
                lock (sync) { cancellation?.Dispose(); cancellation = null; pending = null; completed = null; occupied = false; }
                wake.Dispose();
            }
        }
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true; cancellation?.Cancel(); wake.Set();
            }
            // Inner loops observe cancellation. Do not block Unity waiting for arbitrary CPU work.
            thread.Join(100);
        }
    }
}
