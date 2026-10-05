using Godot;
using System;
using System.Collections.Generic;

public interface IEpisode
{
    float Step(float dt, int tick, out bool failed);
    float Quality { get; }          // 0..1, для зрелости сектора
    IReadOnlyList<DreamScoreTerm> Rewards { get; }
    IReadOnlyList<DreamScoreTerm> Penalties { get; }
    void MarkFailed();
}

public interface IExamVerdict { bool Passed { get; } }

public sealed class DreamScoreTerm
{
    public string Name { get; }
    public string Explanation { get; }
    public float Value { get; set; }

    public DreamScoreTerm(string name, string explanation)
    {
        Name = name;
        Explanation = explanation;
    }
}

/// Задача с автоматической сложностью
public interface ICurriculum
{
    string Status { get; }
    float Bonus(int ok, int total);    // добавка к итогу, чтобы трудный уровень не проигрывал лёгкому
    void Update(int ok, int total);
}

public interface IDreamTask
{
    int Sector { get; }
    float Seconds { get; }
    int ExamCount { get; }
    float FallPenalty { get; }
    IChip MakeArbiter();
    IEpisode Begin(Ragdoll g, int seed, int examIndex);   // examIndex < 0 — популяция
}

public static class DreamTasks
{
    public static IDreamTask Make(int sector) => sector switch
    {
        Protocol.Stand => new StandTask(),
        Protocol.Recover => new RecoverTask(),
        _ => null,
    };
}

/// Арбитр, жёстко держащий один сектор (для сна и отладки)
public sealed class LockArbiter : IChip
{
    public readonly int Sector;
    public LockArbiter(int sector) { Sector = sector; }
    public LobeKind Kind => LobeKind.Arbiter;
    public string Label => $"ARB LOCK {Protocol.Names[Sector]}";
    public void Reset(Brain b) => Set(b);
    public void Tick(Brain b, float dt) => Set(b);
    void Set(Brain b) { for (int k = 0; k < Protocol.Count; k++) b.P[k] = k == Sector ? 1f : 0f; }
}

public static class ArbiterInfo
{
    public static string State(Brain b) => b.Slots[(int)LobeKind.Arbiter].Chip switch
    {
        ArbiterRom a => a.StateName,
        LockArbiter l => "LOCK " + Protocol.Names[l.Sector],
        _ => "заклинило",
    };
}

// ───────── STAND: стоять в позе экономно, только ветер, без толчков ─────────
public sealed class StandTask : IDreamTask
{
    public float EpisodeSeconds = 6f;
    public float WindLevel = 0.03f, WindTau = 0.5f;          // доля веса тела
    public float Alive = 0.2f, Upright = 0.2f, PoseWeight = 1f, PoseSigma = 0.2f;
    public float EnergyCost = 0.5f, EffortCost = 1.0f, HeatCost = 0.3f, SlideCost = 0.3f;
    public float DiscomfortCost = 0.1f, PainCost = 0.5f, AcuteCost = 1.5f;
    public float Fall = 0.5f;

    public int Sector => Protocol.Stand;
    public float Seconds => EpisodeSeconds;
    public int ExamCount => 2;                                // 0 — ветер, 1 — штиль
    public float FallPenalty => Fall;
    public IChip MakeArbiter() => new LockArbiter(Protocol.Stand);
    public IEpisode Begin(Ragdoll g, int seed, int examIndex) => new StandEpisode(this, g, seed, examIndex);
}

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

// ───────── RECOVER: после толчка вернуть ξ в опору и вернуться в STAND ─────────
public sealed class RecoverTask : IDreamTask, ICurriculum
{
    public float EpisodeSeconds = 7.5f, SecondPushDelay = 3f;
    public float PushMax = 3000f;                       // импульс в грудь при уровне 100%
    public float LevelFwd = 0f, LevelBack = 0f, LevelStep = 0.05f;   // Fwd: в спину (+x), Back: в грудь (−x)
    public float MinExamLevel;
    public float ImpactCost = 2f, ImpactFree = 60f;     // удар стопы, px/s
    public float Alive = 3f, Upright = 0.2f, PoseWeight = 0.4f, PoseSigma = 0.25f;
    public float MarginCost = 0.5f, ReturnBonus = 3f;
    public float FootMarginCost = 3f, FootPad = 10f;
    public float FrontierShare = 0.6f;
    public float PoseDelay = 1.5f, PoseRamp = 1f;          // поза после толчка: включается плавно, без привязки к протоколу
    public float CheckWindow = 0.75f;                      // окно итога перед 2-м толчком и в конце эпизода
    public float SettleSpread = 20f, SettleV = 15f;        // px, px/s
    public float ForceShare = 0.8f;                        // доля тренировок с принудительным RECOVER при толчке
    public float StanceCost = 1.5f, CalmVel = 50f;         // штраф за широкую стойку, когда уже спокойно (px/s)
    public float EnergyCost = 0.3f, EffortCost = 0.5f, HeatCost = 0.3f;
    public float PainCost = 0.4f, AcuteCost = 1f;
    public float Fall = 3f;

    public int Sector => Protocol.Recover;
    public float Seconds => EpisodeSeconds;
    public int ExamCount => 4;                          // 0,2 — в спину; 1,3 — в грудь
    public float FallPenalty => Fall;
    public IChip MakeArbiter() => new ArbiterRom();
    public IEpisode Begin(Ragdoll g, int seed, int examIndex) => new RecoverEpisode(this, g, seed, examIndex);

    public float LevelOf(int dir) => dir == 0 ? LevelFwd : LevelBack;
    public string Status => $"спина {LevelFwd:P0} / грудь {LevelBack:P0} (мин. {MinExamLevel:P0})";
    public float Bonus(int ok, int total) => 0.5f * (LevelFwd + LevelBack) * ok / Math.Max(total, 1);

    readonly int[] _examN = new int[2], _examFail = new int[2];
    internal void ExamBegun(int dir) => _examN[dir]++;
    internal void ExamFailed(int dir) => _examFail[dir]++;

    public void Update(int ok, int total)
    {
        for (int d = 0; d < 2; d++)
        {
            if (_examN[d] == 0) continue;
            int pass = _examN[d] - _examFail[d];
            float lv = LevelOf(d);
            if (pass == _examN[d]) lv = Math.Min(1f, lv + LevelStep);
            else if (pass == 0)    lv = Math.Max(MinExamLevel, lv - LevelStep);
            if (d == 0) LevelFwd = lv; else LevelBack = lv;
        }
        Array.Clear(_examN); Array.Clear(_examFail);
    }
}

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
            g.Bodies[g.Chest].ApplyCentralImpulse(new Vector2(_imp, 0f));
            _pushed = true;
            _lastPushTick = tick;
            if (_force) ForceRecover();
        }
        if (tick == _pushTick2)
        {
            g.Bodies[g.Chest].ApplyCentralImpulse(new Vector2(_imp2, 0f));
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
