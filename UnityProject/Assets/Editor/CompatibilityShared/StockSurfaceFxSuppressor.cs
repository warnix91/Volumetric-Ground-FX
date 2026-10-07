using System;
using System.Collections.Generic;
using System.Reflection;
using GroundBlastFx.Config;
using GroundBlastFx.Contracts;
using UnityEngine;

namespace GroundBlastFx.Core
{
    /// <summary>
    /// Masque la poussière stock de KSP (ModuleSurfaceFX : sprites de particules) quand le réglage « masquer la poussière
    /// stock » est actif, pour qu'elle ne se superpose pas au rendu volumétrique.
    /// Méthode : fxMax = 0 sur chaque module. Le module stock continue de tourner, voit une échelle nulle et retire
    /// proprement son effet (même chemin que moteur coupé). Valeurs d'origine restaurées si le réglage est désactivé
    /// et à la sortie de la scène de vol. Aucun fichier d'un autre mod n'est modifié.
    /// 1.7 : masque aussi la fumée stock des pas de tir (émetteurs <c>ps</c> de LaunchPadFX, aux bouches du déflecteur) :
    /// ses sprites formaient un « deuxième petit nuage » au-dessus du nuage volumétrique. Seuls leurs renderers sont
    /// coupés (KSP continue de piloter l'émission ; les positions des bouches restent lues par LaunchPadRegistry).
    /// </summary>
    public sealed class StockSurfaceFxSuppressor
    {
        private readonly Dictionary<ModuleSurfaceFX, float> _original = new Dictionary<ModuleSurfaceFX, float>();
        private readonly List<ModuleSurfaceFX> _dead = new List<ModuleSurfaceFX>();
        private readonly Dictionary<Renderer, bool> _padRenderers = new Dictionary<Renderer, bool>();
        private static readonly FieldInfo PadPsField =
            typeof(LaunchPadFX).GetField("ps", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private float _scanTimer;
        private IGroundBlastFxRenderer _replacement;

        private bool ReplacementAvailable => _replacement != null && !(_replacement is NullRenderer) && _replacement.IsAvailable;

        /// <summary>Les effets stock restent disponibles si le renderer de remplacement échoue.</summary>
        public void SetReplacementRenderer(IGroundBlastFxRenderer renderer)
        {
            // NullRenderer réussit ses appels (IsAvailable=true), mais ne dessine aucun remplacement.
            _replacement = renderer;
            if (!ReplacementAvailable) Restore();
            else RequestScan();
        }

        public int SuppressedCount => _original.Count;

        public void Tick(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = 2f;
            Apply();
        }

        public void RequestScan() { _scanTimer = 0f; }

        public void Apply()
        {
            if (!ReplacementAvailable || !GeSettings.HideStockSurfaceFx)
            {
                Restore();
                return;
            }
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            for (int vi = 0; vi < loaded.Count; vi++)
            {
                Vessel v = loaded[vi];
                if (v == null || !v.loaded) continue;
                List<Part> parts = v.parts;
                for (int pi = 0; pi < parts.Count; pi++)
                {
                    Part p = parts[pi];
                    if (p == null) continue;
                    for (int k = 0; k < p.Modules.Count; k++)
                    {
                        if (!(p.Modules[k] is ModuleSurfaceFX fx) || _original.ContainsKey(fx)) continue;
                        _original[fx] = fx.fxMax;
                        fx.fxMax = 0f;
                    }
                }
            }
            HidePadSmoke();
        }

        private void HidePadSmoke()
        {
            try
            {
                LaunchPadFX[] pads = UnityEngine.Object.FindObjectsOfType<LaunchPadFX>();
                for (int i = 0; i < pads.Length; i++)
                {
                    var systems = PadPsField != null ? PadPsField.GetValue(pads[i]) as ParticleSystem[] : null;
                    if (systems == null) continue;
                    for (int j = 0; j < systems.Length; j++)
                    {
                        if (systems[j] == null) continue;
                        ParticleSystemRenderer[] rs = systems[j].GetComponentsInChildren<ParticleSystemRenderer>(true);
                        for (int r = 0; r < rs.Length; r++)
                        {
                            if (rs[r] == null) continue;
                            if (!_padRenderers.ContainsKey(rs[r])) _padRenderers[rs[r]] = rs[r].enabled;
                            rs[r].enabled = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                GeLog.ExceptionOnce("StockSurfaceFx.HidePadSmoke", e);
            }
        }

        public void Restore()
        {
            foreach (KeyValuePair<Renderer, bool> kv in _padRenderers)
                if (kv.Key != null) kv.Key.enabled = kv.Value;
            _padRenderers.Clear();
            if (_original.Count == 0) return;
            _dead.Clear();
            foreach (KeyValuePair<ModuleSurfaceFX, float> kv in _original)
            {
                if (kv.Key != null) kv.Key.fxMax = kv.Value;
                else _dead.Add(kv.Key);
            }
            _original.Clear();
            _dead.Clear();
        }
    }
}
