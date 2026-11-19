using System;

namespace Isle
{
    /// <summary>Damped spring used for the island morph. Stiffness ~ speed, Damping is the damping ratio (1 = no bounce).</summary>
    sealed class Spring
    {
        public double Value, Velocity, Target;
        public double Stiffness = 400, Damping = 0.85;

        public bool Step(double dt)
        {
            const double sub = 1.0 / 240.0;
            double c = 2 * Damping * Math.Sqrt(Stiffness);
            while (dt > 1e-6)
            {
                double s = Math.Min(sub, dt);
                dt -= s;
                double a = -Stiffness * (Value - Target) - c * Velocity;
                Velocity += a * s;
                Value += Velocity * s;
            }
            if (Math.Abs(Value - Target) < 0.05 && Math.Abs(Velocity) < 0.5)
            {
                Value = Target;
                Velocity = 0;
                return true;
            }
            return false;
        }

        public void Snap(double v)
        {
            Value = Target = v;
            Velocity = 0;
        }
    }
}
