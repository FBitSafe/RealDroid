using Godot;
using System;
using System.Collections.Generic;

sealed class RecoverEpisode : IEpisode, IExamVerdict
{
    readonly DreamScoreTerm[] _rewards =
    {
        new("Alive", "Surviving this step."),
        new("Upright", "Reward for staying upright."),
        new("Pose", "Reward for matching the standing pose."),
        new("Return", "Bonus for returning to Stand after recovery."),
    };
    readonly DreamScoreTerm[] _penalties =
    {
        new("Energy", "Power used by the motors."),
        new("Effort", "Motor activation effort."),
        new("Heat", "Penalty for motor heat."),
        new("Support / stance", "Penalty for leaving support or staying wide while calm."),
        new("Pain", "Pain signal."),
        new("Acute pain", "Protective acute-pain signal."),
        new("Impact", "Penalty for hard foot landings."),
        new("Fall", "Per remaining physics tick after failure."),
    };
    readonly RecoverTask T;
    readonly Ragdoll G;
    readonly int _pushTick, _pushTick2, _dir;
    readonly float _imp, _imp2, _jwSum;
    readonly float[] _ref, _jw, _pain;
    bool _pushed, _reported, _failRec, _force;
    bool _gA, _gB;
    int _toggles;
    float _maxKnee, _maxImpact;
    int[] _knees;
    float _endPose;
    readonly int _examIndex, _footA, _footB, _lastTick;
    int _lastPushTick, _winTicks, _prePushTick = -1, _acuteJoint = -1;
    float _prePush, _acuteAt, _maxAcute, _maxSpread, _recoverSec, _acutePeak;
    float[] _acuteCh;

    public float Quality => _endPose;
    public bool Passed => !_failRec;
    public float MaxKnee => _maxKnee;
    public float MaxImpact => _maxImpact;
    public IReadOnlyList<DreamScoreTerm> Rewards => _rewards;
    public IReadOnlyList<DreamScoreTerm> Penalties => _penalties;

    public void MarkFailed() { _penalties[7].Value = T.Fall; RecordFail(); }

    void RecordFail()
    {
        if (_failRec || _examIndex < 0) return;
        _failRec = true;
        T.ExamFailed(_dir);
    }

    public RecoverEpisode(RecoverTask t, Ragdoll g, int seed, int examIndex)
    {
        T = t; G = g;
        var r = new Random(seed);
        int tps = Engine.PhysicsTicksPerSecond;
        if (examIndex >= 0)
        {
            _dir = examIndex % 2;                       // 0 — в спину (+x), 1 — в грудь (−x)
            _pushTick = tps;
            _pushTick2 = -1;
            _imp = (_dir == 0 ? 1f : -1f) * t.LevelOf(_dir) * t.PushMax;
            t.ExamBegun(_dir);
            if (examIndex >= 2) { _prePushTick = (int)(0.4f * tps); _prePush = -0.2f * _imp; }
            _force = true;                                   // экзамен проверяет сам сектор RECOVER
        }
        else
        {
            // оба направления в каждом эпизоде: кандидаты сравниваются честно
            _pushTick = (int)((1.0 + 0.5 * r.NextDouble()) * tps);
            _pushTick2 = _pushTick + (int)(t.SecondPushDelay * tps);
            int first = r.Next(2);
            _dir = first;
            _imp  = (first == 0 ? 1f : -1f) * Mag(r, t) * t.LevelOf(first) * t.PushMax;
            _imp2 = (first == 0 ? -1f : 1f) * Mag(r, t) * t.LevelOf(1 - first) * t.PushMax;
            _force = r.NextDouble() < t.ForceShare;
        }

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
        _examIndex = examIndex;
        _lastTick = (int)(t.EpisodeSeconds * tps) - 1;
        _winTicks = (int)(t.CheckWindow * tps);
        _lastPushTick = _pushTick;
        var kl = new System.Collections.Generic.List<int>();
        for (int j = 0; j < nj; j++) if (g.Def.Joints[j].Name.StartsWith("knee")) kl.Add(j);
        _knees = kl.ToArray();
        if (g.Brain.Bus.Channels.TryGetValue("pain.acute", out var acuteCh) && acuteCh.Length == nj)
            _acuteCh = acuteCh;
        else
            GD.PushWarning($"Recover exam: per-joint pain.acute channel missing or has invalid length; joint-level acute pain will not be reported.");
        _footA = g.FootFar;
        _footB = g.FootNear;
    }

    public float Step(float dt, int tick, out bool failed)
    {
        var g = G;
        if (tick == _pushTick)
                {
                    _gA = g.Grounded[_footA];
                    _gB = g.Grounded[_footB];
                    PushModel.Impulse(T.LevelOf(_dir), _dir, g);
                    _pushed = true;
                    _lastPushTick = tick;
                    if (_force) ForceRecover();
                }
                if (tick == _pushTick2)
                {
                    PushModel.Impulse(MathF.Abs(_imp2) / T.PushMax, _imp2 >= 0f ? 0 : 1, g);
                    _lastPushTick = tick;
                    if (_force) ForceRecover();
                }
        if (tick == _prePushTick)
            g.Bodies[g.Chest].ApplyCentralImpulse(new Vector2(_prePush, 0f));

        float hy = -g.Bodies[g.Head].GlobalPosition.Y;
        float rot = MathF.Abs(g.Tilt(g.Chest));
        failed = hy < 0.4f * g.HeadHeight0 || rot > 1.2f;
        if (failed)
        {
            RecordFail();
            Report(hy < 0.4f * g.HeadHeight0 ? "ПАДЕНИЕ(голова)" : "ПАДЕНИЕ(наклон)", tick);
            _endPose = 0f;
            ClearTerms();
            return 0f;
        }

        float err = 0f;
        for (int j = 0; j < _ref.Length; j++)
        {
            float d = g.Angle[j] - _ref[j];
            err += _jw[j] * d * d;
        }
        float pose = MathF.Exp(-(err / _jwSum) / (T.PoseSigma * T.PoseSigma));

        int state = g.Brain.Slots[(int)LobeKind.Arbiter].Chip is ArbiterRom a ? a.State : Protocol.Stand;
        var bl = g.Brain.Balance;

        float p1 = 0f, p2 = 0f;
        if (_pain != null) { p1 = _pain[1]; p2 = _pain[2]; }
        _maxAcute = MathF.Max(_maxAcute, p2);
        bool ga = g.Grounded[_footA], gb = g.Grounded[_footB];
        float impactPenalty = 0f;
        if (_pushed)
        {
            if (ga != _gA) _toggles++;
            if (gb != _gB) _toggles++;
            if (!_gA && ga)
            {
                float vy = Math.Max(0f, g.Bodies[_footA].LinearVelocity.Y);
                _maxImpact = MathF.Max(_maxImpact, vy);
                float excess = Math.Max(0f, vy - T.ImpactFree) / 100f;
                impactPenalty += T.ImpactCost * excess * excess;
            }
            if (!_gB && gb)
            {
                float vy = Math.Max(0f, g.Bodies[_footB].LinearVelocity.Y);
                _maxImpact = MathF.Max(_maxImpact, vy);
                float excess = Math.Max(0f, vy - T.ImpactFree) / 100f;
                impactPenalty += T.ImpactCost * excess * excess;
            }
            foreach (int k in _knees) _maxKnee = MathF.Max(_maxKnee, g.Angle[k]);
        }
        _gA = ga; _gB = gb;
        if (_acuteCh != null)
        {
            for (int j = 0; j < _acuteCh.Length; j++)
            {
                if (_acuteCh[j] <= 0.05f || _acuteCh[j] <= _acutePeak) continue;
                _acutePeak = _acuteCh[j];
                _acuteJoint = j;
                _acuteAt = tick / (float)Engine.PhysicsTicksPerSecond;
            }
        }

        _rewards[0].Value = T.Alive;
        _rewards[1].Value = T.Upright * (1f - rot / 1.2f);
        _rewards[2].Value = 0f;
        _rewards[3].Value = 0f;
        _penalties[0].Value = T.EnergyCost * g.Power;
        _penalties[1].Value = T.EffortCost * g.Effort;
        _penalties[2].Value = T.HeatCost * g.Heat;
        _penalties[3].Value = 0f;
        _penalties[4].Value = T.PainCost * p1;
        _penalties[5].Value = T.AcuteCost * p2;
        _penalties[6].Value = impactPenalty;

        float rew = T.Alive + T.Upright * (1f - rot / 1.2f)
                  - T.EnergyCost * g.Power - T.EffortCost * g.Effort - T.HeatCost * g.Heat
                  - T.PainCost * p1 - T.AcuteCost * p2 - impactPenalty;

        if (!_pushed)
        {
            _rewards[2].Value = T.PoseWeight * pose;
            rew += T.PoseWeight * pose;
        }
        else
        {
            // опора = промежуток между стопами независимо от контакта:
            // подвинуть маховую стопу к ξ выгодно сразу, долины нет
            float outside = XiOutside(out float spread, out float comVx);
            _maxSpread = MathF.Max(_maxSpread, spread);
            _penalties[3].Value = T.FootMarginCost * outside / 100f;
            rew -= _penalties[3].Value;

            if (state == Protocol.Recover) _recoverSec += dt;

            // поза — не зависит от протокола, плавно включается после последнего толчка
            float since = (tick - _lastPushTick) / (float)Engine.PhysicsTicksPerSecond;
            float pw = Math.Clamp((since - T.PoseDelay) / T.PoseRamp, 0f, 1f);
            _rewards[2].Value = T.PoseWeight * pw * pose;
            rew += _rewards[2].Value;

            // спокойно, ξ внутри опоры, а ноги всё ещё врозь — пора приставлять ногу
            float calm = pw * Math.Clamp(1f - MathF.Abs(comVx) / T.CalmVel, 0f, 1f) * (outside <= 0f ? 1f : 0f);
            float wide = T.StanceCost * calm * MathF.Max(0f, spread - T.SettleSpread) / 100f;
            _penalties[3].Value += wide;
            rew -= wide;

            // итог: в контрольном окне — спокойно стоит в STAND с приставленной ногой
            if (InCheckWindow(tick) && state == Protocol.Stand
                && spread <= T.SettleSpread && outside <= 0f && MathF.Abs(comVx) <= T.SettleV)
            {
                _rewards[3].Value = T.ReturnBonus;
                rew += T.ReturnBonus;
            }
        }

        _endPose = state == Protocol.Stand ? pose : 0f;
        if (tick >= _lastTick)
        {
            bool back = state == Protocol.Stand;
            if (!back) RecordFail();
            Report(back ? "СТОИТ" : "НЕ ВЕРНУЛАСЬ", tick);
        }
        return rew;
    }

    float XiOutside(out float spread, out float comVx)
    {
        var g = G;
        float m = 0f, cx = 0f, cy = 0f, vx = 0f;
        foreach (var b in g.Bodies)
        {
            m += b.Mass;
            cx += b.Mass * b.GlobalPosition.X;
            cy += b.Mass * b.GlobalPosition.Y;
            vx += b.Mass * b.LinearVelocity.X;
        }
        cx /= m; cy /= m; vx /= m;
        comVx = vx;
        var fa = g.Bodies[_footA].GlobalPosition;
        var fb = g.Bodies[_footB].GlobalPosition;
        float h = MathF.Max(MathF.Max(fa.Y, fb.Y) - cy, 20f);   // Y вниз, пол около 0
        float xi = cx + vx / MathF.Sqrt(980f / h);
        float lo = MathF.Min(fa.X, fb.X) - T.FootPad;
        float hi = MathF.Max(fa.X, fb.X) + T.FootPad;
        spread = MathF.Abs(fa.X - fb.X);
        return MathF.Max(0f, MathF.Max(lo - xi, xi - hi));
    }

    bool InCheckWindow(int tick) =>
        (_pushTick2 > 0 && tick >= _pushTick2 - _winTicks && tick < _pushTick2) || tick > _lastTick - _winTicks;

    void ForceRecover()
    {
        if (G.Brain.Slots[(int)LobeKind.Arbiter].Chip is ArbiterRom a) a.Force(Protocol.Recover);
    }

    void Report(string how, int tick)
    {
        if (_reported || _examIndex < 0) return;
        _reported = true;
        DreamLog.Line($"  exam{_examIndex} толчок {_imp / T.PushMax:+0%;-0%;0%} {how} t={tick / (float)Engine.PhysicsTicksPerSecond:0.00}s " +
                      $"recover={_recoverSec:0.00}s шаг={_maxSpread:0}px acute={_maxAcute:0.00}" +
                      (_acuteJoint >= 0 ? $"({G.Def.Joints[_acuteJoint].Name}@{_acuteAt:0.00}s)" : "") +
                      $" поза={_endPose:0.00} дребезг={_toggles} колено={_maxKnee:0.00} удар={_maxImpact:0}");
    }

    static float Mag(Random r, RecoverTask t) => r.NextDouble() < t.FrontierShare
        ? 0.8f + 0.4f * (float)r.NextDouble()
        : 0.3f + 0.5f * (float)r.NextDouble();

    void ClearTerms()
    {
        foreach (var term in _rewards) term.Value = 0f;
        for (int i = 0; i < 7; i++) _penalties[i].Value = 0f;
    }

}
