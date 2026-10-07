using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using GroundBlastFx.Contracts;
using GroundBlastFx.Model;
using GroundBlastFx.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using QualityLevel = GroundBlastFx.Contracts.QualityLevel;

// Harnais de rendu hors jeu. Il utilise EXACTEMENT le même code que le jeu :
// - RenderCore (Source/GroundBlastFx/Rendering/RenderCore.cs, copié dans Assets/Editor/Shared par les scripts Tools/) ;
// - le modèle physique du Core (Source/GroundBlastFx/Physics/*.cs) et les profils de démo S1…S7 ;
// - le bundle compilé GameData/GroundBlastFx/Shaders/GroundBlastFx.unity3d.
// Chaque scénario est simulé à 30 Hz (front du nuage, fondus, coupure, dissipation) et capturé aux instants clés.
public static class RenderHarness
{
    private sealed class Scene
    {
        public string Id, Title;
        public DemoKind Demo;
        public SurfaceKind Surface;
        public bool Vacuum;
        public float SunElevationDeg, SunAzimuthDeg;
        public float[] Frames;
        public Color DustA, DustB, Ground;
        public float TrenchHeadingDeg = -1f;
        public float WindMs;
        public float CutoffAtS = -1f; // coupure anticipée (sinon fin du profil)
        public float CameraScale = 1f;
        public float CameraHeightScale = 1f;
        public bool Water, Barge, Pad;
        public Color FlameColor = new Color(1f, 0.62f, 0.30f);
        public float FlameIntensity = 1f;
        public float SteamBonus;
        public Vector3 DriftAccel;        // le point d'impact se déplace (fusée qui bascule) : m/s², à partir de t = 2 s
        public float CameraPitchUp;       // caméra plus haute, vue plongeante (comme les captures en jeu)
        public float ThrustScale = 1f;    // poussée × (plusieurs moteurs)
        public float StandoffM = -1f;     // hauteur imposée (sinon profil de démo)
        public int OutletCount;           // bouches réelles du déflecteur (émetteurs stock du pas), sur l'axe TrenchHeadingDeg
        public float OutletDistM;         // distance horizontale des bouches au centre du pas
        public float PadHeightM;          // table du pas surélevée au-dessus du terrain (bouches au niveau de la table, comme au KSC)
        public float ThinAir;             // air raréfié 0..1 (Duna ≈ 0,85) ; calculé depuis PressurePa si elle est donnée
        public float PressurePa = -1f, Gravity = -1f; // atmosphère et gravité imposées (Duna)
        public Vector3 HillPos; public float HillRadius, HillHeight; // butte dans le décor (test du relief)
        public float CameraAzimuthDeg = float.NaN, CameraDistM, CameraHeightM; // cadrage imposé (sinon automatique)
        public bool CompactPad; // banc : terrain 5 m devant la bouche, comme le pad mesuré en jeu
        public float IgnitionStepAtS = -1f, IgnitionBeforeMN, IgnitionAfterMN, IgnitionRampS;
        public bool MouthPad; // banc seulement : sorties couvertes, ouverture haute de 4,4 m
    }

    private static int Width = 960, Height = 540;

    public static void Run()
    {
        Width = Env("GE_RENDER_WIDTH", 960);
        Height = Env("GE_RENDER_HEIGHT", 540);
        string only = Environment.GetEnvironmentVariable("GE_RENDER_SCENARIO");
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        bool monitor = Width >= 1920;
        string outRoot = Path.Combine(root, "Tools", "render-tests", monitor ? "out-monitor" : "out");
        string outputOverride = Environment.GetEnvironmentVariable("GE_RENDER_OUTPUT");
        if (!string.IsNullOrEmpty(outputOverride)) outRoot = Path.GetFullPath(outputOverride);
        Directory.CreateDirectory(outRoot);
        // Diagnostic : comparer un bundle sauvegardé au courant, avec les mêmes entrées du banc.
        string bundlePath = Environment.GetEnvironmentVariable("GE_TEST_BUNDLE");
        if (string.IsNullOrEmpty(bundlePath)) bundlePath = Path.Combine(root, "GameData", "GroundBlastFx", "Shaders", "GroundBlastFx.unity3d");
        AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null) throw new Exception("Bundle introuvable");
        RenderCore.ForceAnalytic = Environment.GetEnvironmentVariable("GE_NO_SIM") == "1";
        // GE_NO_CLOUD_DEPTH=1 : sans la profondeur du nuage opaque (comportement 1.0.1), pour comparer.
        RenderCore.WriteCloudDepth = Environment.GetEnvironmentVariable("GE_NO_CLOUD_DEPTH") != "1";
        // GE_FIRST_SLOT=1..3 : le foyer du scénario prend un autre emplacement GPU que le 0 (en jeu, un 2ᵉ foyer).
        int firstSlot; RenderCore.FirstSlotForTests = int.TryParse(Environment.GetEnvironmentVariable("GE_FIRST_SLOT"), out firstSlot) ? firstSlot : 0;
        // Carte des hauteurs : rayons contre les colliders de la scène du banc (sol, pas, tour, barge, eau).
        RenderCore.Ground = Environment.GetEnvironmentVariable("GE_NO_GROUND") == "1" ? null : new HarnessGround();
        var core = new RenderCore(bundle);
        if (!core.IsAvailable) throw new Exception("RenderCore indisponible : " + core.Status);
        QualityLevel q = QualityLevel.Ultra;
        string qs = Environment.GetEnvironmentVariable("GE_QUALITY");
        if (!string.IsNullOrEmpty(qs)) q = (QualityLevel)Enum.Parse(typeof(QualityLevel), qs, true);
        var settings = new RendererSettings { Quality = q, MaxRenderedClusters = 4 };
        float globalIntensity;
        if (float.TryParse(Environment.GetEnvironmentVariable("GE_INTENSITY"), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out globalIntensity))
            settings.GlobalIntensity = Mathf.Clamp(globalIntensity, 0.25f, 2f);
        float visualGain;
        if (float.TryParse(Environment.GetEnvironmentVariable("GE_EFFECT_BRIGHTNESS"), System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out visualGain)) settings.EffectBrightness = VisualTuning.Brightness(visualGain);
        if (float.TryParse(Environment.GetEnvironmentVariable("GE_IGNITION_STRENGTH"), System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out visualGain)) settings.IgnitionStrength = VisualTuning.IgnitionStrength(visualGain);
        core.Configure(settings);
        float dbg; Shader.SetGlobalFloat("_GEDebugMode", float.TryParse(Environment.GetEnvironmentVariable("GE_DEBUG_MODE"), out dbg) ? dbg : 0f);

        var timing = new StringBuilder();
        var world = new World();
        // Échauffement : le ciel procédural et l'ambiant ne sont corrects qu'après quelques rendus.
        Scene[] all = Scenes();
        world.Setup(all[0], DemoProfiles.Create(all[0].Demo));
        for (int i = 0; i < 4; i++) { world.Camera.Render(); DynamicGI.UpdateEnvironment(); }
        try
        {
            foreach (Scene s in Scenes())
            {
                if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Replace(" ", "").Split(','), s.Id) < 0) continue; // « S3 » ou « S3,S7 »
                // GE_FRAMES=18;22;26 : instants de capture imposés (diagnostic), sinon ceux du scénario.
                string frames = Environment.GetEnvironmentVariable("GE_FRAMES");
                if (!string.IsNullOrEmpty(frames))
                    s.Frames = Array.ConvertAll(frames.Split(';'), v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
                // GE_SUN_ELEV=-12 : hauteur du soleil imposée (degrés), par exemple un décollage du pas de nuit.
                float sunElev;
                if (float.TryParse(Environment.GetEnvironmentVariable("GE_SUN_ELEV"), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out sunElev))
                    s.SunElevationDeg = sunElev;
                string dir = Path.Combine(outRoot, s.Id);
                Directory.CreateDirectory(dir);
                if (File.Exists(Path.Combine(dir, "frames.txt"))) File.Delete(Path.Combine(dir, "frames.txt"));
                foreach (string old in Directory.GetFiles(dir, "t*.png")) File.Delete(old); // images générées par un passage précédent
                // Isoler aussi l'état aléatoire des bouffées entre scénarios : Clear() ne rembobine pas leur RNG.
                // Sinon une impulsion dans S26 changeait les bouffées de S28, pourtant témoin sans impulsion.
                core.Dispose();
                core = new RenderCore(bundle);
                if (!core.IsAvailable) throw new Exception("RenderCore indisponible : " + core.Status);
                core.Configure(settings);
                double ms = RunScene(s, core, settings, world, dir);
                timing.AppendLine("| " + s.Id + " | " + s.Title + " | " + ms.ToString("F2") + " |");
            }
            File.WriteAllText(string.IsNullOrEmpty(outputOverride) ? Path.Combine(root, "Tools", "render-tests", monitor ? "timing-monitor.md" : "timing.md") : Path.Combine(outRoot, "timing.md"),
                "| Scénario | Titre | Coût GPU estimé du volume (ms/image, " + Width + "×" + Height + ", " + settings.Quality + ") |\n|---|---|---|\n" + timing);
            Debug.Log("[GroundBlastFx] RenderHarness terminé : " + outRoot);
        }
        finally
        {
            world.Dispose();
            core.Dispose();
            bundle.Unload(true);
        }
    }

    private static int Div(RendererSettings s) { return s.Quality == QualityLevel.Low ? 4 : 2; }

    private static int Env(string key, int fallback)
    {
        int v;
        return int.TryParse(Environment.GetEnvironmentVariable(key), out v) && v >= 128 && v <= 4096 ? v : fallback;
    }

    private static Scene[] Scenes()
    {
        Color deserts = new Color(0.86f, 0.74f, 0.55f), desertsB = new Color(0.70f, 0.58f, 0.41f);
        Color concrete = new Color(0.72f, 0.71f, 0.68f), concreteB = new Color(0.56f, 0.55f, 0.53f);
        Color regolith = new Color(0.52f, 0.52f, 0.51f), regolithB = new Color(0.38f, 0.38f, 0.37f);
        Color spray = new Color(0.92f, 0.94f, 0.96f), sprayB = new Color(0.80f, 0.84f, 0.88f);
        return new[]
        {
            new Scene { Id = "S1", Title = "Atterrissage kérolox sur terrain sec", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 30, SunAzimuthDeg = 140, Frames = new[] { 8f, 14f, 19f, 23f, 26f, 29.5f }, DustA = deserts, DustB = desertsB,
                Ground = new Color(0.78f, 0.66f, 0.49f), WindMs = 3f },
            new Scene { Id = "S2", Title = "Atterrissage méthalox sur barge", Demo = DemoKind.S2Barge, Surface = SurfaceKind.VesselDeck,
                SunElevationDeg = 45, SunAzimuthDeg = 60, Frames = new[] { 12f, 18f, 22f, 25f, 28f, 32f }, DustA = new Color(0.62f, 0.62f, 0.62f),
                DustB = new Color(0.46f, 0.46f, 0.46f), Ground = new Color(0.10f, 0.20f, 0.27f), Water = true, Barge = true, WindMs = 6f,
                FlameColor = new Color(0.60f, 0.70f, 1.00f), FlameIntensity = 0.7f, SteamBonus = 0.1f },
            new Scene { Id = "S3", Title = "Décollage neuf moteurs, déluge, tranchée orientée", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 40, SunAzimuthDeg = 110, Frames = new[] { 1.5f, 4f, 7f, 11f, 18f, 40f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.55f, 0.53f, 0.48f), Pad = true, TrenchHeadingDeg = 0f, WindMs = 3f, CameraScale = 1.5f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f },
            new Scene { Id = "S4", Title = "Stress test 33 moteurs méthalox", Demo = DemoKind.S4Stress33, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 50, SunAzimuthDeg = 200, Frames = new[] { 2f, 5f, 8f, 12f, 20f, 45f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.60f, 0.56f, 0.49f), Pad = true, TrenchHeadingDeg = 270f, WindMs = 4f, CameraScale = 0.7f, OutletCount = 1, OutletDistM = 38f, PadHeightM = 6f,
                FlameColor = new Color(0.60f, 0.70f, 1.00f), FlameIntensity = 0.7f, SteamBonus = 0.1f },
            new Scene { Id = "S5", Title = "Alunissage, soleil rasant", Demo = DemoKind.S5Moon, Surface = SurfaceKind.Terrain, Vacuum = true,
                SunElevationDeg = 10, SunAzimuthDeg = 120, Frames = new[] { 6f, 12f, 18f, 24f, 29f, 32f }, DustA = regolith, DustB = regolithB,
                Ground = new Color(0.46f, 0.46f, 0.45f), FlameColor = new Color(1f, 0.58f, 0.48f), FlameIntensity = 0.5f, CameraScale = 0.12f,
                CameraHeightScale = 1.6f },
            new Scene { Id = "S6", Title = "Atterrissage de nuit", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = -12, SunAzimuthDeg = 140, Frames = new[] { 8f, 14f, 19f, 23f, 26f, 29.5f }, DustA = deserts, DustB = desertsB,
                Ground = new Color(0.78f, 0.66f, 0.49f), WindMs = 3f },
            new Scene { Id = "S7", Title = "Stationnaire à 20 m au-dessus de l'eau", Demo = DemoKind.S7Water, Surface = SurfaceKind.Water,
                SunElevationDeg = 35, SunAzimuthDeg = 150, Frames = new[] { 1f, 3f, 6f, 12f, 20f, 29f }, DustA = spray, DustB = sprayB,
                Ground = new Color(0.08f, 0.19f, 0.26f), Water = true, WindMs = 5f },
            new Scene { Id = "S8", Title = "Coupure au sol puis 90 s de dissipation", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 30, SunAzimuthDeg = 140, Frames = new[] { 29.5f, 35f, 45f, 60f, 90f, 120f }, DustA = deserts, DustB = desertsB,
                Ground = new Color(0.78f, 0.66f, 0.49f), WindMs = 4f },
            new Scene { Id = "S9", Title = "Décollage qui bascule : le nuage reste sur place", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 40, SunAzimuthDeg = 110, Frames = new[] { 4f, 8f, 12f, 16f, 22f, 35f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.55f, 0.53f, 0.48f), Pad = true, TrenchHeadingDeg = 40f, WindMs = 3f, CameraScale = 0.9f, DriftAccel = new Vector3(0.45f, 0f, 0.15f), CameraPitchUp = 2.6f },
            new Scene { Id = "S10", Title = "Atterrissage vu d'en haut (animation 2 s)", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 35, SunAzimuthDeg = 140, Frames = new[] { 20f, 20.5f, 21f, 21.5f, 22f, 26f }, DustA = deserts, DustB = desertsB,
                Ground = new Color(0.78f, 0.66f, 0.49f), WindMs = 3f, CameraPitchUp = 3.2f, DriftAccel = new Vector3(0.12f, 0f, 0f) },
            new Scene { Id = "S11", Title = "Traces de brûlure sur herbe, vue rapprochée après dissipation", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 40, SunAzimuthDeg = 140, Frames = new[] { 26f, 29.5f, 45f, 70f, 100f, 130f }, DustA = new Color(0.62f, 0.56f, 0.44f),
                DustB = new Color(0.48f, 0.43f, 0.33f), Ground = new Color(0.36f, 0.48f, 0.22f), WindMs = 5f, CameraScale = 0.14f, CameraPitchUp = 7f },
            new Scene { Id = "S12", Title = "Décollage neuf moteurs vu de haut (plus de donut quand la fusée monte)", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 45, SunAzimuthDeg = 120, Frames = new[] { 3f, 7f, 11f, 16f, 22f, 32f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.55f, 0.53f, 0.48f), Pad = true, TrenchHeadingDeg = 0f, WindMs = 3f, CameraScale = 0.9f, CameraPitchUp = 3.2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f },
            new Scene { Id = "S13", Title = "Stationnaire à 40 m au-dessus de l'eau, deux moteurs", Demo = DemoKind.S7Water, Surface = SurfaceKind.Water,
                SunElevationDeg = 40, SunAzimuthDeg = 150, Frames = new[] { 1.5f, 4f, 8f, 14f, 22f, 29f }, DustA = spray, DustB = sprayB,
                Ground = new Color(0.08f, 0.19f, 0.26f), Water = true, WindMs = 5f, ThrustScale = 2.2f, StandoffM = 40f, CameraPitchUp = 1.6f },
            new Scene { Id = "S14", Title = "Décollage hors pas sur terrain sec", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 40, SunAzimuthDeg = 110, Frames = new[] { 1.5f, 4f, 7f, 11f, 18f, 40f }, DustA = deserts, DustB = desertsB,
                Ground = new Color(0.78f, 0.66f, 0.49f), WindMs = 4f, CameraScale = 0.8f },
            new Scene { Id = "S15", Title = "Décollage : animation 2,5 s (le nuage sort des bouches et bouillonne)", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 35, SunAzimuthDeg = 100, Frames = new[] { 2f, 2.5f, 3f, 3.5f, 4f, 4.5f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.55f, 0.53f, 0.48f), Pad = true, TrenchHeadingDeg = 0f, WindMs = 3f, CameraScale = 0.75f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f },
            new Scene { Id = "S16", Title = "Alunissage vu de la caméra de poursuite (poussière visible dès ~30 m)", Demo = DemoKind.S5Moon, Surface = SurfaceKind.Terrain, Vacuum = true,
                SunElevationDeg = 25, SunAzimuthDeg = 200, Frames = new[] { 6f, 10f, 14f, 20f, 25f, 29f }, DustA = regolith, DustB = regolithB,
                Ground = new Color(0.46f, 0.46f, 0.45f), FlameColor = new Color(1f, 0.58f, 0.48f), FlameIntensity = 0.5f, CameraScale = 0.16f, CameraPitchUp = 2.0f },
            new Scene { Id = "S17", Title = "Pas du KSC vu de côté : jets des bouches (comme la capture de référence)", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 40, SunAzimuthDeg = 115, Frames = new[] { 1.5f, 2.5f, 3.5f, 5f, 8f, 14f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                CameraAzimuthDeg = 90f, CameraDistM = 260f, CameraHeightM = 70f },
            new Scene { Id = "S18", Title = "Atterrissage près d'une butte : la poussière épouse le relief", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 35, SunAzimuthDeg = 140, Frames = new[] { 19f, 23f, 26f, 29.5f, 40f, 55f }, DustA = deserts, DustB = desertsB,
                Ground = new Color(0.78f, 0.66f, 0.49f), WindMs = 2f, HillPos = new Vector3(55f, 0f, -10f), HillRadius = 45f, HillHeight = 22f,
                CameraAzimuthDeg = 200f, CameraDistM = 190f, CameraHeightM = 45f },
            new Scene { Id = "S19", Title = "Atterrissage sur Duna (air raréfié : nuage + nappe de grains)", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 30, SunAzimuthDeg = 140, Frames = new[] { 14f, 19f, 23f, 26f, 29.5f, 40f }, DustA = new Color(0.80f, 0.53f, 0.36f),
                DustB = new Color(0.63f, 0.40f, 0.27f), Ground = new Color(0.62f, 0.36f, 0.22f), WindMs = 4f, PressurePa = 6700f, Gravity = 2.94f },
            new Scene { Id = "S20", Title = "Mün : traces laissées par l'atterrissage", Demo = DemoKind.S5Moon, Surface = SurfaceKind.Terrain, Vacuum = true,
                SunElevationDeg = 25, SunAzimuthDeg = 200, Frames = new[] { 24f, 29f, 32f, 40f, 60f, 90f }, DustA = regolith, DustB = regolithB,
                Ground = new Color(0.60f, 0.60f, 0.59f), FlameColor = new Color(1f, 0.58f, 0.48f), FlameIntensity = 0.5f,
                CameraAzimuthDeg = 230f, CameraDistM = 70f, CameraHeightM = 40f },
            new Scene { Id = "S21", Title = "Atterrissage sur la banquise (neige soufflée : nuage blanc)", Demo = DemoKind.S1Landing, Surface = SurfaceKind.Terrain,
                SunElevationDeg = 22, SunAzimuthDeg = 150, Frames = new[] { 14f, 19f, 23f, 26f, 29.5f, 45f }, DustA = new Color(0.93f, 0.95f, 0.98f),
                DustB = new Color(0.80f, 0.85f, 0.92f), Ground = new Color(0.86f, 0.89f, 0.93f), WindMs = 5f, SteamBonus = 0.45f },
            new Scene { Id = "S22", Title = "Pad réel : raccord bas et coupure à 8 s, vue rasante", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 0.3f, 1f, 3f, 7.9f, 8.1f, 8.5f, 9f, 10f, 12f, 20f, 40f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 90f, CameraDistM = 360f, CameraHeightM = 12f },
            new Scene { Id = "S23", Title = "Pad réel : mêmes instants, vue plongeante", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 0.3f, 1f, 3f, 7.9f, 8.1f, 8.5f, 9f, 10f, 12f, 20f, 40f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 90f, CameraDistM = 220f, CameraHeightM = 260f },
            new Scene { Id = "S24", Title = "Bouche couverte : départ de flamme, vue proche", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 0.3f, 1f, 3f, 7.9f, 8.5f, 12f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, MouthPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 55f, CameraDistM = 90f, CameraHeightM = 7f },
            new Scene { Id = "S25", Title = "Bouche couverte : mêmes instants, vue plongeante", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 0.3f, 1f, 3f, 7.9f, 8.5f, 12f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, MouthPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 55f, CameraDistM = 95f, CameraHeightM = 80f },
            new Scene { Id = "S26", Title = "Allumage boosters après moteurs liquides, vue rasante", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 2.9f, 3.0f, 3.1f, 3.2f, 3.4f, 3.8f, 4.3f, 6f, 8.3f, 10f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, MouthPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 90f, CameraDistM = 360f, CameraHeightM = 12f,
                IgnitionStepAtS = 3f, IgnitionBeforeMN = 4f, IgnitionAfterMN = 35f, IgnitionRampS = 0f },
            new Scene { Id = "S27", Title = "Même allumage, vue plongeante", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 2.9f, 3.0f, 3.1f, 3.2f, 3.4f, 3.8f, 4.3f, 6f, 8.3f, 10f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, MouthPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 90f, CameraDistM = 280f, CameraHeightM = 260f,
                IgnitionStepAtS = 3f, IgnitionBeforeMN = 4f, IgnitionAfterMN = 35f, IgnitionRampS = 0f },
            new Scene { Id = "S28", Title = "Rampe lente de poussée, témoin sans déflagration", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 2.9f, 3.0f, 3.1f, 3.2f, 3.4f, 3.8f, 4.3f, 6f, 8.3f, 10f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, MouthPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 90f, CameraDistM = 360f, CameraHeightM = 12f,
                IgnitionStepAtS = 3f, IgnitionBeforeMN = 4f, IgnitionAfterMN = 35f, IgnitionRampS = 3f },
            new Scene { Id = "S29", Title = "Premier allumage à froid, vue rasante", Demo = DemoKind.S3PadLaunch, Surface = SurfaceKind.LaunchPad,
                SunElevationDeg = 72f, SunAzimuthDeg = 115f, Frames = new[] { 2.9f, 3.0f, 3.1f, 3.2f, 3.4f, 3.8f, 4.3f, 6f, 8.3f, 10f }, DustA = concrete, DustB = concreteB,
                Ground = new Color(0.40f, 0.50f, 0.28f), Pad = true, CompactPad = true, MouthPad = true, TrenchHeadingDeg = 0f, WindMs = 2f, OutletCount = 2, OutletDistM = 12f, PadHeightM = 5f,
                StandoffM = 10f, CutoffAtS = 8f, CameraAzimuthDeg = 90f, CameraDistM = 360f, CameraHeightM = 12f,
                IgnitionStepAtS = 3f, IgnitionBeforeMN = 0f, IgnitionAfterMN = 35f, IgnitionRampS = 0f },
        };
    }

    // ------------------------------------------------------------------------------------------------
    // Simulation d'un foyer à partir du modèle physique du Core (même formules que ClusterTracker).
    // ------------------------------------------------------------------------------------------------
    private sealed class Sim
    {
        public readonly PhysicsParams P = new PhysicsParams();
        public DemoScenario Sc;
        public Scene S;
        public float Front, Intensity, IntensityAtCutoff, CutoffTime = -1f, Dissipation, FrontSpeedAtCutoff, MaxCloud, Flame;
        public float Dose, ScorchRadius;
        public bool Active;
        public ImpingementCluster C;
        public float Standoff;
        public float Envelope, WallJet, Thrust, Activation, Source;
        public Vector3 Anchor, Impact;
        public bool AnchorSet;

        public void Step(float t, float dt)
        {
            DemoState st;
            DemoProfiles.Evaluate(Sc, t, out st);
            bool on = st.EnginesOn && (S.CutoffAtS < 0 || t < S.CutoffAtS);
            float thrustScale = S.ThrustScale;
            float ignitionAge = t;
            if (S.IgnitionStepAtS >= 0f)
            {
                // Entrées synthétiques : 4 MN de moteurs liquides, puis saut à 35 MN. Ce n'est pas une télémétrie NASA.
                float fraction = S.IgnitionRampS > 0f ? Mathf.Clamp01((t - S.IgnitionStepAtS) / S.IgnitionRampS) : (t >= S.IgnitionStepAtS ? 1f : 0f);
                float thrust = Mathf.Lerp(S.IgnitionBeforeMN, S.IgnitionAfterMN, fraction) * 1000000f;
                on = thrust > 0f && (S.CutoffAtS < 0f || t < S.CutoffAtS);
                st.Throttle01 = 1f;
                thrustScale = thrust / (Sc.ThrustPerEngineN * Sc.EngineCount);
                ignitionAge = S.IgnitionBeforeMN > 0f ? t + 2f : Mathf.Max(t - S.IgnitionStepAtS, 0f);
            }
            float pAmb = S.Vacuum ? 0f : S.PressurePa > 0f ? S.PressurePa : 101325f * (S.Water ? 1f : 0.9f);
            Standoff = S.StandoffM > 0f ? S.StandoffM : Mathf.Max(st.StandoffM, 0.5f);
            if (on)
            {
                Thrust = Sc.ThrustPerEngineN * st.Throttle01 * Sc.EngineCount * thrustScale;
                float ve = PlumeModel.ExhaustVelocity(Sc.IspS);
                var input = new JetInput { ThrustN = Sc.ThrustPerEngineN * st.Throttle01 * thrustScale, ExhaustVelocityMs = ve, ExitRadiusM = Sc.ExitRadiusM, StandoffM = Standoff, CosAlpha = 1f, AmbientPressurePa = pAmb };
                JetImpingement j;
                PlumeModel.Evaluate(ref input, P, out j);
                Envelope = j.ImpingementRadiusM + (Sc.EngineCount > 1 ? Sc.NozzleSpanM - Sc.ExitRadiusM : 0f);
                // Comme ImpactAggregator en jeu : souffle du foyer entier selon sa poussée totale.
                WallJet = PlumeModel.GroupWallJetVelocity(Thrust, ve, Standoff, pAmb, j.WallJetVelocityMs, P);
                float hAct = PlumeModel.ActivationHeight(Thrust, pAmb, P);
                Activation = PlumeModel.ActivationFade(Standoff, hAct, P);
            }
            bool impinging = on && Activation > 0.001f;
            if (impinging && !Active && CutoffTime < 0f) { Active = true; Front = Envelope; }
            if (impinging && Active)
            {
                float rmax = Mathf.Max(PlumeModel.MaxCloudRadius(Thrust, P), 1.5f * Envelope);
                if (S.Vacuum) rmax = Mathf.Max(rmax, Mathf.Min(0.25f * VacuumEjectaModel.BallisticRange(VacuumEjectaModel.CharacteristicSpeed(WallJet, P), VacuumEjectaModel.SheetAngleDeg(Standoff, Sc.ExitRadiusM, P), 1.62f), 600f));
                MaxCloud = Mathf.Max(MaxCloud, rmax);
                Front = CloudFrontModel.AdvanceActive(Front, Envelope, WallJet, MaxCloud, dt, P);
                SurfaceKind k = S.Surface;
                float erod = k == SurfaceKind.LaunchPad ? 0.15f : k == SurfaceKind.Water ? 0f : k == SurfaceKind.VesselDeck ? 0.05f : 1f;
                float thr = k == SurfaceKind.LaunchPad ? 120f : k == SurfaceKind.Water ? 20f : k == SurfaceKind.VesselDeck ? 150f : (S.Vacuum ? 24f : 40f);
                float steam = S.Vacuum ? 0f : Mathf.Clamp01((k == SurfaceKind.LaunchPad ? 0.9f : k == SurfaceKind.Water ? 0.8f : k == SurfaceKind.VesselDeck ? 0.4f : 0.05f) + S.SteamBonus);
                float lift = PlumeModel.DustLift(WallJet, thr, erod, P);
                float steamVis = PlumeModel.SteamVisibility(WallJet, steam, P);
                float target = Activation * Mathf.Max(lift, steamVis) * CloudFrontModel.IgnitionFade(ignitionAge, P);
                Source = GeMath.Approach(Source, target, P.IntensitySmoothingS, dt);
                float tau = target >= Intensity ? P.IntensitySmoothingS : Mathf.Max(CloudFrontModel.DissipationTime(Front, P) / 3f, P.IntensitySmoothingS);
                if (S.Vacuum) tau = P.IntensitySmoothingS;
                Intensity = GeMath.Approach(Intensity, target, tau, dt);
                Flame = GeMath.Approach(Flame, PlumeModel.FlameLightIntensity(Thrust, S.FlameIntensity, P) * Activation, 0.1f, dt);
                float ps = PlumeModel.ImpingementPressure(Thrust, 1f, Envelope, P);
                Dose = ScorchModel.AddDose(Dose, S.Vacuum ? P.ScorchPressureRefPa * 0.5f : ps, Activation, dt, P);
                if (S.Vacuum ? Activation > 0.5f : ps > P.ScorchPressureRefPa) ScorchRadius = Mathf.Max(ScorchRadius, ScorchModel.Radius(S.Vacuum, Envelope, Front, P));
                C.SteamFraction01 = steam;
                C.Erodibility01 = erod;
            }
            else if (Active)
            {
                Active = false;
                CutoffTime = t;
                FrontSpeedAtCutoff = CloudFrontModel.FrontSpeed(Front, Envelope, WallJet, MaxCloud, P);
                Dissipation = CloudFrontModel.DissipationTime(Front, P) * (S.Surface == SurfaceKind.LaunchPad ? P.PadSteamDissipationFactor : 1f);
                IntensityAtCutoff = Intensity;
                Source = 0f;
            }
            if (!Active && CutoffTime >= 0f)
            {
                float tc = t - CutoffTime;
                if (S.Vacuum) Intensity = IntensityAtCutoff * CloudFrontModel.VacuumCutoffFade(tc, P);
                else
                {
                    Front = CloudFrontModel.AdvanceAfterCutoff(Front, FrontSpeedAtCutoff, tc, MaxCloud, dt, P);
                    Intensity = IntensityAtCutoff * CloudFrontModel.CutoffFade(tc, Dissipation);
                }
                Flame = GeMath.Approach(Flame, 0f, P.FlameLightFadeS, dt);
            }

            // --- Structure du contrat ---
            C.Id = 100 + int.Parse(S.Id.Substring(1)); // un Id par scénario : chaque scène repart d'une grille vide
            C.EnginesActive = Active;
            C.TimeSinceIgnitionS = ignitionAge;
            C.TimeSinceCutoffS = Active || CutoffTime < 0 ? 0f : t - CutoffTime;
            C.IsDemo = true;
            float td = Mathf.Max(t - 2f, 0f);
            Impact = (S.Barge ? new Vector3(0, 3f, 0) : new Vector3(0, S.PadHeightM, 0)) + S.DriftAccel * (0.5f * td * td);
            if (!AnchorSet && Active) { AnchorSet = true; Anchor = Impact; }
            C.ImpactPointWorld = Impact;
            C.CloudAnchorWorld = AnchorSet ? Anchor : Impact;
            C.CloudNorthWorld = Vector3.forward;
            C.Source01 = Mathf.Clamp01(Source);
            C.DissipationTimeS = Active ? CloudFrontModel.DissipationTime(Front, P) * (S.Surface == SurfaceKind.LaunchPad ? P.PadSteamDissipationFactor : 1f) : Dissipation;
            C.Visibility01 = Active || CutoffTime < 0f ? (AnchorSet ? 1f : 0f) : (S.Vacuum ? Mathf.Clamp01(Intensity * 4f) : Mathf.Clamp01((Dissipation - (t - CutoffTime)) / (0.25f * Dissipation)));
            C.SurfaceNormalWorld = Vector3.up;
            C.CloudUpWorld = Vector3.up;
            C.ThinAir01 = S.Vacuum ? 1f : S.PressurePa > 0f ? 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2000f, 30000f, S.PressurePa)) : S.ThinAir;
            C.PlumeAxisWorld = Vector3.down;
            C.NozzleCenterWorld = C.ImpactPointWorld + Vector3.up * Standoff;
            C.PlumeAxisWorld = S.DriftAccel.sqrMagnitude > 0f ? (Vector3.down + S.DriftAccel.normalized * Mathf.Min(td * 0.02f, 0.3f)).normalized : Vector3.down;
            C.StandoffM = Standoff;
            C.ImpingementRadiusM = Envelope;
            C.CloudFrontRadiusM = Front;
            C.MaxCloudRadiusM = MaxCloud;
            C.Surface = S.Surface;
            C.Medium = S.Vacuum ? MediumKind.Vacuum : MediumKind.Atmosphere;
            C.EngineCount = Sc.EngineCount;
            C.TotalThrustN = Active ? Thrust : 0f;
            C.WallJetVelocityMs = Active ? WallJet : 0f;
            C.AmbientPressurePa = S.Vacuum ? 0f : S.PressurePa > 0f ? S.PressurePa : 91500f;
            C.GravityMs2 = S.Gravity > 0f ? S.Gravity : S.Vacuum ? 1.62f : 9.81f;
            C.WindWorldMs = S.Vacuum ? Vector3.zero : new Vector3(0.8f, 0f, 0.6f) * S.WindMs;
            C.Intensity01 = Mathf.Clamp01(Intensity);
            C.DustAlbedoA = S.DustA;
            C.DustAlbedoB = S.DustB;
            C.TrenchDirectionWorld = S.TrenchHeadingDeg >= 0 ? new Vector3(Mathf.Sin(S.TrenchHeadingDeg * Mathf.Deg2Rad), 0, Mathf.Cos(S.TrenchHeadingDeg * Mathf.Deg2Rad)) : Vector3.zero;
            // Bouches réelles (comme LaunchPadRegistry en jeu) : au niveau du terrain, au bord du pas.
            C.DeflectorOutletCount = S.OutletCount;
            // Comme au KSC (journal du 25/09) : bouches au niveau de la table, nord et sud ; sol plus bas au-delà du bord
            // (profil que LaunchPadRegistry mesure en jeu par rayons verticaux).
            C.DeflectorOutlet0World = C.TrenchDirectionWorld * S.OutletDistM + Vector3.up * S.PadHeightM;
            C.DeflectorOutlet1World = -C.TrenchDirectionWorld * S.OutletDistM + Vector3.up * S.PadHeightM;
            float padHalf = 3f * Mathf.Max(Sc.NozzleSpanM * 2.6f, 12f);
            float edge = S.CompactPad ? 5f : Mathf.Max(padHalf - S.OutletDistM, 3f);
            C.DeflectorGround = S.OutletCount > 0 && S.PadHeightM > 0.5f ? new Vector4(S.PadHeightM, edge, S.PadHeightM, edge) : Vector4.zero;
            C.FlameLightColor = S.FlameColor;
            C.FlameLightIntensity = Flame;
            C.VacuumEjectaSpeedMs = VacuumEjectaModel.CharacteristicSpeed(WallJet, P);
            C.VacuumEjectaAngleDeg = VacuumEjectaModel.SheetAngleDeg(Standoff, Sc.ExitRadiusM, P);
            C.LodLevel = 0;
        }
    }

    private static double RunScene(Scene s, RenderCore core, RendererSettings settings, World world, string dir)
    {
        var sim = new Sim { S = s, Sc = DemoProfiles.Create(s.Demo) };
        if (s.CompactPad) File.WriteAllText(Path.Combine(dir, "pad-state.csv"), "time,jetLength,jetFeed,cutFront,exitSpeed,puffCount,maxPuffRadius,puffDataSha256\n");
        if (s.IgnitionStepAtS >= 0f) File.WriteAllText(Path.Combine(dir, "ignition.csv"), "time,thrustN,pulse,exitSpeed,halfWidth,jetLength,jetFeed,cutFront,puffCount,maxPuffRadius,channel,source,meanPuffSpeed,maxPuffSpeed,maxPuffTop\n");
        world.Setup(s, sim.Sc);
        Camera cam = world.Camera;
        var cmd = new CommandBuffer { name = "GroundBlastFx harnais" };
        cam.AddCommandBuffer(RenderCore.Event, cmd);
        var clusters = new ImpingementCluster[1];
        var marks = new ScorchMark[1];
        const float dt = 1f / 30f;
        float t = 0f;
        double gpuMs = 0;
        int gpuSamples = 0;
        int lastCount = 0, lastMarkCount = 0;
        try
        {
            foreach (float frame in s.Frames)
            {
                while (t < frame - 1e-4f)
                {
                    sim.Step(t, dt);
                    clusters[0] = sim.C;
                    int markCount = sim.Dose > 0.05f && !s.Water ? 1 : 0;
                    marks[0] = new ScorchMark
                    {
                        Id = 1, CenterWorld = sim.C.ImpactPointWorld, NormalWorld = Vector3.up,
                        RadiusM = sim.ScorchRadius,
                        Strength01 = ScorchModel.Strength(sim.Dose, sim.P), Surface = s.Surface,
                        Medium = s.Vacuum ? MediumKind.Vacuum : MediumKind.Atmosphere, AgeS = sim.Active ? 0f : t - sim.CutoffTime
                    };
                    RenderEnvironment env = world.Environment(s, t, dt);
                    int count = sim.C.Visibility01 > 0.001f || sim.Active ? 1 : 0;
                    core.Prepare(cam, clusters, count, marks, markCount, settings, ref env);
                    if (s.IgnitionStepAtS >= 0f) WriteIgnitionState(core, sim.C, t, dir);
                    lastCount = count; lastMarkCount = markCount;
                    t += dt;
                }
                world.Frame(s, sim, core, t);
                // La caméra vient d'être placée : on recalcule les rayons et uniformes pour cette position (sans avancer le temps).
                RenderEnvironment envNow = world.Environment(s, t, 0f);
                core.Prepare(cam, clusters, lastCount, marks, lastMarkCount, settings, ref envNow);
                if (s.CompactPad) WritePadState(core, t, dir);
                File.AppendAllText(Path.Combine(dir, "frames.txt"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "t={0:F1} actif={1} h={2:F1} I={3:F2} R={4:F1}/{5:F1} r_i={6:F1} act={7:F2} u_i={8:F0} F={9:F0}kN flamme={10:F2} dose={11:F2}\n",
                    t, sim.Active, sim.Standoff, sim.Intensity, sim.Front, sim.MaxCloud, sim.Envelope, sim.Activation, sim.WallJet, sim.Thrust / 1000f, sim.Flame, sim.Dose));
                core.Record(cmd, Width, Height, Div(settings));
                world.Rocket(s, sim);
                // Les séries de chronométrage avancent le champ GPU : les exclure des captures de diagnostic temporel.
                if (Environment.GetEnvironmentVariable("GE_CAPTURE_ONLY") != "1")
                {
                    gpuMs += world.MeasureVolumeCost(cmd, core, Width, Height, Div(settings));
                    File.AppendAllText(Path.Combine(dir, "frames.txt"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "   dont simulation GPU (compute) : {0:F2} ms\n", world.LastSimMs));
                    gpuSamples++;
                }
                core.Record(cmd, Width, Height, 2);
                if (frame == s.Frames[0]) world.Capture(null); // première image jetée : ciel et ambiant pas encore stables
                world.Capture(Path.Combine(dir, "t" + frame.ToString("000.0").Replace('.', '_').Replace(',', '_') + ".png"));
            }
        }
        finally
        {
            cam.RemoveCommandBuffer(RenderCore.Event, cmd);
            cmd.Release();
        }
        return gpuSamples > 0 ? gpuMs / gpuSamples : 0;
    }

    // Diagnostic du banc seulement : identité des bouffées reçues par le GPU et trajet de la coupure.
    // La réflexion évite d'ajouter une API de test à la DLL du jeu.
    private static void WriteIgnitionState(RenderCore core, ImpingementCluster cluster, float t, string dir)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        int s = RenderCore.FirstSlotForTests;
        Vector4 fx = ((Vector4[])typeof(RenderCore).GetField("_flowX", flags).GetValue(core))[s];
        Vector4 jx = ((Vector4[])typeof(RenderCore).GetField("_jetX", flags).GetValue(core))[s];
        Vector4[] puffs = (Vector4[])typeof(RenderCore).GetField("_puffData", flags).GetValue(core);
        int count = 0; float radius = 0f;
        for (int i = s * 320; i < (s + 1) * 320; i += 2)
            if (puffs[i].w > 0f) { count++; radius = Mathf.Max(radius, puffs[i].w); }
        PadPuffs model = ((PadPuffs[])typeof(RenderCore).GetField("_puffs", flags).GetValue(core))[s];
        int alive = 0; float speedSum = 0f, speedMax = 0f, top = 0f;
        if (model != null) foreach (PadPuffs.Puff q in model.Items)
        {
            if (!q.Alive) continue;
            float speed = Mathf.Sqrt(q.Vx * q.Vx + q.Vz * q.Vz);
            alive++; speedSum += speed; speedMax = Mathf.Max(speedMax, speed);
            top = Mathf.Max(top, q.Y + q.Radius * q.Tall);
        }
        var field = typeof(RenderCore).GetField("_thrustPulse", flags);
        float pulse = 0f;
        if (field != null)
        {
            object item = ((Array)field.GetValue(core)).GetValue(s);
            pulse = (float)item.GetType().GetProperty("Value").GetValue(item, null);
        }
        File.AppendAllText(Path.Combine(dir, "ignition.csv"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F4},{1:F2},{2:F5},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4},{8},{9:F4},{10:F4},{11:F4},{12:F4},{13:F4},{14:F4}\n",
            t, cluster.TotalThrustN, pulse, fx.y, fx.z, jx.x, jx.y, jx.w, count, radius, fx.x, cluster.Source01, alive > 0 ? speedSum / alive : 0f, speedMax, top));
    }

    private static void WritePadState(RenderCore core, float t, string dir)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        Vector4[] puffs = (Vector4[])typeof(RenderCore).GetField("_puffData", flags).GetValue(core);
        Vector4[] jets = (Vector4[])typeof(RenderCore).GetField("_jetX", flags).GetValue(core);
        Vector4[] flows = (Vector4[])typeof(RenderCore).GetField("_flowX", flags).GetValue(core);
        int slot = RenderCore.FirstSlotForTests;
        int count = 0; float radius = 0f;
        string hash;
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                for (int i = slot * 320; i < (slot + 1) * 320; i++)
                {
                    Vector4 p = puffs[i]; writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); writer.Write(p.w);
                    if ((i & 1) == 0 && p.w > 0f) { count++; radius = Mathf.Max(radius, p.w); }
                }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "");
        }
        var motionField = typeof(RenderCore).GetField("_jetMotion", flags);
        float speed = motionField != null ? ((Vector4[])motionField.GetValue(core))[slot].x : flows[slot].y;
        Vector4 j = jets[slot];
        File.AppendAllText(Path.Combine(dir, "pad-state.csv"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F3},{1:F4},{2:F4},{3:F4},{4:F4},{5},{6:F4},{7}\n", t, j.x, j.y, j.w, speed, count, radius, hash));
    }

    // ------------------------------------------------------------------------------------------------
    // Scène Unity : ciel procédural, sol texturé, lanceur, pas de tir, barge, eau.
    // ------------------------------------------------------------------------------------------------
    /// <summary>Relevé du sol du banc : rayons contre les colliders (la fusée est sur le calque « Ignore Raycast »).</summary>
    private sealed class HarnessGround : RenderCore.IGroundSampler
    {
        public void Cast(Vector3[] origins, int count, Vector3 down, float maxDist, float[] outDist)
        {
            Physics.SyncTransforms();
            RaycastHit hit;
            for (int i = 0; i < count; i++)
                outDist[i] = Physics.Raycast(origins[i], down, out hit, maxDist, ~(1 << 2), QueryTriggerInteraction.Ignore) ? hit.distance : -1f;
        }
    }

    private sealed class World : IDisposable
    {
        public Camera Camera;
        private readonly GameObject _camGo, _sunGo, _flameGo, _ground, _rocket, _water, _barge, _tower, _pad, _hill, _mouthPad;
        private readonly Light _sun, _flame;
        private readonly Material _groundMat, _rocketMat, _waterMat, _bargeMat, _padMat, _sky;
        private readonly Texture2D _groundTex, _pixels;
        private readonly RenderTexture _target;

        public World()
        {
            _camGo = new GameObject("GE camera");
            Camera = _camGo.AddComponent<Camera>();
            Camera.depthTextureMode = DepthTextureMode.Depth;
            Camera.clearFlags = CameraClearFlags.Skybox;
            Camera.fieldOfView = 50;
            Camera.nearClipPlane = 0.3f;
            Camera.farClipPlane = 20000f;
            Camera.allowHDR = true;
            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf) { antiAliasing = 1 };
            _target.Create();
            Camera.targetTexture = _target;
            _pixels = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            // GE_TEST_SKYWALL=1 (1.0.2) : mur rouge lointain dessiné APRÈS nos nuages (file transparente 2998), comme le ciel
            // de Scatterer : là où le nuage n'écrit pas sa profondeur, le mur passe devant lui.
            // GE_TEST_LATESMOKE=1 (1.0.2) : fumée d'un autre mod dessinée après nos nuages et calée sur _CameraDepthTexture
            // (bandeau bleu à GE_TEST_WALL_DEPTH mètres, 300 par défaut) : notre nuage doit la cacher quand elle est derrière.
            if (System.Environment.GetEnvironmentVariable("GE_TEST_LATESMOKE") == "1")
            {
                var smokeMat = new Material(Shader.Find("Hidden/GE TestLateSmoke"));
                float wallDepth;
                smokeMat.SetFloat("_GETestWallDepth", float.TryParse(System.Environment.GetEnvironmentVariable("GE_TEST_WALL_DEPTH"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out wallDepth) ? wallDepth : 300f);
                var late = new CommandBuffer { name = "GE test : fumée d'un autre mod" };
                late.Blit(Texture2D.blackTexture, BuiltinRenderTextureType.CameraTarget, smokeMat);
                Camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, late);
            }
            if (System.Environment.GetEnvironmentVariable("GE_TEST_SKYWALL") == "1")
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.DestroyImmediate(wall.GetComponent<Collider>());
                wall.name = "GE test : ciel dessiné après nous";
                wall.transform.SetParent(_camGo.transform, false);
                wall.transform.localPosition = new Vector3(0f, 0f, 15000f);
                wall.transform.localScale = new Vector3(40000f, 20000f, 1f);
                var wallMat = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.9f, 0.1f, 0.1f), renderQueue = 2998 };
                wall.GetComponent<MeshRenderer>().sharedMaterial = wallMat;
            }

            _sunGo = new GameObject("GE sun");
            _sun = _sunGo.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.shadows = LightShadows.Soft;
            RenderSettings.sun = _sun;
            _sky = new Material(Shader.Find("Skybox/Procedural"));
            RenderSettings.skybox = _sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;

            _flameGo = new GameObject("GE flame");
            _flame = _flameGo.AddComponent<Light>();
            _flame.type = LightType.Point;
            _flame.shadows = LightShadows.None;

            _groundTex = GroundTexture();
            _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            _ground.transform.localScale = new Vector3(2000, 1, 2000);
            _groundMat = new Material(Shader.Find("Standard")) { mainTexture = _groundTex, mainTextureScale = new Vector2(900, 900) };
            _groundMat.SetFloat("_Glossiness", 0.05f);
            _ground.GetComponent<Renderer>().sharedMaterial = _groundMat;

            _water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            _water.transform.localScale = new Vector3(2000, 1, 2000);
            _waterMat = new Material(Shader.Find("Standard"));
            _waterMat.SetFloat("_Glossiness", 0.93f);
            _waterMat.SetFloat("_Metallic", 0.0f);
            _water.GetComponent<Renderer>().sharedMaterial = _waterMat;

            _rocketMat = new Material(Shader.Find("Standard")) { color = new Color(0.82f, 0.83f, 0.84f) };
            _rocketMat.SetFloat("_Glossiness", 0.45f);
            _rocket = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _rocket.layer = 2; // pas dans la carte des hauteurs du sol
            _rocket.GetComponent<Renderer>().sharedMaterial = _rocketMat;

            _bargeMat = new Material(Shader.Find("Standard")) { color = new Color(0.30f, 0.31f, 0.33f) };
            _barge = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _barge.GetComponent<Renderer>().sharedMaterial = _bargeMat;
            _barge.transform.localScale = new Vector3(52, 6, 90);
            _barge.transform.position = new Vector3(0, 0, 0);

            _padMat = new Material(Shader.Find("Standard")) { color = new Color(0.62f, 0.61f, 0.58f) };
            _pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _pad.GetComponent<Renderer>().sharedMaterial = _padMat;
            _pad.transform.localScale = new Vector3(70, 2, 70);
            _pad.transform.position = new Vector3(0, -0.9f, 0);
            // Deux ouvertures synthétiques : largeur 14 m et hauteur 4,4 m, toit à 5 m.
            // Hauteur/distance inspirées du log KSP ; largeur supposée, ce ne sont pas les meshes KSP.
            _mouthPad = new GameObject("GE bouches du banc");
            for (int side = -1; side <= 1; side += 2)
            {
                MouthBlock(new Vector3(0f, 4.7f, side * 14.5f), new Vector3(16f, 0.6f, 5f));
                MouthBlock(new Vector3(-7.5f, 2.2f, side * 14.5f), new Vector3(1f, 4.4f, 5f));
                MouthBlock(new Vector3(7.5f, 2.2f, side * 14.5f), new Vector3(1f, 4.4f, 5f));
            }
            _hill = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _hill.GetComponent<Renderer>().sharedMaterial = _groundMat;
            _tower = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _tower.GetComponent<Renderer>().sharedMaterial = _bargeMat;
        }

        public void Setup(Scene s, DemoScenario sc)
        {
            _water.SetActive(s.Water);
            _ground.SetActive(!s.Water);
            _barge.SetActive(s.Barge);
            _pad.SetActive(s.Pad);
            _tower.SetActive(s.Pad);
            _hill.SetActive(s.HillHeight > 0f);
            _mouthPad.SetActive(s.MouthPad);
            if (s.HillHeight > 0f)
            {
                _hill.transform.localScale = new Vector3(2f * s.HillRadius, 2f * s.HillHeight, 2f * s.HillRadius);
                _hill.transform.position = new Vector3(s.HillPos.x, 0f, s.HillPos.z);
            }
            _groundMat.color = s.Ground;
            _waterMat.color = s.Ground;
            if (s.Pad)
            {
                float span = Mathf.Max(sc.NozzleSpanM * 2.6f, 12f);
                _pad.transform.localScale = s.MouthPad
                    ? new Vector3(16f, 2f + s.PadHeightM, 2f * s.OutletDistM)
                    : s.CompactPad
                    ? new Vector3(16f, 2f + s.PadHeightM, 2f * s.OutletDistM + 10f)
                    : new Vector3(span * 6f, 2f + s.PadHeightM, span * 6f);
                _pad.transform.rotation = Quaternion.Euler(0f, s.CompactPad ? s.TrenchHeadingDeg : 0f, 0f);
                _pad.transform.position = new Vector3(0, s.PadHeightM + 0.1f - 0.5f * (2f + s.PadHeightM), 0);
                _tower.transform.localScale = new Vector3(8, span * 12f, 8);
                float camAz = (s.SunAzimuthDeg + 200f) * Mathf.Deg2Rad + Mathf.PI + 0.5f; // derrière le lanceur vu de la caméra
                _tower.transform.localScale = new Vector3(6, span * 9f, 6);
                _tower.transform.position = new Vector3(Mathf.Sin(camAz) * span * 2.2f, span * 4.5f, Mathf.Cos(camAz) * span * 2.2f);
            }
            float elev = s.SunElevationDeg * Mathf.Deg2Rad, az = s.SunAzimuthDeg * Mathf.Deg2Rad;
            Vector3 toSun = new Vector3(Mathf.Cos(elev) * Mathf.Sin(az), Mathf.Sin(elev), Mathf.Cos(elev) * Mathf.Cos(az)).normalized;
            _sun.transform.rotation = Quaternion.LookRotation(-toSun);
            _sun.intensity = s.SunElevationDeg < 0 ? 0f : 1.25f;
            _sun.color = new Color(1f, 0.96f, 0.9f);
            _sky.SetFloat("_AtmosphereThickness", s.Vacuum ? 0f : 1.0f);
            _sky.SetFloat("_Exposure", s.SunElevationDeg < 0 ? 0.05f : 1.2f);
            _sky.SetColor("_GroundColor", s.Ground * 0.6f);
            RenderSettings.ambientIntensity = s.SunElevationDeg < 0 ? 0.08f : 1f;
            DynamicGI.UpdateEnvironment();
            for (int w = 0; w < 40; w++) { Camera.Render(); DynamicGI.UpdateEnvironment(); } // chauffe : l'éclairage ambiant du ciel est prêt pour la première capture
        }

        private void MouthBlock(Vector3 position, Vector3 size)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.transform.SetParent(_mouthPad.transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = size;
            part.GetComponent<Renderer>().sharedMaterial = _padMat;
        }

        public RenderEnvironment Environment(Scene s, float t, float dt)
        {
            Vector3 toSun = -_sun.transform.forward;
            // Soleil de KSP lu dans GroundBlastFx.log (0,90 / 0,90 / 0,90) : le volume est éclairé comme en jeu.
            Color raw = _sun.color * 0.92f;
            return RenderEnvironment.FromSun(toSun, raw, Vector3.up, s.Vacuum ? 0f : 0.9f, s.Ground, t, dt);
        }

        public void Frame(Scene s, Sim sim, RenderCore core, float t)
        {
            // Le nuage simulé occupe ~0,6 × R_max : la caméra reste dehors pour juger la forme d'ensemble.
            float R = Mathf.Max(Mathf.Max(sim.Front * 1.25f, 0.6f * sim.MaxCloud), 25f) * s.CameraScale;
            float dist = Mathf.Max(R * 1.6f, 40f);
            float az = (s.SunAzimuthDeg + 200f) * Mathf.Deg2Rad; // caméra à contre-jour partiel (liseré lumineux)
            Vector3 target = new Vector3(0, Mathf.Clamp(R * 0.12f, 3f, 60f), 0);
            float camH = s.CameraPitchUp > 0f ? R * 0.32f * s.CameraPitchUp : Mathf.Clamp(R * 0.16f * s.CameraHeightScale, 2.5f, 180f);
            if (!float.IsNaN(s.CameraAzimuthDeg)) { az = s.CameraAzimuthDeg * Mathf.Deg2Rad; dist = s.CameraDistM; camH = s.CameraHeightM; target = new Vector3(0, 8f, 0); }
            Vector3 pos = new Vector3(Mathf.Sin(az) * dist, camH, Mathf.Cos(az) * dist);
            Camera.transform.position = pos;
            Camera.transform.LookAt(target);
            FlameLightParams l = core.Lights[0];
            _flame.enabled = l.Enabled;
            _flame.transform.position = l.Position;
            _flame.color = l.Color;
            _flame.intensity = l.Intensity;
            _flame.range = l.Range;
        }

        public void Rocket(Scene s, Sim sim)
        {
            float radius = s.Demo == DemoKind.S4Stress33 ? 9f : s.Demo == DemoKind.S3PadLaunch ? 3.7f : s.Demo == DemoKind.S2Barge ? 7f : s.Demo == DemoKind.S5Moon ? 4.2f : 3.7f;
            float height = s.Demo == DemoKind.S4Stress33 ? 70f : s.Demo == DemoKind.S3PadLaunch ? 55f : s.Demo == DemoKind.S5Moon ? 5f : 42f;
            Vector3 nozzle = sim.C.NozzleCenterWorld;
            _rocket.transform.localScale = new Vector3(radius, height * 0.5f, radius);
            _rocket.transform.position = nozzle + Vector3.up * (height * 0.5f + 1.5f);
        }

        public double MeasureVolumeCost(CommandBuffer cmd, RenderCore core, int w, int h, int div)
        {
            // Coût GPU estimé : temps moyen de 12 rendus avec le volume moins 12 rendus sans, synchronisés par une lecture.
            // Séries alternées, minimum de chaque : écarte les à-coups du pilote et du système.
            double with = double.MaxValue, without = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                core.Record(cmd, w, h, div);
                with = Math.Min(with, TimeRenders(24));
                cmd.Clear();
                without = Math.Min(without, TimeRenders(24));
            }
            // + coût de la simulation GPU (un pas de compute par image de jeu), mesuré de la même façon :
            // série de pas de simulation seuls, moins une série vide, synchronisées par une lecture GPU.
            double sim = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                RenderTexture.active = _target;
                _pixels.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 24; i++) core.StepSimulationForTiming(1f / 60f);
                _pixels.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                _pixels.Apply(false);
                sw.Stop();
                sim = Math.Min(sim, sw.Elapsed.TotalMilliseconds / 24);
            }
            if (_lowQuality) sim /= 3.0; // qualité Bas : un pas de simulation pour 3 images
            LastSimMs = sim;
            return Math.Max(0, with - without) + sim;
        }

        private bool _lowQuality = System.Environment.GetEnvironmentVariable("GE_QUALITY") == "Low";

        public double LastSimMs;

        private double TimeRenders(int n)
        {
            RenderTexture.active = _target;
            Camera.Render();
            _pixels.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) Camera.Render();
            _pixels.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            _pixels.Apply(false);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / n;
        }

        public void Capture(string path)
        {
            Camera.Render();
            // HDR → affichage : tonemapper ACES (approximation de Narkowicz), comme TUFX dans le jeu.
            var hdr = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = _target;
            hdr.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            hdr.Apply(false);
            RenderTexture.active = null;
            Color[] px = hdr.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                // Par défaut comme la caméra de KSP (sans HDR) : valeurs écrêtées à 1. GE_TONEMAP=aces : ancien affichage.
                px[i] = _aces ? new Color(Aces(c.r), Aces(c.g), Aces(c.b), 1f) : new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
            }
            _pixels.SetPixels(px);
            _pixels.Apply(false);
            UnityEngine.Object.DestroyImmediate(hdr);
            if (path != null) File.WriteAllBytes(path, _pixels.EncodeToPNG());
        }

        private static readonly bool _aces = System.Environment.GetEnvironmentVariable("GE_TONEMAP") == "aces";

        private static float Aces(float x)
        {
            x = Mathf.Max(0f, x) * 0.9f;
            float v = x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f);
            return Mathf.Clamp01(v); // projet et KSP en espace gamma : pas de correction supplémentaire
        }
        private static Texture2D GroundTexture()
        {
            const int size = 512;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, true);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float a = Mathf.PerlinNoise(x * 0.012f, y * 0.012f);
                    float b = Mathf.PerlinNoise(x * 0.07f + 47, y * 0.07f + 13);
                    float c = Mathf.PerlinNoise(x * 0.31f + 5, y * 0.31f + 91);
                    float v = Mathf.Clamp(0.72f + a * 0.28f + b * 0.12f + c * 0.08f, 0.5f, 1.2f);
                    px[x + y * size] = new Color(v, v * 0.99f, v * 0.97f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            return tex;
        }

        public void Dispose()
        {
            Camera.targetTexture = null;
            _target.Release();
            UnityEngine.Object.DestroyImmediate(_target);
            UnityEngine.Object.DestroyImmediate(_pixels);
            foreach (var go in new[] { _camGo, _sunGo, _flameGo, _ground, _rocket, _water, _barge, _tower, _pad, _hill, _mouthPad }) UnityEngine.Object.DestroyImmediate(go);
            foreach (var m in new[] { _groundMat, _rocketMat, _waterMat, _bargeMat, _padMat, _sky }) UnityEngine.Object.DestroyImmediate(m);
            UnityEngine.Object.DestroyImmediate(_groundTex);
        }
    }
}
