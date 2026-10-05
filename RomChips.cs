using Godot;
using System;
using System.Collections.Generic;

// ───────── ПРОШИВКА ─────────
public sealed class FirmwareRom : IChip
{
    public LobeKind Kind => LobeKind.Firmware;
    public string Label { get; private set; }

    public float CurrentLimit = 1f;
    public float Zeta = 0.7f;
    public float Tone = 0.02f;
    public float DerateStart = 85f, DerateEnd = 115f;
    public float MaxAngVel = 40f, MaxLinVel = 3000f;
    public bool ThermalGuard = true, VelocityGuard = true;
    public float BrakeZone = 0.3f;   // рад до предела, где включается торможение
    public float BrakeGain = 0.5f;   // 0 — без торможения

    public static FirmwareRom Stock() => new() { Label = "FW 1.1" };
    public static FirmwareRom Overclock() => new()
        { Label = "FW OC!", CurrentLimit = 1.4f, Tone = 0.05f, ThermalGuard = false, BrakeGain = 0f };

    public void Reset(Brain b) { }

    public void Tick(Brain b, float dt)
    {
        var body = b.Body;
        if (VelocityGuard) body.ClampVelocities(MaxAngVel, MaxLinVel);
        float boot = b.Slots[(int)LobeKind.Firmware].Boot;

        for (int j = 0; j < body.Angle.Length; j++)
        {
            var jd = body.Def.Joints[j];
            float tr = body.RatedTorque(j);
            float I  = body.JointInertia[j];
            float a  = b.Angle[j], w = b.Vel[j] * 10f;           // энкодер, а не истина
            float kp = tr * Math.Max(b.Stiff[j], Tone);
            float kd = 2f * Zeta * MathF.Sqrt(Math.Max(kp, tr * 0.01f) * I);

            // доводчик: сустав летит в предел — тормозим
            float near = 0f;
            if (w < 0f && a - jd.Lower < BrakeZone) near = 1f - Math.Max(a - jd.Lower, 0f) / BrakeZone;
            else if (w > 0f && jd.Upper - a < BrakeZone) near = 1f - Math.Max(jd.Upper - a, 0f) / BrakeZone;
            kd += near * BrakeGain * 2f * MathF.Sqrt(tr * I);

            float tgt = Math.Clamp(b.Target[j], jd.Lower, jd.Upper);
            float tau = Ragdoll.Spd(tgt - a, w, kp, kd, I, dt);

            float lim = CurrentLimit * boot;
            if (ThermalGuard)
                lim *= Math.Clamp((DerateEnd - body.Temp[j]) / (DerateEnd - DerateStart), 0f, 1f);

            float i = tau / tr;
            body.CurrentCmd[j] = float.IsFinite(i) ? Math.Clamp(i, -lim, lim) : 0f;
        }
    }
}

// ───────── ВЕСТИБУЛЯР ─────────
public sealed class VestibularRom : IChip
{
    public LobeKind Kind => LobeKind.Vestibular;
    public string Label { get; private set; }
    public float Drift;
    readonly float[] _bias = new float[Bal.Size];
    readonly Random _rng = new(3);

    public static VestibularRom Stock() => new() { Label = "VEST ROM" };
    public static VestibularRom Worn()  => new() { Label = "VEST б/у", Drift = 0.15f };

    public void Reset(Brain b) => Array.Clear(_bias);

    public void Tick(Brain b, float dt)
    {
        var body = b.Body;
        var o = b.Balance;
        const float H = Ragdoll.RefHeight;

        float ct = body.Tilt(body.Chest);
        o[Bal.PelvisTilt] = body.Tilt(body.Pelvis);
        o[Bal.PelvisW]    = body.Bodies[body.Pelvis].AngularVelocity * 0.1f;
        o[Bal.ChestTilt]  = ct;
        o[Bal.ChestW]     = body.Bodies[body.Chest].AngularVelocity * 0.1f;

        float z  = Math.Max(-body.Com.Y, 20f);
        float w0 = MathF.Sqrt(Ragdoll.Gravity / z);
        float xi = body.Com.X + body.ComVel.X / w0;
        o[Bal.ComVx] = body.ComVel.X / H;
        o[Bal.ComH]  = z / H;

        float half = 0f;
        if (body.HasSupport)
        {
            float c = 0.5f * (body.SupportMin + body.SupportMax);
            half = 0.5f * (body.SupportMax - body.SupportMin) / H;
            o[Bal.ComX] = (body.Com.X - c) / H;
            o[Bal.Xi]   = (xi - c) / H;
            o[Bal.Support] = 1f;
        }
        else { o[Bal.ComX] = 0f; o[Bal.Xi] = 0f; o[Bal.Support] = 0f; }
        o[Bal.HalfWidth] = half;

        if (Drift > 0f)
        {
            float a = dt / 2f, s = MathF.Sqrt(2f * a);
            for (int i = 0; i <= Bal.Xi; i++)
            {
                _bias[i] += -a * _bias[i] + s * Rng.Gauss(_rng);
                o[i] += Drift * _bias[i];
            }
        }

        o[Bal.Margin]  = body.HasSupport ? half - MathF.Abs(o[Bal.Xi]) : -1f;
        o[Bal.GroundN] = body.Grounded[body.FootNear] ? 1f : 0f;
        o[Bal.GroundF] = body.Grounded[body.FootFar] ? 1f : 0f;
        float headH = -body.Bodies[body.Head].GlobalPosition.Y;
        o[Bal.Lying] = headH < 0.4f * body.HeadHeight0 || MathF.Abs(ct) > 1.2f ? 1f : 0f;
    }
}

// ───────── РЕФЛЕКСЫ ─────────
public sealed class ReflexRom : IChip
{
    public LobeKind Kind => LobeKind.Reflex;
    public string Label { get; private set; }
    public bool DoNotTouchSwingingLeg;
    public float AnkleGain = 4f, AnkleDamp = 0.6f, AnkleMax = 0.8f, HipGain = 0.8f, SpineGain = 0.4f;
    public float Scale = 1f;

    // боль (направленная): отдёргивание внутрь хода
    public float WithdrawGain = 2f, WithdrawStep = 0.3f, WithdrawStiff = 0.6f;
    // острая: оцепенение всего тела
    public float GuardThreshold = 0.05f, GuardFreeze = 0.5f, GuardStiff = 0.5f, FlinchInhibit = 0.3f;
    public float GuardRecoverRelief = 0.7f;                  // насколько ослаблять оцепенение при P[Recover]=1
    public float AnkleEngageTau = 0.12f, AnkleReleaseTau = 0.04f;   // плавное включение / быстрое отпускание, с
    public float SwingKnee = 0.5f, SwingTime = 0.3f;         // рад, с; 0 — выключить
    float[] _ank, _air;
    static readonly Dictionary<int, int> _kneeOf = new();
    // дискомфорт рефлекса не вызывает — он для драйвов и решений

    public static ReflexRom Stock() => new() { Label = "REFLEX ROM 1.2" };
    public static ReflexRom Hyper() => new()
        { Label = "REFLEX x2.5", Scale = 2.5f, WithdrawGain = 5f, GuardStiff = 1f, FlinchInhibit = 0.6f };

    float[] _limit, _side, _total;
    static readonly Dictionary<RagdollDef, int[]> FootByJoint = new();

    public void Reset(Brain b)
    {
        b.Bus.Channels.TryGetValue("pain.limit", out _limit);
        b.Bus.Channels.TryGetValue("pain.side", out _side);
        b.Bus.Channels.TryGetValue("pain.total", out _total);
        if (_ank != null) Array.Clear(_ank);
        if (_air != null) Array.Clear(_air);
    }

    public void Tick(Brain b, float dt)
    {
        var body = b.Body;
        var bl = b.Balance;
        Array.Clear(b.ReflexTarget);
        Array.Clear(b.ReflexStiff);
        b.ReflexInhibit[0] = 0f;

        if (bl[Bal.Lying] < 0.5f)
        {
            int nj = b.ReflexTarget.Length;
            if (_ank == null || _ank.Length != nj) { _ank = new float[nj]; _air = new float[nj]; }
            float off = 0f;
            if (bl[Bal.Support] > 0.5f)
                off = Math.Clamp(Scale * (AnkleGain * bl[Bal.ComX] + AnkleDamp * bl[Bal.ComVx]), -AnkleMax, AnkleMax);
            float pr = Math.Clamp(b.P[Protocol.Recover], 0f, 1f);
            foreach (int a in body.Ankles)
            {
                int ft = FootOf(body, a);
                bool on = body.Grounded[ft] && bl[Bal.Support] > 0.5f
                          && !(DoNotTouchSwingingLeg && b.SwingingJoints[a]);
                float tau = on ? AnkleEngageTau : AnkleReleaseTau;
                float k = Math.Min(1f, dt / Math.Max(tau, 1e-6f));
                _ank[a] += ((on ? 1f : 0f) - _ank[a]) * k;      // _ank — вес включения 0..1
                b.ReflexTarget[a] += _ank[a] * off;             // сигнал баланса без задержки

                // перенос: колено сгибается в первые SwingTime с после отрыва стопы
                _air[a] = body.Grounded[ft] ? 0f : _air[a] + dt;
                int knee = KneeOf(body, a);
                if (SwingKnee > 0f && _air[a] > 0f && _air[a] < SwingTime
                    && knee >= 0 && !b.SwingingJoints[knee])
                    b.ReflexTarget[knee] += SwingKnee * pr * MathF.Sin(MathF.PI * _air[a] / SwingTime);
            }
            foreach (int h in body.Hips)
                if (!DoNotTouchSwingingLeg || !b.SwingingJoints[h])
                    b.ReflexTarget[h] += Scale * HipGain * bl[Bal.PelvisTilt];
            foreach (int s in body.Spine) b.ReflexTarget[s] -= Scale * SpineGain * bl[Bal.ChestTilt];
        }

        if (_limit == null || _side == null) return;
        int n = _limit.Length;
        Span<bool> withdrawn = stackalloc bool[n];
        for (int j = 0; j < n; j++)
        {
            if (DoNotTouchSwingingLeg && b.SwingingJoints[j]) continue;
            float w = Math.Min(1f, _limit[j] * WithdrawGain);
            if (w < 0.01f) continue;
            withdrawn[j] = true;
            float desired = b.Angle[j] - _side[j] * WithdrawStep;
            float delta = desired - b.MotorTarget[j];
            b.ReflexTarget[j] += w * (delta - b.ReflexTarget[j]);
            b.ReflexStiff[j] = Math.Max(b.ReflexStiff[j], w * WithdrawStiff);
        }

        float x = _total != null ? _total[2] : 0f;
        if (x <= GuardThreshold) return;
        float relief = 1f - GuardRecoverRelief * Math.Clamp(b.P[Protocol.Recover], 0f, 1f);
        float f = Math.Min(1f, GuardFreeze * x) * relief;
        for (int j = 0; j < n; j++)
        {
            if (withdrawn[j] || (DoNotTouchSwingingLeg && b.SwingingJoints[j])) continue;
            float hold = b.Angle[j] - b.MotorTarget[j];
            b.ReflexTarget[j] += f * (hold - b.ReflexTarget[j]);
            b.ReflexStiff[j] = Math.Max(b.ReflexStiff[j], GuardStiff * x * relief);
        }
        b.ReflexInhibit[0] = Math.Min(1f, FlinchInhibit * x) * relief;
    }

    static int FootOf(Ragdoll body, int ankleJoint)
    {
        if (!FootByJoint.TryGetValue(body.Def, out var feet))
        {
            feet = new int[body.Def.Joints.Count];
            for (int j = 0; j < feet.Length; j++)
            {
                var joint = body.Def.Joints[j];
                feet[j] = body.Def.PartIndex(joint.Child);
                if (feet[j] < 0)
                    throw new InvalidOperationException($"Joint '{joint.Name}' child '{joint.Child}' has no matching body part.");
                if (joint.Name == "ankle_n" && feet[j] != body.FootNear)
                    throw new InvalidOperationException("ankle_n does not map to FootNear.");
                if (joint.Name == "ankle_f" && feet[j] != body.FootFar)
                    throw new InvalidOperationException("ankle_f does not map to FootFar.");
            }
            FootByJoint.Add(body.Def, feet);
        }
        return feet[ankleJoint];
    }

    static int KneeOf(Ragdoll body, int ankleJoint)
    {
        if (!_kneeOf.TryGetValue(ankleJoint, out int knee))
        {
            string ankleName = body.Def.Joints[ankleJoint].Name;
            const string prefix = "ankle_";
            if (!ankleName.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Joint '{ankleName}' is not an ankle.");
            string kneeName = "knee_" + ankleName.Substring(prefix.Length);
            knee = body.Def.JointIndex(kneeName);
            if (knee < 0)
                throw new InvalidOperationException($"No matching joint '{kneeName}' for '{ankleName}'.");
            _kneeOf.Add(ankleJoint, knee);
        }
        return knee;
    }
}

// ───────── АРБИТР ─────────
public sealed class ArbiterRom : IChip
{
    const float StartupStandTime = 0.5f;

    public LobeKind Kind => LobeKind.Arbiter;
    public string Label => "ARB ROM";
    public float BlendTime = 0.2f, ExitMargin = 0.05f, MinRecover = 0.5f, GetupCalm = 1f;
    public float ExitDwell = 0.3f, ExitSpread = 20f, ExitVel = 0.15f; // Bal.ComVx: normalized by 100 px
    public int State { get; private set; }
    public string StateName => Protocol.Names[State];
    float _timer;
    float _exitDwell;
    double _startupTime;

    public void Reset(Brain b) { State = Protocol.Stand; _timer = 0f; _exitDwell = 0f; _startupTime = 0.0; }

    public void Force(int protocol) => Go(protocol);

    public void Tick(Brain b, float dt)
    {
        var bl = b.Balance;
        bool lying = bl[Bal.Lying] > 0.5f;
        float m = bl[Bal.Margin];
        _timer += dt;

        if (State != Protocol.Stand || _startupTime >= StartupStandTime)
        {
            switch (State)
            {
                case Protocol.Stand:
                    if (lying) Go(Protocol.Getup);
                    else if (m < 0f) Go(Protocol.Recover);
                    break;
                case Protocol.Recover:
                    if (lying) Go(Protocol.Getup);
                    else
                    {
                        var body = b.Body;
                        float spread = MathF.Abs(body.Bodies[body.FootNear].GlobalPosition.X -
                                                body.Bodies[body.FootFar].GlobalPosition.X);
                        bool settled = m > ExitMargin && _timer > MinRecover
                                    && bl[Bal.GroundN] > 0.5f && bl[Bal.GroundF] > 0.5f
                                    && spread <= ExitSpread && MathF.Abs(bl[Bal.ComVx]) <= ExitVel;
                        _exitDwell = settled ? _exitDwell + dt : 0f;
                        if (_exitDwell >= ExitDwell) Go(Protocol.Stand);
                    }
                    break;
                case Protocol.Getup:
                    if (lying) _timer = 0f;
                    else if (_timer > GetupCalm) Go(Protocol.Stand);
                    break;
            }
        }

        _startupTime += dt;
        float a = Math.Min(1f, dt / BlendTime);
        for (int k = 0; k < Protocol.Count; k++)
            b.P[k] += ((k == State ? 1f : 0f) - b.P[k]) * a;
    }

    void Go(int s) { State = s; _timer = 0f; _exitDwell = 0f; }
}

// ───────── МОТОРНАЯ КОРА ─────────
public sealed class MotorRom : IChip
{
    const float SwingDuration = 0.45f;
    const float SwingHeight = 18f;
    const float SwingStiffness = 0.2f;
    readonly ArbiterRom _arbiterDefaults = new();
    public float LandPhase = 0.35f, LandStiff = 0.25f, LandRamp = 0.12f, LandKnee = 0.25f;
    public float LandPress = 4f, LandTimeout = 0.2f, SwingKneeMax = 1.2f;
    public float RearmDelay = 0.08f, SettleHold = 0.3f;
    public int MaxSteps = 3;

    public LobeKind Kind => LobeKind.Motor;
    public string Label => "MOTOR ROM";
    public bool CapturePointStepping = true;
    public float CapturePointAssist = 1f;
    public int CapturePointSteps { get; private set; }
    public int CapturePointTimeouts { get; private set; }
    public float BackStepMax { get; private set; }
    public float FwdStepMax { get; private set; }
    float[][] _pose, _stiff;
    float[] _stepBaseTarget, _stepBaseStiff;
    int _swingFoot = -1;
    int _landingFoot = -1;
    float _swingElapsed;
    float _landingElapsed;
    float _swingStartX, _swingStartY, _swingTargetX;
    float _landingHipTarget, _landingKneeTarget;
    float _rearmTimer, _settleTimer;
    int _lastStepFoot = -1, _stepsInSeries;
    bool _stepArmed = true, _closingStep, _closeStepDone;

    public void Reset(Brain b)
    {
        _swingFoot = -1;
        _landingFoot = -1;
        _swingElapsed = 0f;
        _landingElapsed = 0f;
        _rearmTimer = _settleTimer = 0f;
        _lastStepFoot = -1;
        _stepsInSeries = 0;
        _closingStep = false;
        _closeStepDone = false;
        _stepArmed = true;
        CapturePointSteps = CapturePointTimeouts = 0;
        Array.Clear(b.SwingingJoints);
        var joints = b.Body.Def.Joints;
        int n = joints.Count;
        _pose = new float[Protocol.Count][];
        _stiff = new float[Protocol.Count][];
        for (int k = 0; k < Protocol.Count; k++) { _pose[k] = new float[n]; _stiff[k] = new float[n]; }
        _stepBaseTarget = new float[n];
        _stepBaseStiff = new float[n];

        for (int j = 0; j < n; j++)
        {
            string s = joints[j].Name;
            (_pose[Protocol.Stand][j],   _stiff[Protocol.Stand][j])   = Stand(s);
            (_pose[Protocol.Recover][j], _stiff[Protocol.Recover][j]) = Recover(s);
            (_pose[Protocol.Getup][j],   _stiff[Protocol.Getup][j])   = Curl(s);
        }
        ComputeStepReach(b.Body);
    }

    public static (float, float) Stand(string s) =>
        s.StartsWith("hip")      ? (-0.05f, 1.0f) :
        s.StartsWith("knee")     ? ( 0.10f, 1.0f) :
        s.StartsWith("ankle")    ? (-0.05f, 1.0f) :
        s == "lumbar"            ? ( 0.00f, 0.8f) :
        s == "thoracic"          ? ( 0.00f, 0.8f) :
        s.StartsWith("shoulder") ? (-0.15f, 0.4f) :
        s.StartsWith("elbow")    ? (-0.30f, 0.4f) :
        s == "neck"              ? ( 0.00f, 0.6f) : (0f, 0.5f);

    static (float, float) Recover(string s) =>
        s.StartsWith("shoulder") ? (-0.9f, 0.6f) :
        s.StartsWith("elbow")    ? (-0.5f, 0.5f) : Stand(s);

    static (float, float) Curl(string s) =>
        s.StartsWith("hip")      ? (-0.9f, 0.15f) :
        s.StartsWith("knee")     ? ( 1.3f, 0.15f) :
        s.StartsWith("ankle")    ? ( 0.3f, 0.10f) :
        s == "lumbar"            ? ( 0.4f, 0.15f) :
        s == "thoracic"          ? ( 0.3f, 0.15f) :
        s.StartsWith("shoulder") ? (-1.0f, 0.10f) :
        s.StartsWith("elbow")    ? (-1.2f, 0.10f) :
        s == "neck"              ? ( 0.3f, 0.10f) : (0f, 0.1f);

    public void Tick(Brain b, float dt)
    {
        Array.Clear(b.SwingingJoints);
        for (int j = 0; j < b.MotorTarget.Length; j++)
        {
            float t = 0f, s = 0f;
            for (int k = 0; k < Protocol.Count; k++)
            {
                float p = b.P[k];
                if (p < 1e-4f) continue;
                t += p * _pose[k][j];
                s += p * _stiff[k][j];
            }
            b.MotorTarget[j] = t;
            b.MotorStiff[j] = s;
        }

        float assist = Math.Clamp(CapturePointAssist, 0f, 1f);
        if (!CapturePointStepping || assist <= 0f) return;
        Array.Copy(b.MotorTarget, _stepBaseTarget, _stepBaseTarget.Length);
        Array.Copy(b.MotorStiff, _stepBaseStiff, _stepBaseStiff.Length);
        ApplyCapturePointStep(b, dt);
        if (assist < 1f)
        {
            for (int j = 0; j < b.MotorTarget.Length; j++)
            {
                b.MotorTarget[j] = Mathf.Lerp(_stepBaseTarget[j], b.MotorTarget[j], assist);
                b.MotorStiff[j] = Mathf.Lerp(_stepBaseStiff[j], b.MotorStiff[j], assist);
            }
        }
    }

    void ApplyCapturePointStep(Brain b, float dt)
    {
        var body = b.Body;
        var recover = b.P[Protocol.Recover];
        if (recover < 0.5f || !body.HasSupport)
        {
            _swingFoot = -1;
            _landingFoot = -1;
            _landingElapsed = 0f;
            _rearmTimer = _settleTimer = 0f;
            _stepsInSeries = 0;
            _lastStepFoot = -1;
            _closingStep = false;
            _closeStepDone = false;
            _stepArmed = true;
            return;
        }

        float height = Math.Max(-body.Com.Y, 20f);
        float omega = MathF.Sqrt(Ragdoll.Gravity / height);
        float xi = body.Com.X + body.ComVel.X / omega;
        bool inside = xi >= body.SupportMin && xi <= body.SupportMax;
        if (inside)
        {
            _stepsInSeries = 0;
            _rearmTimer = 0f;
            if (_swingFoot < 0 && _landingFoot < 0) _stepArmed = true;
        }
        else
        {
            _closeStepDone = false;
            if (_rearmTimer > 0f && _swingFoot < 0 && _landingFoot < 0)
            {
                _rearmTimer = Math.Max(0f, _rearmTimer - dt);
                if (_rearmTimer <= 0f && _stepsInSeries < MaxSteps)
                    _stepArmed = true;
            }
        }

        float spread = MathF.Abs(body.Bodies[body.FootNear].GlobalPosition.X -
                                 body.Bodies[body.FootFar].GlobalPosition.X);
        bool settledForClose = inside && spread > _arbiterDefaults.ExitSpread
            && body.Grounded[body.FootNear] && body.Grounded[body.FootFar];
        _settleTimer = settledForClose ? _settleTimer + dt : 0f;

        if (_swingFoot < 0 && _landingFoot < 0 && _stepArmed && !inside
            && _stepsInSeries < MaxSteps &&
            (xi < body.SupportMin || xi > body.SupportMax))
        {
            float direction = MathF.Sign(body.ComVel.X);
            if (direction == 0f) direction = xi > body.SupportMax ? 1f : -1f;
            int first = body.FootNear, second = body.FootFar;
            float firstBehind = direction * (xi - body.Bodies[first].GlobalPosition.X);
            float secondBehind = direction * (xi - body.Bodies[second].GlobalPosition.X);
            _swingFoot = firstBehind >= 0f && (secondBehind < 0f || firstBehind <= secondBehind)
                ? first
                : second;
            if (firstBehind < 0f && secondBehind < 0f &&
                MathF.Abs(firstBehind) > MathF.Abs(secondBehind))
                _swingFoot = second;
            if (_swingFoot == _lastStepFoot)
                _swingFoot = _swingFoot == body.FootNear ? body.FootFar : body.FootNear;

            StartStep(body, _swingFoot, xi + 0.1f * direction * Ragdoll.RefHeight, false);
        }
        else if (_swingFoot < 0 && _landingFoot < 0 && !_closeStepDone && _settleTimer >= SettleHold)
        {
            int rear = MathF.Abs(body.Bodies[body.FootNear].GlobalPosition.X - xi) >
                       MathF.Abs(body.Bodies[body.FootFar].GlobalPosition.X - xi)
                ? body.FootNear : body.FootFar;
            int other = rear == body.FootNear ? body.FootFar : body.FootNear;
            float directionToOther = MathF.Sign(body.Bodies[rear].GlobalPosition.X -
                                                body.Bodies[other].GlobalPosition.X);
            if (directionToOther == 0f) directionToOther = rear == body.FootNear ? -1f : 1f;
            float targetX = body.Bodies[other].GlobalPosition.X + directionToOther * 5f;
            StartStep(body, rear, targetX, true);
            _closeStepDone = true;
            _settleTimer = 0f;
        }

        if (_swingFoot >= 0)
        {
            _swingElapsed += dt;
            float p = Math.Clamp(_swingElapsed / SwingDuration, 0f, 1f);
            float smooth = p * p * (3f - 2f * p);
            float targetY = p >= 1f - LandPhase
                ? _swingStartY + LandPress
                : _swingStartY - SwingHeight * MathF.Sin(MathF.PI * p);
            SetSwingTargets(body, b, _swingFoot, Mathf.Lerp(_swingStartX, _swingTargetX, smooth), targetY, p);
            if (p >= 1f - LandPhase)
                SetLandingTargets(body, b, _swingFoot);

            if (p >= 1f - LandPhase && body.Grounded[_swingFoot])
            {
                BeginLanding(body, b, _swingFoot);
                _swingFoot = -1;
            }
            else if (p >= 1f)
            {
                BeginLanding(body, b, _swingFoot);
                _swingFoot = -1;
            }
        }

        if (_landingFoot >= 0)
            ApplyLandingRamp(body, b, dt);
    }

    void StartStep(Ragdoll body, int foot, float targetX, bool closing)
    {
        _swingFoot = foot;
        _lastStepFoot = foot;
        _swingElapsed = 0f;
        _landingElapsed = 0f;
        _landingFoot = -1;
        _swingStartX = body.Bodies[foot].GlobalPosition.X;
        _swingStartY = body.Bodies[foot].GlobalPosition.Y;
        _swingTargetX = targetX;
        _stepArmed = false;
        _closingStep = closing;
        _stepsInSeries++;
        CapturePointSteps++;
    }

    void CompleteStep(Ragdoll body, Brain brain, int foot, bool timedOut)
    {
        _landingFoot = -1;
        _landingElapsed = 0f;
        ClearSwingFlags(body, brain, foot);
        _closingStep = false;

        float height = Math.Max(-body.Com.Y, 20f);
        float omega = MathF.Sqrt(Ragdoll.Gravity / height);
        float xi = body.Com.X + body.ComVel.X / omega;
        bool inside = body.HasSupport && xi >= body.SupportMin && xi <= body.SupportMax;
        if (inside)
        {
            _stepsInSeries = 0;
            _stepArmed = true;
            _rearmTimer = 0f;
        }
        else if (_stepsInSeries < MaxSteps)
        {
            _stepArmed = false;
            _rearmTimer = RearmDelay;
        }
        else
        {
            _stepArmed = false;
            _rearmTimer = 0f;
        }

        if (timedOut)
            CapturePointTimeouts++;
    }

    static void ClearSwingFlags(Ragdoll body, Brain brain, int foot)
    {
        bool near = foot == body.FootNear;
        string suffix = near ? "n" : "f";
        int hip = body.Def.JointIndex($"hip_{suffix}");
        int knee = body.Def.JointIndex($"knee_{suffix}");
        int ankle = body.Def.JointIndex($"ankle_{suffix}");
        if (hip >= 0) brain.SwingingJoints[hip] = false;
        if (knee >= 0) brain.SwingingJoints[knee] = false;
        if (ankle >= 0) brain.SwingingJoints[ankle] = false;
    }

    void ComputeStepReach(Ragdoll body)
    {
        float thigh = body.Def.Parts[body.Def.PartIndex("thigh_n")].Length;
        float shin = body.Def.Parts[body.Def.PartIndex("shin_n")].Length;
        int hip = body.Def.JointIndex("hip_n");
        var hipDef = body.Def.Joints[hip];
        float lo = Math.Min(hipDef.Lower + 0.05f, hipDef.Upper - 0.05f);
        float hi = Math.Max(hipDef.Lower + 0.05f, hipDef.Upper - 0.05f);
        float back = 0f, forward = 0f;
        const int samples = 256;
        for (int i = 0; i <= samples; i++)
        {
            float hipAngle = Mathf.Lerp(lo, hi, i / (float)samples);
            for (int k = 0; k <= samples; k++)
            {
                float kneeAngle = SwingKneeMax * k / samples;
                float horizontal = -(thigh * MathF.Sin(hipAngle) +
                                     shin * MathF.Sin(hipAngle + kneeAngle));
                forward = Math.Max(forward, horizontal);
                back = Math.Max(back, -horizontal);
            }
        }
        BackStepMax = back;
        FwdStepMax = forward;
    }

    void SetSwingTargets(Ragdoll body, Brain brain, int foot, float targetX, float targetY, float progress)
    {
        bool near = foot == body.FootNear;
        string suffix = near ? "n" : "f";
        int hip = body.Def.JointIndex($"hip_{suffix}");
        int knee = body.Def.JointIndex($"knee_{suffix}");
        int ankle = body.Def.JointIndex($"ankle_{suffix}");
        if (hip < 0 || knee < 0 || ankle < 0) return;

        Vector2 hipPoint = body.JointPos(hip);
        var ankleDef = body.Def.Joints[ankle];
        var footDef = body.Def.Parts[foot];
        Vector2 ankleOffset = ankleDef.Anchor - footDef.Center;
        Vector2 ankleTarget = new(targetX + ankleOffset.X, targetY + ankleOffset.Y);
        Vector2 toTarget = ankleTarget - hipPoint;
        float thighLength = body.Def.Parts[body.Def.PartIndex(near ? "thigh_n" : "thigh_f")].Length;
        float shinLength = body.Def.Parts[body.Def.PartIndex(near ? "shin_n" : "shin_f")].Length;
        float maxReach = 0.95f * (thighLength + shinLength);
        float distance = Math.Clamp(toTarget.Length(), 1f, maxReach);
        if (toTarget.Length() > maxReach)
            toTarget = toTarget.Normalized() * maxReach;
        float kneeAngle = MathF.Acos(Math.Clamp(
            (thighLength * thighLength + shinLength * shinLength - distance * distance) /
            (2f * thighLength * shinLength), -1f, 1f));
        kneeAngle = Math.Clamp(kneeAngle, 0f, SwingKneeMax);
        float targetBearing = -MathF.Atan2(toTarget.X, toTarget.Y);
        float thighRotation = targetBearing - MathF.Atan2(
            shinLength * MathF.Sin(kneeAngle),
            thighLength + shinLength * MathF.Cos(kneeAngle));
        float pelvisRotation = body.Bodies[body.Pelvis].GlobalRotation;
        float thighTarget = Mathf.Wrap(thighRotation - pelvisRotation, -Mathf.Pi, Mathf.Pi);
        var hipDef = body.Def.Joints[hip];
        float hipLo = Math.Min(hipDef.Lower + 0.05f, hipDef.Upper - 0.05f);
        float hipHi = Math.Max(hipDef.Lower + 0.05f, hipDef.Upper - 0.05f);
        thighTarget = Math.Clamp(thighTarget, hipLo, hipHi);
        float kneeTarget = kneeAngle;
        float shinRotation = thighRotation + kneeAngle;
        float ankleTargetAngle = Mathf.Wrap(-shinRotation, -Mathf.Pi, Mathf.Pi);

        brain.MotorTarget[hip] = Math.Clamp(thighTarget, body.Def.Joints[hip].Lower, body.Def.Joints[hip].Upper);
        brain.MotorTarget[knee] = Math.Clamp(kneeTarget, body.Def.Joints[knee].Lower, body.Def.Joints[knee].Upper);
        brain.MotorTarget[ankle] = Math.Clamp(ankleTargetAngle, body.Def.Joints[ankle].Lower, body.Def.Joints[ankle].Upper);
        brain.MotorStiff[hip] *= SwingStiffness;
        brain.MotorStiff[knee] *= SwingStiffness;
        brain.MotorStiff[ankle] *= SwingStiffness;
        if (progress >= 1f - LandPhase)
        {
            brain.MotorStiff[hip] = LandStiff;
            brain.MotorStiff[knee] = LandStiff;
            brain.MotorTarget[knee] = Math.Clamp(
                Math.Max(brain.MotorTarget[knee], LandKnee),
                body.Def.Joints[knee].Lower, body.Def.Joints[knee].Upper);
        }
        if (progress < 1f)
        {
            brain.SwingingJoints[hip] = true;
            brain.SwingingJoints[knee] = true;
            brain.SwingingJoints[ankle] = true;
        }
    }

    void SetLandingTargets(Ragdoll body, Brain brain, int foot)
    {
        bool near = foot == body.FootNear;
        string suffix = near ? "n" : "f";
        int knee = body.Def.JointIndex($"knee_{suffix}");
        int ankle = body.Def.JointIndex($"ankle_{suffix}");
        if (knee < 0 || ankle < 0) return;

        int shin = body.JointParent(ankle);
        float ankleTarget = Mathf.Wrap(-body.Bodies[shin].GlobalRotation, -Mathf.Pi, Mathf.Pi);
        brain.MotorTarget[ankle] = Math.Clamp(
            ankleTarget, body.Def.Joints[ankle].Lower, body.Def.Joints[ankle].Upper);
        brain.MotorTarget[knee] = Math.Clamp(
            Math.Max(brain.MotorTarget[knee], LandKnee),
            body.Def.Joints[knee].Lower, body.Def.Joints[knee].Upper);
    }

    void BeginLanding(Ragdoll body, Brain brain, int foot)
    {
        SetLandingTargets(body, brain, foot);
        bool near = foot == body.FootNear;
        int hip = body.Def.JointIndex(near ? "hip_n" : "hip_f");
        int knee = body.Def.JointIndex(near ? "knee_n" : "knee_f");
        if (hip < 0 || knee < 0) return;
        _landingFoot = foot;
        _landingElapsed = 0f;
        _landingHipTarget = brain.MotorTarget[hip];
        _landingKneeTarget = brain.MotorTarget[knee];
    }

    void ApplyLandingRamp(Ragdoll body, Brain brain, float dt)
    {
        if (_landingFoot < 0) return;
        bool near = _landingFoot == body.FootNear;
        string suffix = near ? "n" : "f";
        int hip = body.Def.JointIndex($"hip_{suffix}");
        int knee = body.Def.JointIndex($"knee_{suffix}");
        int ankle = body.Def.JointIndex($"ankle_{suffix}");
        if (hip < 0 || knee < 0 || ankle < 0) return;

        brain.SwingingJoints[hip] = true;
        brain.SwingingJoints[knee] = true;
        brain.SwingingJoints[ankle] = true;
        float landingX = _swingTargetX;
        SetSwingTargets(body, brain, _landingFoot, landingX, _swingStartY + LandPress, 1f);
        SetLandingTargets(body, brain, _landingFoot);
        if (!body.Grounded[_landingFoot])
        {
            _landingElapsed += dt;
            float timeoutBlend = Math.Clamp(_landingElapsed / Math.Max(LandTimeout, 1e-6f), 0f, 1f);
            brain.MotorTarget[hip] = Mathf.Lerp(_landingHipTarget, BaseTarget(brain, hip), timeoutBlend);
            brain.MotorTarget[knee] = Mathf.Lerp(_landingKneeTarget, LandKnee, timeoutBlend);
            brain.MotorTarget[ankle] = Mathf.Lerp(brain.MotorTarget[ankle], BaseTarget(brain, ankle), timeoutBlend);
            brain.MotorStiff[hip] = Mathf.Lerp(LandStiff, BaseStiff(brain, hip), timeoutBlend);
            brain.MotorStiff[knee] = Mathf.Lerp(LandStiff, BaseStiff(brain, knee), timeoutBlend);
            brain.MotorStiff[ankle] = Mathf.Lerp(LandStiff, BaseStiff(brain, ankle), timeoutBlend);
            if (timeoutBlend >= 1f)
                CompleteStep(body, brain, _landingFoot, timedOut: true);
            return;
        }

        _landingElapsed += dt;
        float blend = Math.Clamp(_landingElapsed / Math.Max(LandRamp, 1e-6f), 0f, 1f);
        brain.MotorTarget[hip] = _landingHipTarget;
        brain.MotorTarget[knee] = Math.Clamp(
            Math.Max(_landingKneeTarget, LandKnee),
            body.Def.Joints[knee].Lower, body.Def.Joints[knee].Upper);
        brain.MotorStiff[hip] = Mathf.Lerp(LandStiff, brain.MotorStiff[hip], blend);
        brain.MotorStiff[knee] = Mathf.Lerp(LandStiff, brain.MotorStiff[knee], blend);
        brain.MotorStiff[ankle] = Mathf.Lerp(LandStiff, brain.MotorStiff[ankle], blend);
        if (blend >= 1f)
            CompleteStep(body, brain, _landingFoot, timedOut: false);
    }

    float BaseTarget(Brain brain, int joint)
    {
        float target = 0f;
        for (int k = 0; k < Protocol.Count; k++)
            target += brain.P[k] * _pose[k][joint];
        return target;
    }

    float BaseStiff(Brain brain, int joint)
    {
        float stiffness = 0f;
        for (int k = 0; k < Protocol.Count; k++)
            stiffness += brain.P[k] * _stiff[k][joint];
        return stiffness;
    }
}