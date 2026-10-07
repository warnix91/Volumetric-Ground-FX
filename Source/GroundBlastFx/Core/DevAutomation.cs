using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GroundBlastFx.Config;
using GroundBlastFx.Contracts;
using GroundBlastFx.Model;
using UnityEngine;
using QualityLevel = GroundBlastFx.Contracts.QualityLevel;

namespace GroundBlastFx.Core
{
    /// <summary>
    /// Automatisation de test RÉSERVÉE AU DÉVELOPPEMENT. Inactive sauf si le fichier
    /// GameData/Volumetric Ground FX/PluginData/DevAutomation.cfg existe avec enabled = true — il n'est jamais livré.
    /// Au menu principal : charge une sauvegarde et passe en vol sur un vaisseau ; en vol : exécute des étapes
    /// horodatées (démos, captures d'écran, caméra, moteurs, mesures de performance), puis peut quitter le jeu.
    /// Ne sauvegarde jamais la partie.
    /// Format :
    /// GROUNDBLASTFX_DEV
    /// {
    ///     enabled = true
    ///     save = GroundBlastFx_TEST        // dossier de saves/
    ///     game = persistent                // fichier .sfs sans extension
    ///     vessel = Starship SuperHeavy     // nom du vaisseau à piloter (sinon vaisseau actif de la sauvegarde)
    ///     STEP
    ///     {
    ///         at = 5                       // secondes (temps réel) depuis le chargement de la scène de vol
    ///         action = demo                // demo, stopdemo, screenshot, overlay, window, camera, engines,
    ///         arg = S1Landing              // throttle, quality, perf, resetperf, log, quit
    ///     }
    /// }
    /// </summary>
    public static class DevAutomation
    {
        public sealed class Step
        {
            public float At;
            public string Action;
            public string Arg;
            public bool Done;
        }

        public static bool Enabled { get; private set; }
        public static bool LoadAttempted;
        public static string Save;
        public static string GameFile = "persistent";
        public static string VesselName;
        public static string LaunchCraft;   // chemin d'un .craft relatif au dossier de la sauvegarde : lancement neuf
        public static string LaunchSite = "LaunchPad";
        public static readonly List<Step> Steps = new List<Step>();

        public static void Read()
        {
            Enabled = false;
            Steps.Clear();
            try
            {
                if (!File.Exists(GePaths.DevAutomationFile)) return;
                ConfigNode root = ConfigNode.Load(GePaths.DevAutomationFile);
                ConfigNode n = root?.GetNode("GROUNDBLASTFX_DEV");
                if (n == null || !CfgParse.Bool(n, "enabled", false)) return;
                Save = n.GetValue("save");
                GameFile = n.GetValue("game") ?? "persistent";
                VesselName = n.GetValue("vessel");
                LaunchCraft = n.GetValue("launchCraft");
                LaunchSite = n.GetValue("launchSite") ?? "LaunchPad";
                foreach (ConfigNode s in n.GetNodes("STEP"))
                {
                    Steps.Add(new Step
                    {
                        At = CfgParse.Float(s, "at", 0f),
                        Action = (s.GetValue("action") ?? "").Trim().ToLowerInvariant(),
                        Arg = s.GetValue("arg") ?? "",
                    });
                }
                Steps.Sort((a, b) => a.At.CompareTo(b.At));
                Enabled = true;
                GeLog.Warn("DevAutomation ACTIVE (" + GePaths.DevAutomationFile + ") : sauvegarde « " + Save + " », " + Steps.Count + " étapes. Fichier de développement : à supprimer pour jouer normalement.");
            }
            catch (Exception e)
            {
                Enabled = false;
                GeLog.Warn("DevAutomation illisible : " + e.Message);
            }
        }
    }

    /// <summary>Menu principal : chargement automatique de la sauvegarde de test.</summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public sealed class DevAutoLoader : MonoBehaviour
    {
        private float _timer = 3f;

        private void Start()
        {
            if (!DevAutomation.LoadAttempted) DevAutomation.Read();
            if (!DevAutomation.Enabled || DevAutomation.LoadAttempted) enabled = false;
        }

        private void Update()
        {
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f) return;
            enabled = false;
            DevAutomation.LoadAttempted = true;
            try
            {
                GeSession.EnsureLoaded();
                string save = DevAutomation.Save;
                if (string.IsNullOrEmpty(save) || !Directory.Exists(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), save)))
                {
                    GeLog.Error("DevAutomation : sauvegarde introuvable « " + save + " »");
                    return;
                }
                HighLogic.SaveFolder = save;
                Game game = GamePersistence.LoadGame(DevAutomation.GameFile, save, true, false);
                if (game == null || game.flightState == null)
                {
                    GeLog.Error("DevAutomation : chargement de « " + save + "/" + DevAutomation.GameFile + " » impossible");
                    return;
                }
                if (!string.IsNullOrEmpty(DevAutomation.LaunchCraft))
                {
                    // Le pas de tir n'existe qu'une fois la scène du KSC chargée : on y passe d'abord (DevLaunchFromKsc).
                    HighLogic.CurrentGame = game;
                    GamePersistence.UpdateScenarioModules(game);
                    game.startScene = GameScenes.SPACECENTER;
                    GeLog.Info("DevAutomation : passage au KSC avant le lancement de « " + DevAutomation.LaunchCraft + " »");
                    game.Start();
                    return;
                }
                int idx = game.flightState.activeVesselIdx;
                if (!string.IsNullOrEmpty(DevAutomation.VesselName))
                {
                    List<ProtoVessel> pvs = game.flightState.protoVessels;
                    for (int i = 0; i < pvs.Count; i++)
                    {
                        if (pvs[i].vesselName == DevAutomation.VesselName && (pvs[i].situation == Vessel.Situations.LANDED || pvs[i].situation == Vessel.Situations.PRELAUNCH || pvs[i].situation == Vessel.Situations.SPLASHED))
                        {
                            idx = i;
                            break;
                        }
                    }
                }
                if (idx < 0 || idx >= game.flightState.protoVessels.Count)
                {
                    GeLog.Error("DevAutomation : vaisseau « " + DevAutomation.VesselName + " » introuvable");
                    return;
                }
                GeLog.Info("DevAutomation : passage en vol sur « " + game.flightState.protoVessels[idx].vesselName + " » (index " + idx + ")");
                HighLogic.CurrentGame = game;
                GamePersistence.UpdateScenarioModules(game);
                game.startScene = GameScenes.FLIGHT;
                game.flightState.activeVesselIdx = idx;
                game.Start();
            }
            catch (Exception e)
            {
                GeLog.Error("DevAutomation : échec du chargement automatique : " + e);
            }
        }
    }

    /// <summary>Scène du KSC : lancement neuf du .craft demandé (une seule fois par session).</summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public sealed class DevLaunchFromKsc : MonoBehaviour
    {
        private static bool _launched;
        private float _timer = 4f;

        private void Start()
        {
            if (!DevAutomation.Enabled || _launched || string.IsNullOrEmpty(DevAutomation.LaunchCraft)) enabled = false;
        }

        private void Update()
        {
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f) return;
            enabled = false;
            _launched = true;
            try
            {
                string craftPath = Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), HighLogic.SaveFolder), DevAutomation.LaunchCraft);
                ConfigNode craft = ConfigNode.Load(craftPath);
                if (craft == null) { GeLog.Error("DevAutomation : craft illisible " + craftPath); return; }
                VesselCrewManifest manifest = VesselCrewManifest.FromConfigNode(craft);
                GeLog.Info("DevAutomation : lancement neuf de « " + DevAutomation.LaunchCraft + " » sur " + DevAutomation.LaunchSite);
                FlightDriver.StartWithNewLaunch(craftPath, HighLogic.CurrentGame.flagURL, DevAutomation.LaunchSite, manifest);
            }
            catch (Exception e)
            {
                GeLog.Error("DevAutomation : échec du lancement : " + e);
            }
        }
    }

    /// <summary>Scène de vol : exécution des étapes.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class DevAutomationRunner : MonoBehaviour
    {
        private float _t;
        private bool _started;

        private void Start()
        {
            if (!DevAutomation.Enabled) { enabled = false; return; }
            for (int i = 0; i < DevAutomation.Steps.Count; i++) DevAutomation.Steps[i].Done = false;
        }

        private void Update()
        {
            if (!_started)
            {
                if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null || GroundBlastFxAddon.Instance == null) return;
                _started = true;
                GeLog.Info("DevAutomation : scène de vol prête, début des étapes.");
            }
            _t += Time.unscaledDeltaTime;
            List<DevAutomation.Step> steps = DevAutomation.Steps;
            for (int i = 0; i < steps.Count; i++)
            {
                DevAutomation.Step s = steps[i];
                if (s.Done || s.At > _t) continue;
                s.Done = true;
                try { Run(s); }
                catch (Exception e) { GeLog.Error("DevAutomation : étape « " + s.Action + " " + s.Arg + " » en échec : " + e.Message); }
            }
        }

        private static void Run(DevAutomation.Step s)
        {
            GroundBlastFxAddon addon = GroundBlastFxAddon.Instance;
            string arg = s.Arg.Trim();
            GeLog.Info("DevAutomation t=" + s.At.ToString("0.0", CultureInfo.InvariantCulture) + " s : " + s.Action + " " + arg);
            switch (s.Action)
            {
                case "demo":
                {
                    var kind = (DemoKind)Enum.Parse(typeof(DemoKind), arg, true);
                    string err = addon.StartDemo(kind);
                    if (err != null) GeLog.Warn("DevAutomation : démo refusée : " + err);
                    break;
                }
                case "stopdemo":
                    addon.StopDemo();
                    break;
                case "screenshot":
                {
                    Directory.CreateDirectory(GePaths.CapturesDir);
                    string path = Path.Combine(GePaths.CapturesDir, arg + ".png");
                    ScreenCapture.CaptureScreenshot(path);
                    GeLog.Info("DevAutomation : capture " + path);
                    break;
                }
                case "overlay":
                    GeSettings.ShowOverlay = ParseBool(arg);
                    break;
                case "window":
                    addon.Window?.SetVisible(ParseBool(arg));
                    break;
                case "camera":
                {
                    string[] p = arg.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    FlightCamera cam = FlightCamera.fetch;
                    if (cam == null) break;
                    if (p.Length > 0) cam.SetDistance(ParseFloat(p[0]));
                    if (p.Length > 1) cam.camPitch = ParseFloat(p[1]) * Mathf.Deg2Rad;
                    if (p.Length > 2) cam.camHdg = ParseFloat(p[2]) * Mathf.Deg2Rad;
                    break;
                }
                case "engines":
                {
                    Vessel v = FlightGlobals.ActiveVessel;
                    bool on = ParseBool(arg);
                    int n = 0;
                    for (int i = 0; i < v.parts.Count; i++)
                    {
                        Part part = v.parts[i];
                        for (int k = 0; k < part.Modules.Count; k++)
                        {
                            if (!(part.Modules[k] is ModuleEngines me)) continue;
                            if (on) me.Activate(); else me.Shutdown();
                            n++;
                        }
                    }
                    GeLog.Info("DevAutomation : " + n + " moteur(s) " + (on ? "activés" : "coupés"));
                    break;
                }
                case "throttle":
                    FlightInputHandler.state.mainThrottle = Mathf.Clamp01(ParseFloat(arg));
                    break;
                case "stage":
                    KSP.UI.Screens.StageManager.ActivateNextStage();
                    break;
                case "sun":
                    SetSunElevation(ParseFloat(arg));
                    break;
                case "trench":
                {
                    // trench begin | rotate <deg> | radial | save | cancel
                    UI.TrenchTool t = addon.Overlay?.Trench;
                    if (t == null) break;
                    string[] p = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string verb = p.Length > 0 ? p[0].ToLowerInvariant() : "";
                    if (verb == "begin") t.Begin();
                    else if (verb == "rotate" && p.Length > 1) t.Rotate(ParseFloat(p[1]));
                    else if (verb == "radial") t.SetRadial();
                    else if (verb == "save") t.Save();
                    else if (verb == "cancel") t.Cancel();
                    GeLog.Info("DevAutomation : tranchée " + (t.Active ? "site « " + t.SiteKey + " », cap " + t.HeadingDeg.ToString("0", CultureInfo.InvariantCulture) : "inactive") + (t.Message != null ? " — " + t.Message : ""));
                    break;
                }
                case "quality":
                    GeSettings.Renderer.Quality = (QualityLevel)Enum.Parse(typeof(QualityLevel), arg, true);
                    GeSettings.NotifyChanged();
                    break;
                case "perf":
                {
                    PerfMonitor p = addon.Perf;
                    GeLog.Info("PERF [" + arg + "] Core " + p.AverageMs.ToString("0.0000", CultureInfo.InvariantCulture) + " ms/frame moy., max "
                               + p.MaxMs.ToString("0.0000", CultureInfo.InvariantCulture) + " ms ; Submit " + p.RendererAverageMs.ToString("0.0000", CultureInfo.InvariantCulture)
                               + " ms ; alloc " + p.AverageAllocBytes.ToString("0", CultureInfo.InvariantCulture) + " o/frame (" + p.FramesWithAlloc + "/" + p.FramesMeasured
                               + " frames) ; jets " + addon.Jets.Count + ", rayons " + addon.Probe.LastRayCount + ", foyers " + addon.Tracker.InUseCount + " (" + addon.ClusterCount
                               + " envoyés), traces " + addon.MarkCount + ", GC gen0 " + GC.CollectionCount(0) + ", FPS " + (1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)).ToString("0"));
                    DumpClusters(addon);
                    break;
                }
                case "resetperf":
                    addon.Perf.ResetCounters();
                    break;
                case "log":
                    GeLog.Info("DevAutomation : " + arg);
                    break;
                case "quit":
                    GeLog.Info("DevAutomation : fin des étapes, fermeture du jeu (sans sauvegarde).");
                    Application.Quit();
                    break;
                default:
                    GeLog.Warn("DevAutomation : action inconnue « " + s.Action + " »");
                    break;
            }
        }

        private static void DumpClusters(GroundBlastFxAddon addon)
        {
            ImpingementCluster[] cl = addon.Clusters;
            for (int i = 0; i < addon.ClusterCount; i++)
            {
                ImpingementCluster c = cl[i];
                GeLog.Info("  foyer #" + c.Id + (c.IsDemo ? " (démo)" : "") + " " + c.Surface + "/" + c.Medium + " actif=" + c.EnginesActive
                           + " I=" + F(c.Intensity01) + " F=" + F(c.TotalThrustN / 1000f) + " kN ×" + c.EngineCount + " h=" + F(c.StandoffM)
                           + " r_i=" + F(c.ImpingementRadiusM) + " R=" + F(c.CloudFrontRadiusM) + "/" + F(c.MaxCloudRadiusM) + " u_i=" + F(c.WallJetVelocityMs)
                           + " p_s=" + F(c.ImpingementPressurePa) + " p_amb=" + F(c.AmbientPressurePa) + " g=" + F(c.GravityMs2) + " vapeur=" + F(c.SteamFraction01)
                           + " érod=" + F(c.Erodibility01) + " lum=" + F(c.FlameLightIntensity) + " cam=" + F(c.CameraDistanceM) + " m LOD" + c.LodLevel
                           + " tranchée=" + c.TrenchDirectionWorld + " vent=" + c.WindWorldMs.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s"
                           + " éjectas=" + F(c.VacuumEjectaSpeedMs) + " m/s @" + F(c.VacuumEjectaAngleDeg) + "°");
            }
        }

        /// <summary>
        /// Avance le temps universel (dans la journée qui vient) jusqu'à ce que le soleil soit à l'élévation voulue
        /// au-dessus du vaisseau actif (côté matin). Négatif = nuit. Sert aux scénarios S1 (30°), S2 (45°), S5 (10°), S6 (nuit).
        /// </summary>
        private static void SetSunElevation(float targetDeg)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            CelestialBody sun = Planetarium.fetch.Sun;
            if (body == sun || body.rotationPeriod <= 0) return;
            Vector3d c = body.position;
            Vector3d up0 = ((Vector3d)v.transform.position - c).normalized;
            Vector3d axis = ((Vector3d)body.transform.up).normalized;
            SurfaceFrame.Basis(body, v.transform.position, out Vector3 _, out Vector3 _, out Vector3 east);
            Vector3d ax = Vector3d.Cross(axis, up0);
            double sign = Vector3d.Dot(ax, (Vector3d)east) >= 0 ? 1.0 : -1.0;
            Vector3d sunDir = (sun.position - c).normalized;
            double bestDt = 0, bestErr = double.MaxValue;
            double prevElev = double.NaN;
            for (int k = 0; k <= 720; k++)
            {
                double dt = body.rotationPeriod * k / 720.0;
                double a = sign * 2.0 * Math.PI * dt / body.rotationPeriod;
                Vector3d u = up0 * Math.Cos(a) + Vector3d.Cross(axis, up0) * Math.Sin(a) + axis * Vector3d.Dot(axis, up0) * (1 - Math.Cos(a));
                double elev = Math.Asin(Vector3d.Dot(u, sunDir)) * 180.0 / Math.PI;
                bool rising = double.IsNaN(prevElev) || elev >= prevElev;
                prevElev = elev;
                double err = Math.Abs(elev - targetDeg);
                if (targetDeg >= 0 && !rising) continue;
                if (err < bestErr) { bestErr = err; bestDt = dt; }
            }
            double ut = Planetarium.GetUniversalTime() + bestDt;
            Planetarium.SetUniversalTime(ut);
            GeLog.Info("DevAutomation : soleil à " + targetDeg.ToString("0", CultureInfo.InvariantCulture) + "° (écart " + bestErr.ToString("0.0", CultureInfo.InvariantCulture) + "°), UT = " + ut.ToString("0", CultureInfo.InvariantCulture));
        }

        private static string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private static bool ParseBool(string s) { return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("on", StringComparison.OrdinalIgnoreCase); }

        private static float ParseFloat(string s) { return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
    }
}
