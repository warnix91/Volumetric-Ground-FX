using System;

namespace GroundBlastFx.Contracts
{
    /// <summary>Bornes communes du rendu et des deux interfaces ; une config non finie revient au défaut.</summary>
    public static class VisualTuning
    {
        public const float DefaultBrightness = 0.85f, MinBrightness = 0.5f, MaxBrightness = 1.5f;
        public const float DefaultIgnitionStrength = 1f, MinIgnitionStrength = 0f, MaxIgnitionStrength = 2f;
        public static float Brightness(float value) => FiniteClamp(value, MinBrightness, MaxBrightness, DefaultBrightness);
        public static float IgnitionStrength(float value) => FiniteClamp(value, MinIgnitionStrength, MaxIgnitionStrength, DefaultIgnitionStrength);
        private static float FiniteClamp(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
