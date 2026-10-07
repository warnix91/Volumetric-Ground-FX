using System;
using System.Diagnostics;
using GroundBlastFx.Config;
using GroundBlastFx.Contracts;
using GroundBlastFx.Model;
using GroundBlastFx.Surface;
using GroundBlastFx.UI;
using UnityEngine;
using QualityLevel = GroundBlastFx.Contracts.QualityLevel;

namespace GroundBlastFx.Core
{
    /// <summary>
    /// Addon de la scène de vol (tâche CL-1.1) : cycle de vie, GameEvents, tableaux pré-alloués.
    /// Update     : échantillonnage à SampleRateHz (25 Hz) — moteurs + démo → rayons lancés en jobs.
    /// LateUpdate : fin des jobs → modèle → fusion → suivi des foyers → traces → Submit au renderer (une fois par frame).
    /// Toute exception est interceptée (une seule fois dans le log) ; si le renderer échoue, bascule sur NullRenderer.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class GroundBlastFxAddon : MonoBehaviour
    {
        public static GroundBlastFxAddon Instance { get; private set; }

        private readonly EngineSampler _engines = new EngineSampler();
        private readonly SurfaceProbe _probe = new SurfaceProbe();
        private readonly SurfaceClassifier _classifier = new SurfaceClassifier();
        private readonly ImpactAggregator _aggregator = new ImpactAggregator();
        private readonly ClusterTracker _tracker = new ClusterTracker();
        private readonly ScorchManager _scorch = new ScorchManager();
        private readonly DemoDirector _demo = new DemoDirector();
        private readonly StockSurfaceFxSuppressor _stockFx = new StockSurfaceFxSuppressor();
        private readonly PerfMonitor _perf = new PerfMonitor();
        private readonly Stopwatch _rendererWatch = new Stopwatch();

        private readonly JetBuffer _jets = new JetBuffer();
        private JetHit[] _hits = new JetHit[64];
        private readonly ImpingementCluster[] _clusters = new ImpingementCluster[ClusterTracker.MaxClusters];
        private int _clusterCount;
        private readonly ScorchMark[] _marks = new ScorchMark[256];
        private int _markCount;

        private IGroundBlastFxRenderer _renderer = new NullRenderer();
        private string _rendererError;
        private float _sampleTimer;
        private bool _probePending;
        private CelestialBody _probeBody;
        private GroundBlastFxWindow _window;
        private DebugOverlay _overlay;
        private int _ticks;

        // --- Accès en lecture pour l'UI et l'overlay ---
        public JetBuffer Jets => _jets;
        public JetHit[] Hits => _hits;
        public ImpingementCluster[] Clusters => _clusters;
        public int ClusterCount => _clusterCount;
        public int MarkCount => _markCount;
        public ScorchMark[] Marks => _marks;
        public ClusterTracker Tracker => _tracker;
        public EngineSampler Engines => _engines;
        public DemoDirector Demo => _demo;
        public PerfMonitor Perf => _perf;
        public SurfaceProbe Probe => _probe;
        public ImpactAggregator Aggregator => _aggregator;
        public ScorchManager Scorch => _scorch;
        public StockSurfaceFxSuppressor StockFx => _stockFx;
        public SurfaceClassifier Classifier => _classifier;
        public IGroundBlastFxRenderer Renderer => _renderer;
        public string RendererError => _rendererError;
        public int Ticks => _ticks;
        public DebugOverlay Overlay => _overlay;
        public GroundBlastFxWindow Window => _window;

        private void Awake()
        {
            Instance = this;
            GeSession.EnsureLoaded();
            GameEvents.onVesselWasModified.Add(OnVesselModified);
            GameEvents.onVesselPartCountChanged.Add(OnVesselModified);
            GameEvents.onVesselDestroy.Add(OnVesselDestroy);
            GameEvents.onVesselLoaded.Add(OnVesselLoaded);
            GameEvents.onVesselGoOffRails.Add(OnVesselLoaded);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GeSettings.Changed += OnSettingsChanged;
        }

        private void Start()
        {
            try
            {
                _probe.Init();
                _tracker.Pads = _classifier.Pads;
                _engines.Pads = _classifier.Pads;
                _scorch.OnFlightStart();
                CreateRenderer();
                _stockFx.Apply();
                _window = new GroundBlastFxWindow(this);
                _overlay = new DebugOverlay(this);
                GeLog.Info("Scène de vol : Volumetric Ground FX (VGFX) actif (renderer " + _renderer.BackendName + ", qualité " + GeSettings.Renderer.Quality + ")");
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("GroundBlastFxAddon.Start", e);
            }
        }

        private void CreateRenderer()
        {
            IGroundBlastFxRenderer r = RendererLocator.Create();
            try
            {
                r.Initialize(GeSettings.Renderer);
                if (!r.IsAvailable)
                {
                    _rendererError = "renderer « " + r.BackendName + " » indisponible (IsAvailable = false)";
                    GeLog.Warn(_rendererError + " : bascule sur NullRenderer.");
                    SafeShutdown(r);
                    r = new NullRenderer();
                }
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("Renderer.Initialize", e);
                _rendererError = "échec d'initialisation : " + e.Message;
                SafeShutdown(r);
                r = new NullRenderer();
            }
            _renderer = r;
            _stockFx.SetReplacementRenderer(r);
            string info;
            try { info = r.GetDebugInfo(); } catch (Exception) { info = "(GetDebugInfo a échoué)"; }
            GeSession.LogBackend(r.BackendName, info);
        }

        private static void SafeShutdown(IGroundBlastFxRenderer r)
        {
            try { r.Shutdown(); }
            catch (Exception e) { GeLog.ExceptionOnce("Renderer.Shutdown", e); }
        }

        private void SwitchToNullRenderer(string reason)
        {
            if (_renderer is NullRenderer) return;
            GeLog.Error("Le renderer « " + _renderer.BackendName + " » a échoué (" + reason + ") : bascule sur NullRenderer, le jeu continue normalement.");
            _rendererError = reason;
            SafeShutdown(_renderer);
            _renderer = new NullRenderer();
            _stockFx.SetReplacementRenderer(_renderer);
        }

        private void OnSettingsChanged()
        {
            try { _renderer.ApplySettings(GeSettings.Renderer); }
            catch (Exception e) { GeLog.ExceptionOnce("Renderer.ApplySettings", e); SwitchToNullRenderer("exception dans ApplySettings"); }
            _stockFx.Apply();
        }

        private void Update()
        {
            _perf.Begin();
            try
            {
                float dt = Time.deltaTime;
                _demo.Update(dt);
                _stockFx.Tick(dt);
                PhysicsParams p = GeConfig.Physics;
                float interval = 1f / Mathf.Clamp(p.SampleRateHz, 1f, 120f);
                _sampleTimer += dt;
                if (dt > 0f && _sampleTimer >= interval && !_probePending)
                {
                    _sampleTimer = Mathf.Min(_sampleTimer - interval, interval);
                    CelestialBody body = FlightGlobals.currentMainBody;
                    if (body != null)
                    {
                        _jets.Clear();
                        _engines.Collect(_jets);
                        _demo.Collect(_jets);
                        if (_hits.Length < _jets.Count) _hits = new JetHit[_jets.Items.Length];
                        int ring = GeSettings.Renderer.Quality >= QualityLevel.High ? p.RingRaysHigh : p.RingRaysLow;
                        _probe.Schedule(_jets, ring);
                        _probeBody = body;
                        _probePending = true;
                    }
                }
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("GroundBlastFxAddon.Update", e);
            }
            _perf.End();
        }

        private void LateUpdate()
        {
            _perf.Begin();
            try
            {
                float dt = Time.deltaTime;
                if (_probePending)
                {
                    _probePending = false;
                    _probe.Complete(_jets, _hits, _classifier, _probeBody);
                    _aggregator.Process(_jets, _hits, _probeBody);
                    _tracker.BeginTick();
                    GroupData[] groups = _aggregator.Groups;
                    for (int i = 0; i < _aggregator.GroupCount; i++) _tracker.ApplyGroup(ref groups[i], _probeBody);
                    _tracker.EndTick();
                    _ticks++;
                }
                CelestialBody body = FlightGlobals.currentMainBody;
                _tracker.Advance(dt);
                _scorch.Update(_tracker, body, dt);
                Vector3 cam = FlightCamera.fetch != null ? FlightCamera.fetch.transform.position : Vector3.zero;
                _clusterCount = _tracker.BuildOutput(_clusters, cam, GeSettings.Renderer, dt);
                _markCount = GeSettings.Renderer.EnableScorch ? _scorch.BuildOutput(_marks, body) : 0;
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("GroundBlastFxAddon.LateUpdate", e);
                _clusterCount = 0;
                _markCount = 0;
            }
            _perf.End();

            _rendererWatch.Reset();
            _rendererWatch.Start();
            try
            {
                _renderer.Submit(_clusters, _clusterCount, _marks, _markCount);
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("Renderer.Submit", e);
                SwitchToNullRenderer("exception dans Submit : " + e.GetType().Name);
            }
            _rendererWatch.Stop();
            _perf.RendererSample(_rendererWatch.Elapsed.TotalMilliseconds);
            _perf.EndFrame();
        }

        private void OnGUI()
        {
            try
            {
                _overlay?.OnGUI();
                _window?.OnGUI();
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("GroundBlastFxAddon.OnGUI", e);
            }
        }

        // --- Actions de l'UI ---

        public string StartDemo(DemoKind kind)
        {
            try
            {
                Vessel active = FlightGlobals.ActiveVessel;
                string site = null;
                if (active != null) _engines.IsPadLatched(active, out site);
                return _demo.Start(kind, active, _probe.SceneryMask, site);
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("Démo", e);
                return e.Message;
            }
        }

        public void StopDemo()
        {
            _demo.StopAll();
            _tracker.ClearDemo();
        }

        public void ClearScorchMarks()
        {
            _scorch.Clear();
            _markCount = 0;
            for (int i = 0; i < _tracker.Capacity; i++) _tracker[i].ScorchSlot = -1;
        }

        // --- Événements ---

        private void OnVesselModified(Vessel v)
        {
            _engines.Invalidate(v);
            _stockFx.RequestScan();
        }

        private void OnVesselDestroy(Vessel v)
        {
            _engines.Remove(v);
        }

        private void OnVesselLoaded(Vessel v)
        {
            _engines.Invalidate(v);
            _stockFx.RequestScan();
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            _tracker.Clear();
            _demo.StopAll();
            _clusterCount = 0;
            _markCount = 0;
        }

        private void OnDestroy()
        {
            GameEvents.onVesselWasModified.Remove(OnVesselModified);
            GameEvents.onVesselPartCountChanged.Remove(OnVesselModified);
            GameEvents.onVesselDestroy.Remove(OnVesselDestroy);
            GameEvents.onVesselLoaded.Remove(OnVesselLoaded);
            GameEvents.onVesselGoOffRails.Remove(OnVesselLoaded);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GeSettings.Changed -= OnSettingsChanged;
            try { _probe.Dispose(); } catch (Exception e) { GeLog.ExceptionOnce("SurfaceProbe.Dispose", e); }
            try { _renderer.Submit(_clusters, 0, _marks, 0); } catch (Exception) { }
            SafeShutdown(_renderer);
            _renderer = new NullRenderer();
            try { _stockFx.Restore(); } catch (Exception e) { GeLog.ExceptionOnce("StockSurfaceFx.Restore", e); }
            try { _window?.Destroy(); } catch (Exception e) { GeLog.ExceptionOnce("GroundBlastFxWindow.Destroy", e); }
            _overlay?.Destroy();
            _tracker.Clear();
            _engines.Clear();
            _classifier.Clear();
            if (GeLog.RepeatedErrorCount > 0) GeLog.Info("Erreurs répétées pendant ce vol : " + GeLog.RepeatedErrorsSummary());
            GeLog.Info("Scène de vol quittée : Volumetric Ground FX (VGFX) nettoyé (" + _ticks + " sondages, coût moyen " + _perf.AverageMs.ToString("0.000") + " ms/frame).");
            if (Instance == this) Instance = null;
        }
    }
}
