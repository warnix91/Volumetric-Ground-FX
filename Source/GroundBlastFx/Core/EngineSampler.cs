using System;
using System.Collections.Generic;
using GroundBlastFx.Config;
using GroundBlastFx.Contracts;
using GroundBlastFx.Model;
using GroundBlastFx.Surface;
using UnityEngine;

namespace GroundBlastFx.Core
{
    /// <summary>Tampon de jets réutilisé d'un sondage à l'autre (ne grossit que si nécessaire).</summary>
    public sealed class JetBuffer
    {
        public JetSample[] Items = new JetSample[64];
        public int Count;

        public void Clear() { Count = 0; }

        public void Add(ref JetSample s)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
            Items[Count++] = s;
        }
    }

    /// <summary>
    /// Détection des moteurs allumés de tous les vaisseaux chargés.
    /// Toutes les classes dérivées de ModuleEngines sont prises en compte (ModuleEnginesFX, ModuleEnginesRF de RealFuels…).
    /// Le jet sort selon +forward du thrustTransform : c'est la convention de KSP (la poussée est appliquée selon −forward,
    /// et ModuleSurfaceFX stock lance son rayon de poussière selon trf.forward — vérifié dans Assembly-CSharp 1.12.5).
    /// Les listes de moteurs sont mises en cache par vaisseau et reconstruites seulement quand le vaisseau change.
    /// </summary>
    public sealed class EngineSampler
    {
        public LaunchPadRegistry Pads;
        private sealed class EngineEntry
        {
            public ModuleEngines Module;
            public Part Part;
            public float ExitRadiusM;
            public PropellantLight Light;
        }

        private sealed class VesselEntry
        {
            public readonly List<EngineEntry> Engines = new List<EngineEntry>();
            public bool Dirty = true;
            public bool Latched;
            public double LatchAltitude;
            public string SiteKey;
            public float TotalThrustN;
        }

        private readonly Dictionary<Vessel, VesselEntry> _vessels = new Dictionary<Vessel, VesselEntry>();
        private readonly List<Vessel> _stale = new List<Vessel>();
        private readonly HashSet<string> _loggedParts = new HashSet<string>(StringComparer.Ordinal);

        public int ActiveEngineCount { get; private set; }

        public void Invalidate(Vessel v)
        {
            if (v != null && _vessels.TryGetValue(v, out VesselEntry e)) e.Dirty = true;
        }

        public void Remove(Vessel v)
        {
            if (v != null) _vessels.Remove(v);
        }

        public void Clear() { _vessels.Clear(); }

        /// <summary>true si ce vaisseau est dans la fenêtre « pas de tir » (tâche CL-3.1).</summary>
        public bool IsPadLatched(Vessel v, out string siteKey)
        {
            siteKey = null;
            if (v != null && _vessels.TryGetValue(v, out VesselEntry e) && e.Latched) { siteKey = e.SiteKey; return true; }
            return false;
        }

        public void Collect(JetBuffer jets)
        {
            PhysicsParams p = GeConfig.Physics;
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            int activeEngines = 0;
            for (int vi = 0; vi < loaded.Count; vi++)
            {
                Vessel v = loaded[vi];
                if (v == null || !v.loaded || v.packed) continue;
                VesselEntry entry = GetEntry(v);
                UpdateLatch(v, entry, p);

                float total = 0f;
                for (int i = 0; i < entry.Engines.Count; i++)
                {
                    ModuleEngines m = entry.Engines[i].Module;
                    if (IsFiring(m)) total += m.finalThrust;
                }
                entry.TotalThrustN = total * 1000f;
                if (total <= 0f) continue;

                float pAmb = (float)(v.staticPressurekPa * 1000.0);
                // Longueur des rayons : hauteur d'activation de la poussée totale du vaisseau (+ marge).
                float probe = Mathf.Max(PlumeModel.ActivationHeight(entry.TotalThrustN, pAmb, p) * 1.2f, 20f);

                for (int i = 0; i < entry.Engines.Count; i++)
                {
                    EngineEntry e = entry.Engines[i];
                    ModuleEngines m = e.Module;
                    if (!IsFiring(m)) continue;
                    List<Transform> tts = m.thrustTransforms;
                    if (tts == null || tts.Count == 0) continue;
                    activeEngines++;
                    float isp = m.realIsp;
                    if (isp <= 1f && m.atmosphereCurve != null) isp = m.atmosphereCurve.Evaluate((float)(v.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres));
                    float ve = PlumeModel.ExhaustVelocity(isp);
                    if (ve <= 1f) continue;
                    List<float> mults = m.thrustTransformMultipliers;
                    int n = tts.Count;
                    bool useMults = mults != null && mults.Count == n;
                    for (int k = 0; k < n; k++)
                    {
                        Transform t = tts[k];
                        if (t == null) continue;
                        float mult = useMults ? mults[k] : 1f / n;
                        float thrust = m.finalThrust * 1000f * mult;
                        if (thrust < 10f) continue;
                        var s = new JetSample
                        {
                            NozzleWorld = t.position,
                            AxisWorld = t.forward,
                            ThrustN = thrust,
                            ExhaustVelocityMs = ve,
                            ExitRadiusM = e.ExitRadiusM,
                            FlameColor = e.Light.Color,
                            FlameIntensity = e.Light.Intensity,
                            SteamBonus = e.Light.SteamBonus,
                            Soot = e.Light.Soot,
                            ProbeLengthM = probe,
                            AmbientPressureAtNozzlePa = pAmb,
                            Vessel = v,
                            Part = e.Part,
                            Key = 0,
                            PadLatched = entry.Latched,
                            SiteKey = entry.SiteKey,
                        };
                        jets.Add(ref s);
                    }
                }
            }
            ActiveEngineCount = activeEngines;
            PruneStale(loaded);
        }

        private static bool IsFiring(ModuleEngines m)
        {
            return m != null && m.isEnabled && m.EngineIgnited && !m.flameout && m.finalThrust > 0.001f;
        }

        private VesselEntry GetEntry(Vessel v)
        {
            if (!_vessels.TryGetValue(v, out VesselEntry entry))
            {
                entry = new VesselEntry();
                _vessels[v] = entry;
            }
            if (entry.Dirty) Rebuild(v, entry);
            return entry;
        }

        private void Rebuild(Vessel v, VesselEntry entry)
        {
            entry.Dirty = false;
            entry.Engines.Clear();
            PhysicsParams p = GeConfig.Physics;
            List<Part> parts = v.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i];
                if (part == null) continue;
                for (int k = 0; k < part.Modules.Count; k++)
                {
                    if (!(part.Modules[k] is ModuleEngines me)) continue;
                    entry.Engines.Add(new EngineEntry
                    {
                        Module = me,
                        Part = part,
                        ExitRadiusM = ExitRadius(part, me, p),
                        Light = ResolvePropellant(me),
                    });
                }
            }
        }

        private float ExitRadius(Part part, ModuleEngines m, PhysicsParams p)
        {
            string name = part.partInfo != null ? part.partInfo.name : part.name;
            int nozzles = m.thrustTransforms != null && m.thrustTransforms.Count > 0 ? m.thrustTransforms.Count : 1;
            if (name != null && GeConfig.EngineExitDiameter.TryGetValue(name, out float d))
                return Mathf.Clamp(0.5f * d, p.NozzleExitRadiusMinM, p.NozzleExitRadiusMaxM);

            float diameter = EstimateDiameter(part, m);
            float r = PlumeModel.EstimateExitRadius(diameter, nozzles, p);
            // Plusieurs tuyères dans une même pièce (grappe Super Heavy, RD-180…) : les bounds de la pièce ne disent
            // rien de la taille d'une tuyère. L'espacement entre tuyères voisines la borne : r_e ≤ 0,48 × écart minimal.
            if (nozzles > 1)
            {
                float spacing = MinNozzleSpacing(m);
                if (spacing > 0.05f) r = Mathf.Clamp(Mathf.Min(r, 0.48f * spacing), p.NozzleExitRadiusMinM, p.NozzleExitRadiusMaxM);
            }
            if (name != null && _loggedParts.Add(name))
                GeLog.Info("Tuyère estimée : " + name + " — diamètre pièce " + diameter.ToString("0.00") + " m, " + nozzles + " tuyère(s), r_e = " + r.ToString("0.00") + " m (surcharge possible dans Configs/Engines.cfg)");
            return r;
        }

        private static float MinNozzleSpacing(ModuleEngines m)
        {
            List<Transform> t = m.thrustTransforms;
            float best = float.MaxValue;
            for (int i = 0; i < t.Count; i++)
            {
                if (t[i] == null) continue;
                for (int j = i + 1; j < t.Count; j++)
                {
                    if (t[j] == null) continue;
                    float d = Vector3.Distance(t[i].position, t[j].position);
                    if (d > 0.01f && d < best) best = d;
                }
            }
            return best == float.MaxValue ? 0f : best;
        }

        /// <summary>Diamètre de la pièce perpendiculairement au jet, depuis les bounds de ses renderers.</summary>
        private static float EstimateDiameter(Part part, ModuleEngines m)
        {
            Transform axis = m.thrustTransforms != null && m.thrustTransforms.Count > 0 && m.thrustTransforms[0] != null ? m.thrustTransforms[0] : part.transform;
            return NozzleGeometry.EstimateDiameter(part.FindModelComponents<Renderer>(), axis);
        }

        /// <summary>Premier motif de Propellants.cfg (dans l'ordre du fichier) qui correspond à un ergol du moteur.</summary>
        private static PropellantLight ResolvePropellant(ModuleEngines m)
        {
            List<PropellantLight> lights = GeConfig.Propellants;
            List<Propellant> props = m.propellants;
            if (props != null)
            {
                for (int i = 0; i < lights.Count; i++)
                {
                    string match = lights[i].Match;
                    for (int k = 0; k < props.Count; k++)
                    {
                        string name = props[k].name;
                        if (name != null && name.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) return lights[i];
                    }
                }
            }
            return GeConfig.DefaultPropellant;
        }

        private void UpdateLatch(Vessel v, VesselEntry e, PhysicsParams p)
        {
            if (v.situation == Vessel.Situations.PRELAUNCH)
            {
                if (!e.Latched) GeLog.Info("Pas de tir : « " + v.vesselName + " » verrouillé sur " + (string.IsNullOrEmpty(v.landedAt) ? "(site inconnu)" : v.landedAt));
                e.Latched = true;
                e.LatchAltitude = v.altitude;
                e.SiteKey = string.IsNullOrEmpty(v.landedAt) ? "LaunchPad" : v.landedAt;
                return;
            }
            if (e.Latched)
            {
                if (v.altitude - e.LatchAltitude > p.PadLatchAltitudeM || (v.Landed && !IsPadSite(v.landedAt)))
                {
                    e.Latched = false;
                    GeLog.Info("Pas de tir : « " + v.vesselName + " » déverrouillé (altitude " + (v.altitude - e.LatchAltitude).ToString("0") + " m au-dessus du site)");
                }
                return;
            }
            // Un vaisseau chargé sur un pad moddé ne repasse pas toujours par PRELAUNCH : posé sur la zone réelle du pas,
            // il est verrouillé. 1.9.2 : posé seulement (et à moins de 40 m au-dessus de la zone) ; un vaisseau qui
            // revenait survoler le pas se reverrouillait sans cesse (journal : « déverrouillé » en boucle).
            Vector3 vesselPoint = v.rootPart != null ? v.rootPart.transform.position : v.transform.position;
            if ((v.Landed || v.Splashed) && Pads != null && Pads.IsAbovePad(vesselPoint, 40f))
            {
                e.Latched = true;
                e.LatchAltitude = v.altitude;
                e.SiteKey = string.IsNullOrEmpty(v.landedAt) ? "LaunchPad" : v.landedAt;
                return;
            }
            // Posé sur un pas de tir connu sans être en PRELAUNCH (retour sur le pas, rover…).
            if (v.Landed && IsPadSite(v.landedAt))
            {
                e.Latched = true;
                e.LatchAltitude = v.altitude;
                e.SiteKey = v.landedAt;
            }
        }

        private static bool IsPadSite(string landedAt)
        {
            if (string.IsNullOrEmpty(landedAt)) return false;
            if (GeConfig.FindLaunchSite(landedAt) != null) return true;
            for (int i = 0; i < GeConfig.PadPatterns.Count; i++)
                if (landedAt.IndexOf(GeConfig.PadPatterns[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void PruneStale(List<Vessel> loaded)
        {
            if (_vessels.Count <= loaded.Count) return;
            _stale.Clear();
            foreach (KeyValuePair<Vessel, VesselEntry> kv in _vessels)
            {
                if (kv.Key == null || !kv.Key.loaded) _stale.Add(kv.Key);
            }
            for (int i = 0; i < _stale.Count; i++) _vessels.Remove(_stale[i]);
            _stale.Clear();
        }

        public float TotalThrustN(Vessel v)
        {
            return v != null && _vessels.TryGetValue(v, out VesselEntry e) ? e.TotalThrustN : 0f;
        }
    }
}
