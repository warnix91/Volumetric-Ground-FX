using System;

namespace GroundBlastFx.Rendering
{
    /// <summary>
    /// Impulsion visuelle issue d'une hausse rapide de poussée réelle. Etat par foyer, sans Unity ni allocation.
    /// Un saut d'au moins 18 % (et 25 kN) doit se stabiliser 80 ms : une rampe continue et les petites oscillations
    /// ne produisent pas de déflagrations répétées. Ces durées sont un étalonnage visuel, pas un calcul de surpression.
    /// </summary>
    internal struct ThrustPulse
    {
        private bool _ready, _pending;
        private float _lastTime, _lastThrust, _base, _peak, _candidateAge, _quiet;
        private float _amplitude, _pulseAge, _cooldown;
        // Lecture seule pour le journal du Rendering ; aucune modification du détecteur.
        public float StartThrustN => _base;
        public float RiseThrustN => _peak - _base;
        public float Strength01 => _amplitude;
        public float Value { get; private set; }
        public float Rise { get; private set; } // incrément positif, consommé une seule fois par pas de temps

        public float Step(float time, float dt, float thrustN, bool eligible, bool fresh, bool coldIgnition)
        {
            Rise = 0f;
            if (!Finite(time) || !Finite(dt) || !Finite(thrustN)) { this = default; return 0f; }
            thrustN = Math.Max(thrustN, 0f);
            if (!eligible || thrustN <= 0f)
            {
                this = default;
                _ready = true; _lastTime = time; _lastThrust = thrustN;
                return 0f;
            }
            if (fresh || !_ready)
            {
                this = default;
                _ready = true; _lastTime = time; _lastThrust = thrustN;
                // Un foyer ancien qui change de slot/caméra n'est pas un nouvel allumage.
                if (coldIgnition && dt > 0f && dt <= 0.2f && thrustN >= 25000f) Begin(0f, thrustN);
                return 0f;
            }
            // Deux Prepare à la même date (placement de caméra, pause) ne créent ni vieillissement ni nouvel événement.
            if (dt <= 0f || time == _lastTime) return Value;
            float elapsed = time - _lastTime;
            if (elapsed <= 0f || elapsed > 0.2f || dt > 0.2f)
            {
                this = default;
                _ready = true; _lastTime = time; _lastThrust = thrustN;
                return 0f; // saut de temps / reprise / accélération temporelle : recaler l'historique
            }
            _lastTime = time;
            _cooldown = Math.Max(0f, _cooldown - elapsed);
            _pulseAge += elapsed;
            if (_pending)
            {
                _candidateAge += elapsed;
                // La hausse doit s'arrêter rapidement. Une montée lisse continue annule la candidature.
                if (Math.Abs(thrustN - _peak) > 0.025f * Math.Max(_peak, 25000f)) _quiet = 0f;
                else _quiet += elapsed;
                _peak = Math.Max(_peak, thrustN);
                if (_candidateAge > 0.32f || thrustN < 0.85f * _peak) _pending = false;
                else if (_quiet >= 0.08f)
                {
                    float rise = _peak - _base;
                    if (rise >= Math.Max(25000f, 0.18f * _peak))
                    {
                        float relative = Smooth(0.18f, 0.75f, rise / Math.Max(_peak, 1f));
                        float size = Math.Min(1f, (float)Math.Sqrt(rise / 6000000f));
                        _amplitude = relative * (0.25f + 0.75f * size);
                        _pulseAge = 0f; _cooldown = 0.75f;
                    }
                    _pending = false;
                }
            }
            float jump = thrustN - _lastThrust;
            if (!_pending && _cooldown <= 0f && jump >= Math.Max(25000f, 0.18f * thrustN))
                Begin(_lastThrust, thrustN);
            _lastThrust = thrustN;
            // Départ rapide, puis extinction exacte à 1 s : pas de suralimentation persistante du nuage.
            float previous = Value;
            Value = _amplitude * Smooth(0f, 0.04f, _pulseAge) * (1f - Smooth(0.12f, 1f, _pulseAge));
            Rise = Math.Max(0f, Value - previous);
            return Value;
        }

        private void Begin(float before, float after)
        {
            _pending = true; _base = before; _peak = after; _candidateAge = _quiet = 0f;
        }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static float Smooth(float a, float b, float v)
        {
            float t = Math.Max(0f, Math.Min(1f, (v - a) / (b - a)));
            return t * t * (3f - 2f * t);
        }
    }
}
