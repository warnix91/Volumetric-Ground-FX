using System;
using GroundBlastFx.Contracts;
using GroundBlastFx.Core;
using UnityEngine;

namespace GroundBlastFx.Config
{
    /// <summary>
    /// Onglet « Volumetric Ground FX (VGFX) » dans les paramètres de la partie de KSP (Difficulté du jeu). Mêmes réglages que la fenêtre
    /// en vol (densité des effets, portée visible), plus les effets à activer et deux options. Les réglages restent communs à toutes les parties (Settings.cfg) :
    /// l'onglet affiche les valeurs actuelles et les applique quand le joueur valide.
    /// Textes : Localization/GroundBlastFx.cfg (langue du jeu).
    /// </summary>
    public sealed class GeStockParams : GameParameters.CustomParameterNode
    {
        public override string Title => "#GBFX_Params_Title";
        public override string DisplaySection => "Volumetric Ground FX (VGFX)";
        public override string Section => "GroundBlastFx";
        public override int SectionOrder => 1;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomFloatParameterUI("#GBFX_Params_Density", toolTip = "#GBFX_Params_DensityTip",
            minValue = 0.25f, maxValue = 2f, stepCount = 36, asPercentage = true)]
        public float density = 1f;

        [GameParameters.CustomFloatParameterUI("#GBFX_Params_Range", toolTip = "#GBFX_Params_RangeTip",
            minValue = 0.5f, maxValue = 20f, stepCount = 40, displayFormat = "N1")]
        public float rangeKm = 8f;

        [GameParameters.CustomFloatParameterUI("#GBFX_Params_Brightness", toolTip = "#GBFX_Params_BrightnessTip",
            minValue = VisualTuning.MinBrightness, maxValue = VisualTuning.MaxBrightness, stepCount = 21, asPercentage = true)]
        public float effectBrightness = VisualTuning.DefaultBrightness;

        [GameParameters.CustomFloatParameterUI("#GBFX_Params_IgnitionStrength", toolTip = "#GBFX_Params_IgnitionStrengthTip",
            minValue = VisualTuning.MinIgnitionStrength, maxValue = VisualTuning.MaxIgnitionStrength, stepCount = 41, asPercentage = true)]
        public float ignitionStrength = VisualTuning.DefaultIgnitionStrength;

        [GameParameters.CustomParameterUI("#GBFX_Params_Dust", toolTip = "#GBFX_Params_DustTip")]
        public bool dust = true;
        [GameParameters.CustomParameterUI("#GBFX_Params_PadSteam", toolTip = "#GBFX_Params_PadSteamTip")]
        public bool padSteam = true;
        [GameParameters.CustomParameterUI("#GBFX_Params_Water", toolTip = "#GBFX_Params_WaterTip")]
        public bool water = true;
        [GameParameters.CustomParameterUI("#GBFX_Params_Vacuum", toolTip = "#GBFX_Params_VacuumTip")]
        public bool vacuum = true;
        [GameParameters.CustomParameterUI("#GBFX_Params_Scorch", toolTip = "#GBFX_Params_ScorchTip")]
        public bool scorch = true;
        [GameParameters.CustomParameterUI("#GBFX_Params_Marks", toolTip = "#GBFX_Params_MarksTip")]
        public bool keepMarks = true;
        [GameParameters.CustomParameterUI("#GBFX_Params_Light", toolTip = "#GBFX_Params_LightTip")]
        public bool flameLight = true;
        [GameParameters.CustomParameterUI("#GBFX_Params_HideStock", toolTip = "#GBFX_Params_HideStockTip")]
        public bool hideStock = true;

        public override bool Interactible(System.Reflection.MemberInfo member, GameParameters parameters)
        {
            // Garder les traces n'a de sens que si les traces sont affichées.
            return member.Name != "keepMarks" || scorch;
        }

        public void CopyFromSettings()
        {
            effectBrightness = VisualTuning.Brightness(GeSettings.Renderer.EffectBrightness);
            ignitionStrength = VisualTuning.IgnitionStrength(GeSettings.Renderer.IgnitionStrength);
            density = GeSettings.Renderer.GlobalIntensity;
            rangeKm = GeSettings.Renderer.MaxRenderDistanceM / 1000f;
            RendererSettings r = GeSettings.Renderer;
            dust = r.EnableDust; padSteam = r.EnablePadSteam; water = r.EnableWater; vacuum = r.EnableVacuumEjecta;
            scorch = r.EnableScorch; flameLight = r.EnableFlameGroundLight;
            keepMarks = GeSettings.PersistentMarks; hideStock = GeSettings.HideStockSurfaceFx;
        }

        public bool ApplyToSettings()
        {
            float d = Mathf.Clamp(density, 0.25f, 2f);
            float r = Mathf.Clamp(rangeKm * 1000f, 500f, 20000f);
            float brightness = VisualTuning.Brightness(effectBrightness);
            float strength = VisualTuning.IgnitionStrength(ignitionStrength);
            RendererSettings s = GeSettings.Renderer;
            bool same = Math.Abs(brightness - s.EffectBrightness) < 1e-4f && Math.Abs(strength - s.IgnitionStrength) < 1e-4f
                        && Math.Abs(d - s.GlobalIntensity) < 1e-3f && Math.Abs(r - s.MaxRenderDistanceM) < 1f
                        && dust == s.EnableDust && padSteam == s.EnablePadSteam && water == s.EnableWater
                        && vacuum == s.EnableVacuumEjecta && scorch == s.EnableScorch && flameLight == s.EnableFlameGroundLight
                        && keepMarks == GeSettings.PersistentMarks && hideStock == GeSettings.HideStockSurfaceFx;
            if (same) return false;
            s.EffectBrightness = brightness;
            s.IgnitionStrength = strength;
            s.GlobalIntensity = d;
            s.MaxRenderDistanceM = r;
            s.EnableDust = dust; s.EnablePadSteam = padSteam; s.EnableWater = water; s.EnableVacuumEjecta = vacuum;
            s.EnableScorch = scorch; s.EnableFlameGroundLight = flameLight;
            GeSettings.PersistentMarks = keepMarks; GeSettings.HideStockSurfaceFx = hideStock;
            return true;
        }
    }

    /// <summary>Synchronise l'onglet des paramètres de KSP avec les réglages du mod (dans les deux sens).</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class GeStockParamsBridge : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(this);
            GameEvents.OnGameSettingsApplied.Add(OnApplied);
            GameEvents.onGameStateLoad.Add(OnGameLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnScene);
            GeSettings.Changed += OnSettingsChanged;
        }

        private void OnDestroy()
        {
            GameEvents.OnGameSettingsApplied.Remove(OnApplied);
            GameEvents.onGameStateLoad.Remove(OnGameLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnScene);
            GeSettings.Changed -= OnSettingsChanged;
        }

        private static GeStockParams Current()
        {
            try { return HighLogic.CurrentGame?.Parameters?.CustomParams<GeStockParams>(); }
            catch (Exception e) { GeLog.ExceptionOnce("GeStockParams.Current", e); return null; }
        }

        private void OnGameLoaded(ConfigNode _) { Current()?.CopyFromSettings(); }
        private void OnScene(GameScenes _) { Current()?.CopyFromSettings(); }
        private void OnSettingsChanged() { Current()?.CopyFromSettings(); }

        private void OnApplied()
        {
            GeStockParams p = Current();
            if (p == null) return;
            if (p.ApplyToSettings())
            {
                GeLog.Info("Réglages modifiés depuis les paramètres de KSP : " + GeSettings.Dump());
                GeSettings.NotifyChanged();
            }
        }
    }
}
