using Godot;
using System;
using System.Collections.Generic;

sealed class StandEpisode : IEpisode
{
    readonly DreamScoreTerm[] _rewards =
    {
        new("Alive", "Surviving this step."),
        new("Upright", "Reward for staying upright."),
        new("Pose", "Reward for matching the standing pose."),
    };
    readonly DreamScoreTerm[] _penalties =
    {
        new("Energy", "Power used by the motors."),
        new("Effort", "Motor activation effort."),
        new("Heat", "Penalty for motor heat."),
        new("Sliding", "Horizontal foot movement."),
        new("Discomfort", "Ongoing discomfort signal."),
        new("Pain", "Pain signal."),
        new("Acute pain", "Protective acute-pain signal."),
        new("Fall", "Per remaining physics tick after failure."),
    };
    readonly StandTask T;
    readonly Ragdoll G;
    readonly Random R;
    readonly float _amp, _weight, _jwSum;
    readonly float[] _ref, _jw, _pain;
    float _wind, _poseSum;
    int _n;

    public float Quality => _n > 0 ? _poseSum / _n : 0f;
    public IReadOnlyList<DreamScoreTerm> Rewards => _rewards;
    public IReadOnlyList<DreamScoreTerm> Penalties => _penalties;

    public void MarkFailed() => _penalties[7].Value = T.Fall;

    public StandEpisode(StandTask t, Ragdoll g, int seed, int examIndex)
    {
        T = t; G = g; R = new Random(seed);
        _amp = examIndex switch { 0 => 1f, 1 => 0f, _ => 1.5f * (float)R.NextDouble() };
        _weight = g.TotalMass * Ragdoll.Gravity;

        int nj = g.Def.Joints.Count;
        _ref = new float[nj]; _jw = new float[nj];
        for (int j = 0; j < nj; j++)
        {
            string n = g.Def.Joints[j].Name;
            _ref[j] = MotorRom.Stand(n).Item1;
            _jw[j] = n.StartsWith("shoulder") || n.StartsWith("elbow") ? 0.3f : n == "neck" ? 0.5f : 1f;
            _jwSum += _jw[j];
        }
        g.Brain.Bus.Channels.TryGetValue("pain.total", out _pain);
    }

    public float Step(float dt, int tick, out bool failed)
    {
        var g = G;
        float a = dt / T.WindTau;
        _wind += -a * _wind + MathF.Sqrt(2f * a) * Rng.Gauss(R);
        var chest = g.Bodies[g.Chest];
        chest.ApplyCentralForce(new Vector2(_wind * _amp * T.WindLevel * _weight, 0f));

        float hy = -g.Bodies[g.Head].GlobalPosition.Y;
        float rot = MathF.Abs(g.Tilt(g.Chest));
        failed = hy < 0.4f * g.HeadHeight0 || rot > 1.2f;
        if (failed)
        {
            ClearTerms();
            return 0f;
        }

        float err = 0f;
        for (int j = 0; j < _ref.Length; j++)
        {
            float d = g.Angle[j] - _ref[j];
            err += _jw[j] * d * d;
        }
        err /= _jwSum;
        float pose = MathF.Exp(-err / (T.PoseSigma * T.PoseSigma));
        _poseSum += pose; _n++;

        float slide = 0f;
        if (g.Grounded[g.FootNear]) slide += MathF.Abs(g.Bodies[g.FootNear].LinearVelocity.X);
        if (g.Grounded[g.FootFar])  slide += MathF.Abs(g.Bodies[g.FootFar].LinearVelocity.X);
        slide /= 100f;

        float p0 = 0f, p1 = 0f, p2 = 0f;
        if (_pain != null) { p0 = _pain[0]; p1 = _pain[1]; p2 = _pain[2]; }

        _rewards[0].Value = T.Alive;
        _rewards[1].Value = T.Upright * (1f - rot / 1.2f);
        _rewards[2].Value = T.PoseWeight * pose;
        _penalties[0].Value = T.EnergyCost * g.Power;
        _penalties[1].Value = T.EffortCost * g.Effort;
        _penalties[2].Value = T.HeatCost * g.Heat;
        _penalties[3].Value = T.SlideCost * slide;
        _penalties[4].Value = T.DiscomfortCost * p0;
        _penalties[5].Value = T.PainCost * p1;
        _penalties[6].Value = T.AcuteCost * p2;
        return T.Alive
             + T.Upright * (1f - rot / 1.2f)
             + T.PoseWeight * pose
             - T.EnergyCost * g.Power
             - T.EffortCost * g.Effort
             - T.HeatCost * g.Heat
             - T.SlideCost * slide
             - T.DiscomfortCost * p0
             - T.PainCost * p1
             - T.AcuteCost * p2;
    }

    void ClearTerms()
    {
        foreach (var term in _rewards) term.Value = 0f;
        for (int i = 0; i < 7; i++) _penalties[i].Value = 0f;
    }

}
