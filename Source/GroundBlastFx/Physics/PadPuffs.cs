using System;

namespace GroundBlastFx.Model
{
    /// <summary>
    /// Vapeur du pas de tir en bouffées (1.0.2). Au lieu d'une grille où la vapeur est déposée puis transportée, ce qui
    /// étale tout en une masse lisse, le déflecteur lâche de grosses bouffées : chacune sort de sa bouche à la vitesse du
    /// jet, freine dans l'air, grossit, glisse sur le sol, monte doucement, dérive au vent puis s'estompe. Le rendu les fond
    /// à chaque image en un seul volume (union de sphères douces : chaque bouffée reste une boule distincte).
    /// Quand le nuage est plein, les deux vieilles bouffées qui se recouvrent le plus fusionnent (volume conservé).
    /// Repère : x = est, y = vertical, z = nord (m), origine = ancrage du nuage. C# pur (testé sans Unity).
    /// </summary>
    public sealed class PadPuffs
    {
        public const int Capacity = 160;

        public struct Puff
        {
            public bool Alive;
            public float X, Y, Z, Vx, Vy, Vz;
            public float Radius, R0, RMax, Density, Fade, Age, Life, Drag;
            public float Tall;   // étirement vertical au-dessus du centre (1 = ronde)
            public float TallK;  // part de cet étirement propre à la bouffée (tours de hauteurs variées)
        }

        /// <summary>Entrées d'un pas, dans le repère du nuage.</summary>
        public struct Inputs
        {
            public int OutletCount;                    // bouches du déflecteur (0 à 2)
            public float O0X, O0Y, O0Z, D0X, D0Z;      // bouche 0 : position (y = hauteur de la bouche), sens de sortie
            public float O1X, O1Y, O1Z, D1X, D1Z;      // bouche 1
            public float OutletFeed;                   // 0..1 : part du jet qui ressort par les bouches
            public float ExitSpeed;                    // m/s à la sortie des bouches
            public float OutletHalfWidth;              // m
            public float JetLength;                    // m : portée du jet (fixe le freinage)
            public float CenterFeed;                   // 0..1 : souffle étalé au pied de la fusée (jet plus canalisé)
            public float CenterX, CenterY, CenterZ;    // point d'impact
            public float CenterSpeed, CenterRadius;    // vitesse du souffle rasant (m/s), rayon de la tache (m)
            public float WindX, WindZ;                 // m/s
            public float CloudRadius;                  // R_max du nuage (m)
            public float Steam;                        // 0..1
            public float Life;                         // s : durée de vie d'une bouffée
            public float Density;                      // 0..1 : épaisseur des bouffées
            public float FadeTau;                      // s : sans apport, le nuage s'efface avec cette constante (0 = jamais)
        }

        public readonly Puff[] Items = new Puff[Capacity];
        private readonly Random _rng;
        private float _acc0, _acc1, _accC;
        private bool _fedThisStep;

        public PadPuffs(int seed) { _rng = new Random(seed); }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++) Items[i].Alive = false;
            _acc0 = _acc1 = _accC = 0f;
        }

        /// <summary>
        /// Opacité d'une bouffée : pleine, puis s'estompe sur la fin de sa vie, et avec tout le nuage dès que le jet ne
        /// l'alimente plus (même rythme que les jets des bouches, validé en 1.0.1 : plus de nuage persistant).
        /// </summary>
        public float Opacity(Puff q)
        {
            if (!q.Alive || q.Life <= 0f) return 0f;
            float t = q.Age / q.Life;
            return q.Density * q.Fade * (1f - SmoothStep(0.55f, 1f, t));
        }

        public void Step(float dt, ref Inputs inp, Func<float, float, float> groundAt)
        {
            if (dt <= 0f) return;
            // « Alimenté » = le jet nourrit les bouches ou le pied de la fusée, pas « une bouffée est née à ce pas »
            // (entre deux naissances, le nuage ne doit pas s'effacer pendant le tir).
            _fedThisStep = (inp.OutletCount > 0 && inp.OutletFeed > 0.05f) || (inp.CenterFeed > 0.15f && inp.CenterSpeed > 8f);
            Emit(dt, ref inp);
            float fade = !_fedThisStep && inp.FadeTau > 0f ? (float)Math.Exp(-dt / inp.FadeTau) : 1f;
            // Gros lanceur (Starship) : la vapeur monte en tours de 100 à 200 m en une dizaine de secondes. Les bouffées
            // s'étirent vers le haut depuis le sol (pas de boule qui se détache). Falcon 9 et plus petit : rondes.
            float bigness = Clamp((inp.CloudRadius / 260f - 1f) * 0.6f, 0f, 1.5f);
            for (int i = 0; i < Capacity; i++)
            {
                ref Puff q = ref Items[i];
                if (!q.Alive) continue;
                q.Age += dt;
                q.Fade *= fade;
                q.Tall = 1f + bigness * q.TallK * Clamp01(q.Age / 10f);
                if (q.Age >= q.Life || q.Fade < 0.02f) { q.Alive = false; continue; }
                // Freinage par l'air vers le vent (portée ≈ vitesse / freinage).
                float keep = (float)Math.Exp(-q.Drag * dt);
                q.Vx = inp.WindX + (q.Vx - inp.WindX) * keep;
                q.Vz = inp.WindZ + (q.Vz - inp.WindZ) * keep;
                q.X += q.Vx * dt; q.Z += q.Vz * dt;
                // Croissance : vite juste après la sortie (les bouffées se rejoignent), puis étalement lent.
                float tg = Math.Min(q.Age / 5f, 1f);
                float grow = 1f - (1f - tg) * (1f - tg);
                float r = q.R0 + (q.RMax - q.R0) * grow + 0.2f * Math.Max(q.Age - 5f, 0f);
                q.Radius = Math.Max(q.Radius, Math.Min(r, 1.6f * q.RMax));
                // Toujours posée sur le sol (le bas de sa sphère dans le terrain) : le nuage s'épaissit vers le haut avec
                // l'âge, la vapeur monte lentement, mais aucune boule ne se détache pour flotter en l'air.
                float g = groundAt(q.X, q.Z);
                float lift = Clamp01(q.Age / 25f) * (0.6f + 0.4f * Clamp01(inp.Steam));
                float target = g + q.Radius * (0.25f + 0.2f * lift);
                q.Vy = (target - q.Y) / 2f;
                q.Y += q.Vy * dt;
                q.Y = Clamp(q.Y, g + 0.2f * q.Radius, g + 0.5f * q.Radius);
            }
        }

        private void Emit(float dt, ref Inputs inp)
        {
            float r0 = Clamp(1.4f * inp.OutletHalfWidth, 3f, 16f);
            // Cadence fixée par la vitesse de sortie (bouffées jointives) ; un jet faible donne des bouffées plus pâles,
            // pas plus rares (sinon un chapelet de petites boules séparées).
            // Taille du lanceur (R_max 260 m ≈ Falcon 9) : un Starship (≈ 800 m) lâche trois fois plus de bouffées, qui
            // vont plus loin (le nuage s'étale le long de la tranchée au lieu de faire un tas au pied du pas).
            float scale = Clamp(inp.CloudRadius / 260f, 1f, 4f);
            float rate = inp.OutletFeed > 0.05f ? Clamp(scale * inp.ExitSpeed / (0.9f * r0), 0f, 16f) : 0f;
            float feedDensity = Clamp01(0.4f + inp.OutletFeed * 1.2f);
            float reach = 0.5f * inp.JetLength * Clamp((float)Math.Sqrt(scale), 1f, 2f);
            float drag = Clamp(Math.Max(inp.ExitSpeed, 5f) / Math.Max(reach, 15f), 0.05f, 6f);   // portée ≈ 0,5 L (× √taille)
            if (inp.OutletCount > 0)
            {
                _acc0 += rate * dt;
                while (_acc0 >= 1f) { _acc0 -= 1f; Spawn(ref inp, inp.O0X, inp.O0Y, inp.O0Z, inp.D0X, inp.D0Z, inp.ExitSpeed, r0, drag, 0.22f, feedDensity); }
            }
            else _acc0 = 0f;
            if (inp.OutletCount > 1)
            {
                _acc1 += rate * dt;
                while (_acc1 >= 1f) { _acc1 -= 1f; Spawn(ref inp, inp.O1X, inp.O1Y, inp.O1Z, inp.D1X, inp.D1Z, inp.ExitSpeed, r0, drag, 0.22f, feedDensity); }
            }
            else _acc1 = 0f;
            // Souffle au pied de la fusée : bouffées lancées dans toutes les directions, freinées vite.
            // Seulement si le souffle étalé est franc : un souffle faible (fusée déjà haute) semait de petites boules
            // isolées autour du pas. Grosses bouffées lentes qui se rejoignent en un bourrelet autour du pied.
            if (inp.CenterFeed > 0.15f && inp.CenterSpeed > 8f)
            {
                _accC += 5f * dt;
                float rc = Clamp(2f * inp.CenterRadius, 8f, 30f);
                float dragC = Clamp(inp.CenterSpeed / (4f * Math.Max(inp.CenterRadius, 6f)), 0.3f, 4f);
                while (_accC >= 1f)
                {
                    _accC -= 1f;
                    double a = _rng.NextDouble() * Math.PI * 2.0;
                    float dx = (float)Math.Cos(a), dz = (float)Math.Sin(a);
                    Spawn(ref inp, inp.CenterX + dx * 0.5f * rc, inp.CenterY, inp.CenterZ + dz * 0.5f * rc, dx, dz,
                          Math.Min(inp.CenterSpeed, 30f) * (0.5f + 0.5f * (float)_rng.NextDouble()), rc, dragC, 0.16f, 1f);
                }
            }
            else _accC = 0f;
        }

        private void Spawn(ref Inputs inp, float x, float y, float z, float dx, float dz, float speed, float r0, float drag, float sizeK, float feed)
        {
            int slot = FreeSlot();
            // Éventail de ±17° : les bouffées s'écartent comme le jet qui sort du déflecteur (pas toutes sur une ligne).
            float side = (float)(_rng.NextDouble() * 2.0 - 1.0) * 0.3f;
            ref Puff q = ref Items[slot];
            q.Alive = true;
            q.X = x + dx * 0.5f * r0; q.Z = z + dz * 0.5f * r0; q.Y = y;
            float s = speed * (0.85f + 0.3f * (float)_rng.NextDouble());
            q.Vx = (dx - dz * side) * s; q.Vz = (dz + dx * side) * s;
            q.Vy = 0f;
            q.R0 = r0; q.Radius = r0;
            // Taille plafonnée à 45 m : un gros lanceur fait un nuage de NOMBREUSES bouffées (relief en chou-fleur), pas
            // quelques sphères géantes qui se fondent en un dôme lisse.
            q.RMax = Math.Min(Math.Max(sizeK * Math.Max(inp.CloudRadius, 20f), 1.6f * r0), 45f) * (0.75f + 0.5f * (float)_rng.NextDouble());
            // Épaisseur un peu variable d'une bouffée à l'autre : ombres internes, sans rendre le nuage translucide.
            q.Density = Clamp01(inp.Density) * feed * (0.75f + 0.25f * (float)_rng.NextDouble());
            q.Fade = 1f;
            q.Tall = 1f;
            q.TallK = 0.45f + 0.9f * (float)_rng.NextDouble();
            q.Age = 0f;
            q.Life = Math.Max(inp.Life, 5f) * (0.85f + 0.3f * (float)_rng.NextDouble());
            q.Drag = drag;
        }

        /// <summary>
        /// Emplacement libre. Nuage plein : on retire la vieille bouffée la plus cachée par une voisine (sa voisine couvre
        /// déjà la même vapeur). Pas de fusion : deux bouffées fondues en une grosse sphère lisse perdraient leurs bosses.
        /// </summary>
        private int FreeSlot()
        {
            for (int i = 0; i < Capacity; i++) if (!Items[i].Alive) return i;
            int drop = -1;
            float best = float.MinValue;
            for (int i = 0; i < Capacity; i++)
            {
                ref Puff a = ref Items[i];
                if (a.Age < 2f) continue;
                for (int j = 0; j < Capacity; j++)
                {
                    if (j == i) continue;
                    ref Puff b = ref Items[j];
                    if (b.Radius < a.Radius) continue;
                    float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    float hidden = (b.Radius - d) / a.Radius;   // 1 = entièrement dans b
                    if (hidden > best) { best = hidden; drop = i; }
                }
            }
            if (drop < 0)
            {
                drop = 0;
                for (int i = 1; i < Capacity; i++) if (Items[i].Age > Items[drop].Age) drop = i;
            }
            Items[drop].Alive = false;
            return drop;
        }

        private static float Clamp(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        private static float Clamp01(float v) => Clamp(v, 0f, 1f);
        private static float SmoothStep(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
