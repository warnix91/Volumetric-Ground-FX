using System;
using GroundBlastFx.Model;

namespace GroundBlastFx.Rendering
{
    /// <summary>
    /// L'impulsion à la bouche accélère aussi la vapeur déjà présente juste en aval. Sans cela, une nouvelle
    /// bouffée rapide reste cachée dans le nuage du préallumage. Aucun changement de taille, de hauteur, de densité
    /// ni de durée de vie : uniquement de la vitesse horizontale, ensuite freinée par le modèle existant.
    /// </summary>
    internal static class PadIgnitionImpulse
    {
        public static void Apply(PadPuffs.Puff[] items, ref PadPuffs.Inputs inp, float impulse, float dt)
        {
            if (impulse <= 0f || !Finite(impulse) || !Finite(dt) || dt <= 0f || !Finite(inp.OutletFeed) || inp.OutletFeed <= 0.05f || inp.OutletCount <= 0) return;
            float reach = Math.Max(inp.JetLength, 15f);
            float width = Math.Max(inp.OutletHalfWidth, 1f);
            if (!Finite(reach) || !Finite(width)) return;
            for (int i = 0; i < PadPuffs.Capacity; i++)
            {
                ref PadPuffs.Puff q = ref items[i];
                // Les nouvelles bouffées reçoivent déjà la vitesse de sortie augmentée à leur naissance.
                if (!q.Alive || q.Age <= 0.16f) continue;
                float w0 = Weight(q.X - inp.O0X, q.Z - inp.O0Z, inp.D0X, inp.D0Z, reach, width);
                float w1 = inp.OutletCount > 1
                    ? Weight(q.X - inp.O1X, q.Z - inp.O1Z, inp.D1X, inp.D1Z, reach, width) : 0f;
                // Deux sorties dont les cônes se croisent ne doublent pas l'impulsion.
                bool first = w0 >= w1;
                float w = first ? w0 : w1;
                if (w <= 0f) continue;
                float dx = first ? inp.D0X : inp.D1X, dz = first ? inp.D0Z : inp.D1Z;
                float inv = 1f / (float)Math.Sqrt(dx * dx + dz * dz);
                q.Vx += dx * inv * impulse * w;
                q.Vz += dz * inv * impulse * w;
                float speed2 = q.Vx * q.Vx + q.Vz * q.Vz;
                if (speed2 > 19600f)
                {
                    float cap = 140f / (float)Math.Sqrt(speed2);
                    q.Vx *= cap; q.Vz *= cap;
                }
            }
        }

        private static float Weight(float x, float z, float dx, float dz, float reach, float width)
        {
            float norm2 = dx * dx + dz * dz;
            if (!Finite(x) || !Finite(z) || !Finite(norm2) || norm2 < 0.0001f) return 0f;
            float inv = 1f / (float)Math.Sqrt(norm2);
            float along = (x * dx + z * dz) * inv;
            // Jamais en amont de la bouche, ni autour du pied du lanceur.
            if (along <= 0f || along >= reach) return 0f;
            float across = Math.Abs((x * dz - z * dx) * inv);
            float cone = width + 0.3f * along;
            return (1f - Smooth(0.2f * reach, reach, along)) * (1f - Smooth(0.6f * cone, cone, across));
        }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static float Smooth(float a, float b, float v)
        {
            float t = Math.Max(0f, Math.Min(1f, (v - a) / (b - a)));
            return t * t * (3f - 2f * t);
        }
    }
}
