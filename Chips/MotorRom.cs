using Godot;
using System;

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
    public bool CapturePointStepping = false;
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
