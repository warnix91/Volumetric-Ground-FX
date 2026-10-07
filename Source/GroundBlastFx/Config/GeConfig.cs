using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GroundBlastFx.Contracts;
using GroundBlastFx.Core;
using GroundBlastFx.Model;
using UnityEngine;

namespace GroundBlastFx.Config
{
    public sealed class SurfaceParams
    {
        public SurfaceKind Kind;
        public float Erodibility = 0.5f;
        public float ThresholdVelocityMs = 60f;
        public float SteamFraction = 0.1f;
        public bool HasColors;
        public Color DustA = new Color(0.55f, 0.52f, 0.48f, 1f);
        public Color DustB = new Color(0.42f, 0.40f, 0.37f, 1f);
    }

    public sealed class BiomeParams
    {
        public string Name;
        public bool HasA, HasB;
        public Color DustA, DustB;
        public float Erodibility = -1f;          // < 0 : valeur de la surface
        public float ThresholdVelocityMs = -1f;  // < 0 : valeur de la surface
        public float SteamFraction = -1f;        // 1.7 : neige, glace (nuage blanc de neige soufflée et de vapeur) ; < 0 : aucune
    }

    public sealed class BodyParams
    {
        public string Name;
        public Color DustA = new Color(0.55f, 0.50f, 0.44f, 1f);
        public Color DustB = new Color(0.40f, 0.36f, 0.31f, 1f);
        public float WindMinMs;
        public float WindMaxMs;
        public float ErodibilityScale = 1f;
        public float ThresholdScale = 1f;
        public readonly Dictionary<string, BiomeParams> Biomes = new Dictionary<string, BiomeParams>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PropellantLight
    {
        public string Match;
        public Color Color = new Color(1f, 0.62f, 0.3f, 1f);
        public float Intensity = 1f;
        public float SteamBonus;
        public float Soot = 0.6f;   // noircissement du sol : kérolox 1 (suie), méthalox ≈ 0,2, hydrolox ≈ 0
    }

    public sealed class LaunchSiteParams
    {
        public string Match;
        public float TrenchHeadingDeg = -1f;     // -1 = radial
        public bool Deluge = true;
    }

    /// <summary>
    /// Configuration chargée depuis GameData/Volumetric Ground FX/Configs/*.cfg via GameDatabase (les patchs ModuleManager
    /// d'autres mods restent donc possibles), plus les réglages de tranchée écrits en jeu (PluginData).
    /// </summary>
    public static class GeConfig
    {
        public static readonly PhysicsParams Physics = new PhysicsParams();
        public static readonly SurfaceParams[] Surfaces = new SurfaceParams[6];
        public static readonly Dictionary<string, BodyParams> Bodies = new Dictionary<string, BodyParams>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<PropellantLight> Propellants = new List<PropellantLight>();
        public static readonly Dictionary<string, float> EngineExitDiameter = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<LaunchSiteParams> LaunchSites = new List<LaunchSiteParams>();
        public static readonly List<string> PadPatterns = new List<string>();
        public static readonly List<string> TerrainPatterns = new List<string>();
        public static readonly BodyParams DefaultBody = new BodyParams { Name = "(défaut)", WindMinMs = 0f, WindMaxMs = 3f };
        public static readonly PropellantLight DefaultPropellant = new PropellantLight { Match = "(défaut)", Color = new Color(1f, 0.7f, 0.4f, 1f), Intensity = 0.8f };

        public static bool Loaded { get; private set; }
        public static int LoadCount { get; private set; }

        public static void Load()
        {
            try
            {
                LoadInternal();
                Loaded = true;
                LoadCount++;
                GeLog.Info("Configuration chargée : " + Summary());
            }
            catch (Exception e)
            {
                GeLog.Error("Échec du chargement de la configuration, valeurs par défaut utilisées : " + e);
                Loaded = true;
            }
        }

        public static SurfaceParams Surface(SurfaceKind kind)
        {
            int i = (int)kind;
            if (i < 0 || i >= Surfaces.Length || Surfaces[i] == null) return Surfaces[(int)SurfaceKind.Terrain] ?? FallbackSurface;
            return Surfaces[i];
        }

        private static readonly SurfaceParams FallbackSurface = new SurfaceParams { Kind = SurfaceKind.Terrain, Erodibility = 0.8f, ThresholdVelocityMs = 40f, SteamFraction = 0.05f };

        public static BodyParams Body(string name)
        {
            if (name != null && Bodies.TryGetValue(name, out BodyParams b)) return b;
            return DefaultBody;
        }

        public static LaunchSiteParams FindLaunchSite(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            for (int i = 0; i < LaunchSites.Count; i++)
            {
                string m = LaunchSites[i].Match;
                if (!string.IsNullOrEmpty(m) && text.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return LaunchSites[i];
            }
            return null;
        }

        public static string Summary()
        {
            return Bodies.Count + " corps, " + Propellants.Count + " ergols, " + EngineExitDiameter.Count + " surcharges de tuyère, "
                   + LaunchSites.Count + " sites de lancement, " + PadPatterns.Count + " motifs de pas de tir";
        }

        private static void LoadInternal()
        {
            ResetDefaults();
            GameDatabase db = GameDatabase.Instance;
            if (db == null) { GeLog.Warn("GameDatabase indisponible : configuration par défaut."); return; }

            foreach (ConfigNode n in db.GetConfigNodes("GROUNDBLASTFX_PHYSICS")) ApplyPhysics(n);

            foreach (ConfigNode n in db.GetConfigNodes("GROUNDBLASTFX_SURFACE"))
            {
                string kindName = n.GetValue("kind");
                if (kindName == null || !TryParseKind(kindName, out SurfaceKind kind)) { GeLog.Warn("GROUNDBLASTFX_SURFACE : kind inconnu « " + kindName + " »"); continue; }
                SurfaceParams s = Surfaces[(int)kind] ?? new SurfaceParams { Kind = kind };
                s.Erodibility = CfgParse.Float(n, "erodibility", s.Erodibility);
                s.ThresholdVelocityMs = CfgParse.Float(n, "thresholdVelocity", s.ThresholdVelocityMs);
                s.SteamFraction = CfgParse.Float(n, "steamFraction", s.SteamFraction);
                if (n.HasValue("dustA")) { s.DustA = CfgParse.ColorValue(n, "dustA", s.DustA); s.HasColors = true; }
                if (n.HasValue("dustB")) { s.DustB = CfgParse.ColorValue(n, "dustB", s.DustB); s.HasColors = true; }
                Surfaces[(int)kind] = s;
            }

            foreach (ConfigNode n in db.GetConfigNodes("GROUNDBLASTFX_BODY"))
            {
                string name = n.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                if (!Bodies.TryGetValue(name, out BodyParams b)) { b = new BodyParams { Name = name }; Bodies[name] = b; }
                b.DustA = CfgParse.ColorValue(n, "dustA", b.DustA);
                b.DustB = CfgParse.ColorValue(n, "dustB", n.HasValue("dustA") && !n.HasValue("dustB") ? b.DustA * 0.75f : b.DustB);
                b.WindMinMs = CfgParse.Float(n, "windMin", b.WindMinMs);
                b.WindMaxMs = CfgParse.Float(n, "windMax", b.WindMaxMs);
                b.ErodibilityScale = CfgParse.Float(n, "erodibilityScale", b.ErodibilityScale);
                b.ThresholdScale = CfgParse.Float(n, "thresholdScale", b.ThresholdScale);
                foreach (ConfigNode bn in n.GetNodes("BIOME"))
                {
                    string bname = bn.GetValue("name");
                    if (string.IsNullOrEmpty(bname)) continue;
                    var bp = new BiomeParams { Name = bname };
                    bp.HasA = CfgParse.TryColor(bn.GetValue("dustA"), out bp.DustA);
                    bp.HasB = CfgParse.TryColor(bn.GetValue("dustB"), out bp.DustB);
                    if (bp.HasA && !bp.HasB) { bp.DustB = bp.DustA * 0.75f; bp.DustB.a = 1f; bp.HasB = true; }
                    bp.Erodibility = CfgParse.Float(bn, "erodibility", -1f);
                    bp.ThresholdVelocityMs = CfgParse.Float(bn, "thresholdVelocity", -1f);
                    bp.SteamFraction = CfgParse.Float(bn, "steamFraction", -1f);
                    b.Biomes[bname] = bp;
                }
            }

            foreach (ConfigNode n in db.GetConfigNodes("GROUNDBLASTFX_PROPELLANT_LIGHT"))
            {
                string match = n.GetValue("match");
                if (string.IsNullOrEmpty(match)) continue;
                Propellants.Add(new PropellantLight
                {
                    Match = match.Trim(),
                    Color = CfgParse.ColorValue(n, "color", DefaultPropellant.Color),
                    Intensity = CfgParse.Float(n, "intensity", 1f),
                    SteamBonus = CfgParse.Float(n, "steamBonus", 0f),
                    Soot = CfgParse.Float(n, "soot", 0.6f),
                });
            }

            foreach (ConfigNode n in db.GetConfigNodes("GROUNDBLASTFX_ENGINE"))
            {
                string part = n.GetValue("part");
                if (string.IsNullOrEmpty(part)) continue;
                float d = CfgParse.Float(n, "nozzleExitDiameter", -1f);
                if (d > 0f) EngineExitDiameter[part.Trim()] = d;
            }

            foreach (ConfigNode n in db.GetConfigNodes("GROUNDBLASTFX_LAUNCHSITE"))
            {
                string match = n.GetValue("match");
                if (string.IsNullOrEmpty(match)) continue;
                LaunchSites.Add(new LaunchSiteParams
                {
                    Match = match.Trim(),
                    TrenchHeadingDeg = CfgParse.Float(n, "trenchHeadingDeg", -1f),
                    Deluge = CfgParse.Bool(n, "deluge", true),
                });
            }

            foreach (ConfigNode n in db.GetConfigNodes("GROUNDBLASTFX_CLASSIFIER"))
            {
                foreach (string v in n.GetValues("padPattern")) if (!string.IsNullOrEmpty(v)) PadPatterns.Add(v.Trim());
                foreach (string v in n.GetValues("terrainPattern")) if (!string.IsNullOrEmpty(v)) TerrainPatterns.Add(v.Trim());
            }

            ApplyUserLaunchSites();
        }

        private static void ResetDefaults()
        {
            for (int i = 0; i < Surfaces.Length; i++) Surfaces[i] = null;
            Surfaces[(int)SurfaceKind.Unknown] = new SurfaceParams { Kind = SurfaceKind.Unknown, Erodibility = 0.5f, ThresholdVelocityMs = 60f, SteamFraction = 0.1f };
            Surfaces[(int)SurfaceKind.Terrain] = new SurfaceParams { Kind = SurfaceKind.Terrain, Erodibility = 0.8f, ThresholdVelocityMs = 40f, SteamFraction = 0.05f };
            Surfaces[(int)SurfaceKind.LaunchPad] = new SurfaceParams { Kind = SurfaceKind.LaunchPad, Erodibility = 0.15f, ThresholdVelocityMs = 120f, SteamFraction = 0.9f };
            Surfaces[(int)SurfaceKind.Structure] = new SurfaceParams { Kind = SurfaceKind.Structure, Erodibility = 0.1f, ThresholdVelocityMs = 150f, SteamFraction = 0.1f };
            Surfaces[(int)SurfaceKind.Water] = new SurfaceParams { Kind = SurfaceKind.Water, Erodibility = 0f, ThresholdVelocityMs = 20f, SteamFraction = 0.8f };
            Surfaces[(int)SurfaceKind.VesselDeck] = new SurfaceParams { Kind = SurfaceKind.VesselDeck, Erodibility = 0.05f, ThresholdVelocityMs = 150f, SteamFraction = 0.4f };
            Bodies.Clear();
            Propellants.Clear();
            EngineExitDiameter.Clear();
            LaunchSites.Clear();
            PadPatterns.Clear();
            TerrainPatterns.Clear();
            CopyPhysics(new PhysicsParams(), Physics);
        }

        /// <summary>Clé de config = nom du champ de PhysicsParams avec la première lettre en minuscule (plumeHalfAngleSeaLevelDeg…).</summary>
        private static void ApplyPhysics(ConfigNode n)
        {
            FieldInfo[] fields = typeof(PhysicsParams).GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (ConfigNode.Value v in n.values)
            {
                FieldInfo f = null;
                for (int i = 0; i < fields.Length; i++)
                {
                    if (string.Equals(fields[i].Name, v.name, StringComparison.OrdinalIgnoreCase)) { f = fields[i]; break; }
                }
                if (f == null) { GeLog.Warn("Physics.cfg : clé inconnue « " + v.name + " » ignorée"); continue; }
                if (f.FieldType == typeof(float) && CfgParse.TryFloat(v.value, out float fv)) f.SetValue(Physics, fv);
                else if (f.FieldType == typeof(int) && int.TryParse(v.value.Trim(), out int iv)) f.SetValue(Physics, iv);
                else GeLog.Warn("Physics.cfg : valeur invalide pour « " + v.name + " » : " + v.value);
            }
        }

        private static void CopyPhysics(PhysicsParams from, PhysicsParams to)
        {
            foreach (FieldInfo f in typeof(PhysicsParams).GetFields(BindingFlags.Public | BindingFlags.Instance)) f.SetValue(to, f.GetValue(from));
        }

        public static string DumpPhysics()
        {
            var sb = new StringBuilder();
            foreach (FieldInfo f in typeof(PhysicsParams).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (sb.Length > 0) sb.Append(", ");
                object val = f.GetValue(Physics);
                sb.Append(char.ToLowerInvariant(f.Name[0])).Append(f.Name.Substring(1)).Append('=');
                sb.Append(val is float fl ? CfgParse.Format(fl) : val.ToString());
            }
            return sb.ToString();
        }

        public static bool TryParseKind(string s, out SurfaceKind kind)
        {
            try
            {
                kind = (SurfaceKind)Enum.Parse(typeof(SurfaceKind), s.Trim(), true);
                return true;
            }
            catch (Exception)
            {
                kind = SurfaceKind.Unknown;
                return false;
            }
        }

        // --- Réglages de tranchée écrits en jeu (PluginData/LaunchSites_user.cfg) ---

        private static readonly Dictionary<string, float> UserTrench = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private static void ApplyUserLaunchSites()
        {
            UserTrench.Clear();
            try
            {
                if (!System.IO.File.Exists(GePaths.UserLaunchSitesFile)) return;
                ConfigNode root = ConfigNode.Load(GePaths.UserLaunchSitesFile);
                if (root == null) return;
                foreach (ConfigNode n in root.GetNodes("GROUNDBLASTFX_LAUNCHSITE_USER"))
                {
                    string site = n.GetValue("site");
                    if (string.IsNullOrEmpty(site)) continue;
                    UserTrench[site.Trim()] = CfgParse.Float(n, "trenchHeadingDeg", -1f);
                }
                GeLog.Info("Réglages de tranchée du joueur : " + UserTrench.Count + " site(s)");
            }
            catch (Exception e)
            {
                GeLog.Warn("Lecture de " + GePaths.UserLaunchSitesFile + " impossible : " + e.Message);
            }
        }

        /// <summary>Cap de tranchée effectif d'un site (réglage du joueur prioritaire). -1 = radial.</summary>
        public static float TrenchHeading(string siteKey, LaunchSiteParams site)
        {
            if (!string.IsNullOrEmpty(siteKey) && UserTrench.TryGetValue(siteKey, out float h)) return h;
            return site != null ? site.TrenchHeadingDeg : -1f;
        }

        public static void SetUserTrench(string siteKey, float headingDeg)
        {
            if (string.IsNullOrEmpty(siteKey)) return;
            UserTrench[siteKey] = headingDeg;
        }

        public static bool SaveUserLaunchSites()
        {
            try
            {
                var root = new ConfigNode();
                foreach (var kv in UserTrench)
                {
                    ConfigNode n = root.AddNode("GROUNDBLASTFX_LAUNCHSITE_USER");
                    n.AddValue("site", kv.Key);
                    n.AddValue("trenchHeadingDeg", CfgParse.Format(kv.Value));
                }
                System.IO.Directory.CreateDirectory(GePaths.PluginData);
                root.Save(GePaths.UserLaunchSitesFile, "GroundBlastFx : cap des tranchées réglé en jeu (-1 = radial)");
                GeLog.Info("Réglage de tranchée enregistré dans " + GePaths.UserLaunchSitesFile);
                return true;
            }
            catch (Exception e)
            {
                GeLog.Error("Enregistrement du réglage de tranchée impossible : " + e.Message);
                return false;
            }
        }
    }
}
