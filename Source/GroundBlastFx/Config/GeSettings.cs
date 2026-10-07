using System;
using System.IO;
using GroundBlastFx.Contracts;
using GroundBlastFx.Core;
using UnityEngine;
using QualityLevel = GroundBlastFx.Contracts.QualityLevel; // UnityEngine.QualityLevel existe aussi

namespace GroundBlastFx.Config
{
    /// <summary>
    /// Réglages du joueur : GameData/GroundBlastFx/PluginData/Settings.cfg (écrit en jeu).
    /// Au premier lancement, les valeurs viennent de PluginData/DefaultSettings.cfg. Le détail du rendu est choisi
    /// identique sur les machines capables de lancer le rendu volumétrique.
    /// </summary>
    public static class GeSettings
    {
        public static readonly RendererSettings Renderer = new RendererSettings();
        public static bool HideStockSurfaceFx = true;
        public static bool PersistentMarks = true;   // 1.7 : traces au sol gardées dans la sauvegarde
        public static bool ShowOverlay;
        public static bool ShowDemoLabels;
        public static readonly string Language = "auto";   // langue du jeu (réglages de KSP)
        public static float WindowX = 240f;
        public static float WindowY = 120f;

        public static event Action Changed;

        public static void Load()
        {
            ConfigNode node = null;
            string source = "valeurs internes";
            try
            {
                if (File.Exists(GePaths.SettingsFile)) { node = ConfigNode.Load(GePaths.SettingsFile); source = GePaths.SettingsFile; }
                else if (File.Exists(GePaths.DefaultSettingsFile)) { node = ConfigNode.Load(GePaths.DefaultSettingsFile); source = GePaths.DefaultSettingsFile; }
            }
            catch (Exception e)
            {
                GeLog.Warn("Lecture des réglages impossible (" + e.Message + ") : valeurs par défaut.");
                node = null;
            }
            ConfigNode s = node?.GetNode("GROUNDBLASTFX_SETTINGS") ?? node;
            if (s != null) Read(s);

            // L'interface publique n'expose plus de commutateurs d'effets ni de profils liés au GPU.
            // Les réglages anciens qui avaient désactivé un effet ne doivent pas survivre à cette migration.
            // 1.0.0 : les commutateurs d'effets reviennent dans l'onglet GroundBlastFx des paramètres de KSP (GeStockParams) ;
            // ils sont donc lus depuis Settings.cfg au lieu d'être forcés.
            Renderer.Quality = QualityLevel.Ultra;
            Renderer.DebugView = false;
            ShowOverlay = false;
            ShowDemoLabels = false;
            GeLog.Info("Réglages chargés depuis " + source);
        }

        private static void Read(ConfigNode s)
        {
            // Lecture des réglages existants ; les commutateurs obsolètes sont normalisés dans Load.
            Renderer.EnableDust = CfgParse.Bool(s, "enableDust", Renderer.EnableDust);
            Renderer.EnablePadSteam = CfgParse.Bool(s, "enablePadSteam", Renderer.EnablePadSteam);
            Renderer.EnableWater = CfgParse.Bool(s, "enableWater", Renderer.EnableWater);
            Renderer.EnableVacuumEjecta = CfgParse.Bool(s, "enableVacuumEjecta", Renderer.EnableVacuumEjecta);
            Renderer.EnableScorch = CfgParse.Bool(s, "enableScorch", Renderer.EnableScorch);
            Renderer.EnableFlameGroundLight = CfgParse.Bool(s, "enableFlameGroundLight", Renderer.EnableFlameGroundLight);
            Renderer.EffectBrightness = VisualTuning.Brightness(CfgParse.Float(s, "effectBrightness", VisualTuning.DefaultBrightness));
            Renderer.IgnitionStrength = VisualTuning.IgnitionStrength(CfgParse.Float(s, "ignitionStrength", VisualTuning.DefaultIgnitionStrength));
            Renderer.GlobalIntensity = Mathf.Clamp(CfgParse.Float(s, "globalIntensity", Renderer.GlobalIntensity), 0.25f, 2f);
            Renderer.MaxRenderDistanceM = Mathf.Clamp(CfgParse.Float(s, "maxRenderDistanceM", Renderer.MaxRenderDistanceM), 500f, 20000f);
            Renderer.MaxRenderedClusters = Mathf.Clamp(CfgParse.Int(s, "maxRenderedClusters", Renderer.MaxRenderedClusters), 1, 16);
            Renderer.DebugView = CfgParse.Bool(s, "rendererDebugView", Renderer.DebugView);
            HideStockSurfaceFx = CfgParse.Bool(s, "hideStockSurfaceFX", HideStockSurfaceFx);
            PersistentMarks = CfgParse.Bool(s, "persistentMarks", PersistentMarks);
            ShowOverlay = CfgParse.Bool(s, "showOverlay", ShowOverlay);
            ShowDemoLabels = CfgParse.Bool(s, "showDemoLabels", ShowDemoLabels);
            // Langue : toujours celle du jeu (1.9.2) ; une valeur ancienne du fichier est ignorée.
            WindowX = CfgParse.Float(s, "windowX", WindowX);
            WindowY = CfgParse.Float(s, "windowY", WindowY);
        }

        public static void Save()
        {
            try
            {
                var root = new ConfigNode();
                ConfigNode s = root.AddNode("GROUNDBLASTFX_SETTINGS");
                s.AddValue("quality", "Ultra");
                s.AddValue("enableDust", Renderer.EnableDust);
                s.AddValue("enablePadSteam", Renderer.EnablePadSteam);
                s.AddValue("enableWater", Renderer.EnableWater);
                s.AddValue("enableVacuumEjecta", Renderer.EnableVacuumEjecta);
                s.AddValue("enableScorch", Renderer.EnableScorch);
                s.AddValue("enableFlameGroundLight", Renderer.EnableFlameGroundLight);
                s.AddValue("effectBrightness", CfgParse.Format(VisualTuning.Brightness(Renderer.EffectBrightness)));
                s.AddValue("ignitionStrength", CfgParse.Format(VisualTuning.IgnitionStrength(Renderer.IgnitionStrength)));
                s.AddValue("globalIntensity", CfgParse.Format(Renderer.GlobalIntensity));
                s.AddValue("maxRenderDistanceM", CfgParse.Format(Renderer.MaxRenderDistanceM));
                s.AddValue("maxRenderedClusters", Renderer.MaxRenderedClusters);
                s.AddValue("rendererDebugView", Renderer.DebugView);
                s.AddValue("hideStockSurfaceFX", HideStockSurfaceFx);
                s.AddValue("persistentMarks", PersistentMarks);
                s.AddValue("showOverlay", ShowOverlay);
                s.AddValue("showDemoLabels", ShowDemoLabels);
                s.AddValue("language", Language);
                s.AddValue("windowX", CfgParse.Format(WindowX));
                s.AddValue("windowY", CfgParse.Format(WindowY));
                Directory.CreateDirectory(GePaths.PluginData);
                root.Save(GePaths.SettingsFile, "GroundBlastFx : réglages du joueur (écrits par la fenêtre en jeu)");
            }
            catch (Exception e)
            {
                GeLog.Warn("Enregistrement des réglages impossible : " + e.Message);
            }
        }

        /// <summary>À appeler après toute modification depuis l'UI : applique au renderer et enregistre.</summary>
        public static void NotifyChanged()
        {
            Save();
            try { Changed?.Invoke(); }
            catch (Exception e) { GeLog.ExceptionOnce("GeSettings.Changed", e); }
        }

        /// <summary>Réinitialise seulement les deux nouveaux réglages, en gardant les préférences existantes.</summary>
        public static void ResetVisualTuning()
        {
            Renderer.EffectBrightness = VisualTuning.DefaultBrightness;
            Renderer.IgnitionStrength = VisualTuning.DefaultIgnitionStrength;
        }

        public static string Dump()
        {
            return "qualité=" + Renderer.Quality + ", poussière=" + Renderer.EnableDust + ", vapeur=" + Renderer.EnablePadSteam
                   + ", eau=" + Renderer.EnableWater + ", vide=" + Renderer.EnableVacuumEjecta + ", traces=" + Renderer.EnableScorch
                   + ", lumière=" + Renderer.EnableFlameGroundLight + ", intensité=" + CfgParse.Format(Renderer.GlobalIntensity)
                   + ", luminosité=" + CfgParse.Format(Renderer.EffectBrightness) + ", souffle=" + CfgParse.Format(Renderer.IgnitionStrength)
                   + ", distance max=" + CfgParse.Format(Renderer.MaxRenderDistanceM) + " m, foyers max=" + Renderer.MaxRenderedClusters
                   + ", masquer poussière stock=" + HideStockSurfaceFx + ", traces gardées=" + PersistentMarks + ", overlay=" + ShowOverlay;
        }
    }
}
