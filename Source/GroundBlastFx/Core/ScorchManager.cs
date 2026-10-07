using System;
using GroundBlastFx.Config;
using GroundBlastFx.Contracts;
using GroundBlastFx.Model;
using UnityEngine;

namespace GroundBlastFx.Core
{
    /// <summary>
    /// Traces au sol :
    /// - brûlure sombre en atmosphère, halo clair décapé dans le vide ;
    /// - accumulation (dose) tant que le jet reste au même endroit ; nouvelle trace si le foyer se déplace ;
    /// - ancrage lat/lon/alt, ou Transform de pièce sur un pont de barge ;
    /// - nombre limité (ScorchMaxMarks), la plus faible est recyclée ;
    /// - durée de vie : la session de jeu. Stockage statique : les traces survivent aux changements de scène,
    ///   mais celles créées « dans le futur » sont effacées après un revert ou un quickload (temps universel qui recule).
    ///   1.7 : si « persistentMarks » est actif, elles sont aussi gardées dans la sauvegarde (GroundBlastFxScenario).
    /// </summary>
    public sealed class ScorchManager
    {
        private sealed class Mark
        {
            public bool InUse;
            public int Id;
            public string BodyName;
            public double Lat, Lon, Alt;
            public Vector3d NormalBody;
            public bool OnDeck;
            public Transform Anchor;
            public Vector3 AnchorLocal, NormalLocal;
            public float RadiusM;
            public float Dose;
            public float SootDose;               // Σ suie × dose : moyenne pondérée des ergols qui ont chauffé la trace
            public float Strength;
            public SurfaceKind Surface;
            public MediumKind Medium;
            public double CreatedUT;
            public float AgeS;
            public Vector3 World, WorldNormal;
            public bool WorldValid;
        }

        private const int Capacity = 256;
        private static readonly Mark[] Marks = CreateMarks();
        private static int _nextId = 1;

        private static Mark[] CreateMarks()
        {
            var a = new Mark[Capacity];
            for (int i = 0; i < Capacity; i++) a[i] = new Mark();
            return a;
        }

        public int ActiveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Capacity; i++) if (Marks[i].InUse) n++;
                return n;
            }
        }

        /// <summary>Début de scène de vol : purge des traces « du futur » (revert, quickload) et des traces de pont.</summary>
        public void OnFlightStart()
        {
            double now = Planetarium.GetUniversalTime();
            int removed = 0;
            for (int i = 0; i < Capacity; i++)
            {
                Mark m = Marks[i];
                if (!m.InUse) continue;
                if (m.CreatedUT > now + 1.0 || m.OnDeck) { m.InUse = false; removed++; }
            }
            if (removed > 0) GeLog.Info("Traces : " + removed + " trace(s) effacée(s) (retour dans le temps ou pont déchargé)");
        }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++) Marks[i].InUse = false;
        }

        public void Update(ClusterTracker tracker, CelestialBody body, float dt)
        {
            if (body == null) return;
            PhysicsParams p = GeConfig.Physics;
            int max = Mathf.Clamp(p.ScorchMaxMarks, 1, Capacity);
            for (int i = 0; GeSettings.Renderer.EnableScorch && i < tracker.Capacity; i++)
            {
                ClusterState s = tracker[i];
                if (!s.InUse || !s.EnginesActive || s.Body != body) continue;
                if (s.Surface == SurfaceKind.Water || s.Activation <= 0f || dt <= 0f) continue;
                bool vacuum = s.Medium == MediumKind.Vacuum;
                float radius = ScorchModel.Radius(vacuum, s.ImpingementRadiusM, s.FrontM, p);
                Mark m = s.ScorchSlot >= 0 && s.ScorchSlot < Capacity && Marks[s.ScorchSlot].InUse ? Marks[s.ScorchSlot] : null;
                if (m != null && m.WorldValid && Vector3.Distance(m.World, s.WorldImpact) > p.ScorchNewMarkDistanceFactor * Mathf.Max(m.RadiusM, radius))
                    m = null;
                // Rayon tant que le jet appuie réellement (sinon une fusée qui s'éloigne ou qui arrive de haut
                // laisserait une tache démesurée : r_i est grand quand la pression est faible).
                // Vide : le halo ne s'élargit que près du sol (la tache d'impact d'un jet haut est immense et sans effet).
                bool pressing = vacuum ? s.Activation > 0.25f && s.ImpingementRadiusM < 45f : s.ImpingementPressurePa > p.ScorchPressureRefPa;
                if (m == null)
                {
                    int slot = AllocateSlot(max);
                    m = Marks[slot];
                    InitMark(m, s, body, pressing ? radius : 0.3f);
                    s.ScorchSlot = slot;
                }
                // Dans le vide, le décapage dépend du temps d'exposition au jet, pas de la faible pression ambiante.
                // Une dose trop faible laissait la Lune sans trace visible pendant une descente normale.
                float dosePressure = vacuum ? p.ScorchPressureRefPa * 1.5f : s.ImpingementPressurePa;
                float before = m.Dose;
                m.Dose = ScorchModel.AddDose(m.Dose, dosePressure, s.Activation, dt, p);
                m.SootDose += s.Soot * (m.Dose - before);
                m.Strength = ScorchModel.Strength(m.Dose, p);
                // AgeS est transmis au rendu comme temps depuis le dernier échauffement.
                m.AgeS = 0f;
                // Seuil = pression de référence de brûlure : ≈ 5 m de rayon pour 850 kN, ≈ 20 m pour 70 MN (Starship).
                if (radius > m.RadiusM && pressing) m.RadiusM = radius;
            }

            for (int i = 0; i < Capacity; i++)
            {
                Mark m = Marks[i];
                if (!m.InUse) continue;
                m.AgeS += dt;
                m.WorldValid = false;
                if (m.OnDeck)
                {
                    if (m.Anchor == null) { m.InUse = false; continue; }
                    m.World = m.Anchor.TransformPoint(m.AnchorLocal);
                    m.WorldNormal = m.Anchor.TransformDirection(m.NormalLocal).normalized;
                    m.WorldValid = true;
                }
                else if (m.BodyName == body.bodyName)
                {
                    m.World = SurfaceFrame.WorldPosition(body, m.Lat, m.Lon, m.Alt);
                    m.WorldNormal = SurfaceFrame.FromBody(body, m.NormalBody).normalized;
                    m.WorldValid = true;
                }
            }
        }

        private static void InitMark(Mark m, ClusterState s, CelestialBody body, float radius)
        {
            m.InUse = true;
            m.Id = _nextId++;
            m.BodyName = body.bodyName;
            m.RadiusM = radius;
            m.Dose = 0f;
            m.SootDose = 0f;
            m.Strength = 0f;
            m.Surface = s.Surface;
            m.Medium = s.Medium;
            m.CreatedUT = Planetarium.GetUniversalTime();
            m.AgeS = 0f;
            m.OnDeck = s.AnchoredToDeck && s.AnchorTransform != null;
            if (m.OnDeck)
            {
                m.Anchor = s.AnchorTransform;
                m.AnchorLocal = s.AnchorTransform.InverseTransformPoint(s.WorldImpact);
                m.NormalLocal = s.AnchorTransform.InverseTransformDirection(s.WorldNormal);
            }
            else
            {
                m.Anchor = null;
                body.GetLatLonAlt(s.WorldImpact, out m.Lat, out m.Lon, out m.Alt);
                m.NormalBody = SurfaceFrame.ToBody(body, s.WorldNormal);
            }
            m.World = s.WorldImpact;
            m.WorldNormal = s.WorldNormal;
            m.WorldValid = true;
        }

        private static int AllocateSlot(int max)
        {
            int used = 0, weakest = -1;
            float weakestScore = float.MaxValue;
            for (int i = 0; i < Capacity; i++)
            {
                Mark m = Marks[i];
                if (!m.InUse) continue;
                used++;
                float score = m.Strength - m.AgeS * 1e-4f;
                if (score < weakestScore) { weakestScore = score; weakest = i; }
            }
            if (used >= max && weakest >= 0)
            {
                Marks[weakest].InUse = false;
                return weakest;
            }
            for (int i = 0; i < Capacity; i++) if (!Marks[i].InUse) return i;
            return weakest >= 0 ? weakest : 0;
        }

        // ---------------------------------------------------------------------------------------------
        // Persistance dans la sauvegarde (1.7, réglage « persistentMarks ») : les traces au sol restent d'un vol
        // à l'autre et après un redémarrage du jeu. Revert et quickload rechargent l'état sauvegardé : les traces
        // créées pendant le vol annulé disparaissent d'elles-mêmes. Les traces de pont (barges) ne sont pas gardées.
        // ---------------------------------------------------------------------------------------------

        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        public static void SaveTo(ConfigNode node)
        {
            if (node == null) return;
            node.RemoveNodes("MARK");
            node.RemoveValues("markCount");
            if (!GeSettings.PersistentMarks) return;
            float min = GeConfig.Physics.ScorchMinStrength;
            int saved = 0;
            for (int i = 0; i < Capacity; i++)
            {
                Mark m = Marks[i];
                if (!m.InUse || m.OnDeck || string.IsNullOrEmpty(m.BodyName) || m.Strength < min) continue;
                ConfigNode n = node.AddNode("MARK");
                n.AddValue("body", m.BodyName);
                n.AddValue("lat", m.Lat.ToString("R", Inv));
                n.AddValue("lon", m.Lon.ToString("R", Inv));
                n.AddValue("alt", m.Alt.ToString("R", Inv));
                n.AddValue("normal", m.NormalBody.x.ToString("R", Inv) + "," + m.NormalBody.y.ToString("R", Inv) + "," + m.NormalBody.z.ToString("R", Inv));
                n.AddValue("radius", CfgParse.Format(m.RadiusM));
                n.AddValue("dose", CfgParse.Format(m.Dose));
                n.AddValue("sootDose", CfgParse.Format(m.SootDose));
                n.AddValue("strength", CfgParse.Format(m.Strength));
                n.AddValue("surface", (int)m.Surface);
                n.AddValue("medium", (int)m.Medium);
                n.AddValue("ut", m.CreatedUT.ToString("R", Inv));
                saved++;
            }
            node.AddValue("markCount", saved);
        }

        public static void LoadFrom(ConfigNode node)
        {
            if (node == null) return;
            // Une autre partie ou un quickload remplace l'état, même quand la persistance est désactivée.
            for (int i = 0; i < Capacity; i++) Marks[i].InUse = false;
            if (!GeSettings.PersistentMarks) return;
            int max = Mathf.Clamp(GeConfig.Physics.ScorchMaxMarks, 1, Capacity);
            int loaded = 0;
            foreach (ConfigNode n in node.GetNodes("MARK"))
            {
                string body = n.GetValue("body");
                if (string.IsNullOrEmpty(body)) continue;
                if (!TryD(n, "lat", out double lat) || !TryD(n, "lon", out double lon) || !TryD(n, "alt", out double alt)) continue;
                Vector3d normal = new Vector3d(0, 1, 0);
                string ns = n.GetValue("normal");
                if (!string.IsNullOrEmpty(ns))
                {
                    string[] parts = ns.Split(',');
                    if (parts.Length == 3
                        && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, Inv, out double nx)
                        && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, Inv, out double ny)
                        && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, Inv, out double nz))
                        normal = new Vector3d(nx, ny, nz);
                }
                if (normal.sqrMagnitude < 1e-6) continue;
                Mark m = Marks[AllocateSlot(max)];
                m.InUse = true;
                m.Id = _nextId++;
                m.BodyName = body;
                m.Lat = lat; m.Lon = lon; m.Alt = alt;
                m.NormalBody = normal.normalized;
                m.OnDeck = false;
                m.Anchor = null;
                m.RadiusM = Mathf.Max(CfgParse.Float(n, "radius", 1f), 0.3f);
                m.Dose = CfgParse.Float(n, "dose", 0f);
                m.SootDose = CfgParse.Float(n, "sootDose", 0f);
                m.Strength = Mathf.Clamp01(CfgParse.Float(n, "strength", 0f));
                m.Surface = (SurfaceKind)CfgParse.Int(n, "surface", (int)SurfaceKind.Terrain);
                m.Medium = (MediumKind)CfgParse.Int(n, "medium", (int)MediumKind.Atmosphere);
                m.CreatedUT = TryD(n, "ut", out double ut) ? ut : 0.0;
                m.AgeS = 3600f;          // refroidie : pas de lueur de sol chaud au chargement
                m.WorldValid = false;    // position monde recalculée à la prochaine mise à jour (repère flottant)
                loaded++;
            }
            if (loaded > 0) GeLog.Info("Traces : " + loaded + " trace(s) au sol rechargée(s) depuis la sauvegarde");
        }

        private static bool TryD(ConfigNode n, string key, out double v)
        {
            v = 0.0;
            string s = n.GetValue(key);
            return !string.IsNullOrEmpty(s) && double.TryParse(s, System.Globalization.NumberStyles.Float, Inv, out v);
        }

        public int BuildOutput(ScorchMark[] output, CelestialBody body)
        {
            if (body == null) return 0;
            float min = GeConfig.Physics.ScorchMinStrength;
            int n = 0;
            for (int i = 0; i < Capacity && n < output.Length; i++)
            {
                Mark m = Marks[i];
                if (!m.InUse || !m.WorldValid || m.Strength < min) continue;
                if (!m.OnDeck && m.BodyName != body.bodyName) continue;
                ref ScorchMark o = ref output[n++];
                o.Id = m.Id;
                o.CenterWorld = m.World;
                o.NormalWorld = m.WorldNormal;
                o.RadiusM = m.RadiusM;
                o.Strength01 = m.Strength;
            o.Soot01 = m.Dose > 1e-4f ? Mathf.Clamp01(m.SootDose / m.Dose) : 0.6f;
                o.Surface = m.Surface;
                o.Medium = m.Medium;
                o.AgeS = m.AgeS;
                o.AnchorTransform = m.OnDeck ? m.Anchor : null;
            }
            return n;
        }
    }
}
