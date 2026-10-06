using Godot;
using System;
using System.Globalization;

// Проба выносливости (--probe): стойка N секунд с периодическим логом.
public partial class Dream
{
    void StartProbe()
    {
        var g = new Ragdoll { Position = new Vector2(0f, -1f), GyroOn = _probeGyro };
        AddChild(g);
        g.Build(_def, 0);
        if (g.Broken)
        {
            GD.PushError("Cannot start endurance probe: ragdoll construction failed.");
            GetTree().Quit(1);
            return;
        }

        g.Dur.Mode = _probeLiveWear ? DamageMode.Live : DamageMode.Off;
        var brain = g.Brain;
        brain.Insert(FirmwareRom.Stock(), true);
        brain.Insert(VestibularRom.Stock(), true);
        brain.Insert(ReflexRom.Stock(), true);
        brain.Insert(new ArbiterRom(), true);

        IChip motor;
        string motorInfo = "chip=MOTOR ROM";
        if (_probeMotor == "rom")
            motor = new MotorRom();
        else
        {
            var chip = ChipFile.LoadMotor(_chipPath, _def, out bool incompatibleIoHash);
            if (chip == null)
            {
                GD.PushError(incompatibleIoHash
                    ? "MOTOR NN: file v3 is incompatible with this probe; retrain with --fresh."
                    : $"Cannot load neural motor chip '{_chipPath}' for endurance probe.");
                GetTree().Quit(1);
                return;
            }
            motor = chip;
            int nonzeroWeights = 0;
            foreach (float weight in chip.W)
                if (weight != 0f) nonzeroWeights++;
            motorInfo = $"chip={chip.Name} generation={chip.Generation} " +
                        $"standMaturity={chip.Maturity[Protocol.Stand]:0.000} " +
                        $"nonzeroWeights={nonzeroWeights}/{chip.W.Length} " +
                        $"path={ProjectSettings.GlobalizePath(_chipPath)}";
        }
        brain.Insert(motor, true);

        var task = new StandTask { EpisodeSeconds = _probeSeconds };
        _probePoseSigma = new RecoverTask().PoseSigma;
        _probeGirl = g;
        _probeEpisode = task.Begin(g, 13013, 1);
        _probeElapsed = 0f;
        _probeNextLog = 2f;
        _probeFinished = false;
        DreamLog.Line($"probe START seconds={_probeSeconds.ToString("0.##", CultureInfo.InvariantCulture)} " +
                      $"motor={_probeMotor} {motorInfo} wear={(_probeLiveWear ? "live" : "off")} " +
                      $"gyro={(_probeGyro ? "on" : "off")} pristine=yes");
    }

    void StepProbe(float dt)
    {
        if (_probeGirl == null || !IsInstanceValid(_probeGirl))
        {
            FinishProbe("body-missing");
            return;
        }
        if (_probeGirl.Broken)
        {
            LogProbe("probe", "event=broken");
            FinishProbe("broken");
            return;
        }

        _probeEpisode.Step(dt, _probeTick++, out bool fell);
        _probeElapsed += dt;

        while (_probeElapsed >= _probeNextLog && _probeNextLog <= _probeSeconds)
        {
            LogProbe("probe");
            _probeNextLog += 2f;
        }

        if (fell)
        {
            var g = _probeGirl;
            string cause = -g.Bodies[g.Head].GlobalPosition.Y < 0.4f * g.HeadHeight0
                ? "fall=head" : "fall=tilt";
            LogProbe("probe", cause);
            FinishProbe(cause);
        }
        else if (_probeElapsed >= _probeSeconds)
            FinishProbe("time");
    }

    void FinishProbe(string reason)
    {
        if (!_probeMode || _probeFinished) return;
        _probeFinished = true;
        LogProbe("probe END", $"reason={reason}");
        _probeEpisode = null;
        _probeGirl = null;
        GetTree().Quit();
    }

    void LogProbe(string prefix, string suffix = "")
    {
        var g = _probeGirl;
        var brain = g?.Brain;
        if (g == null || brain == null || g.Bodies == null)
        {
            DreamLog.Line($"{prefix} t={_probeElapsed.ToString("0.0", CultureInfo.InvariantCulture)} {suffix} " +
                          "comX=n/a xi=n/a margin=n/a pose=n/a Tmax=n/a imax=n/a heat=n/a power=n/a stiffMean=n/a spread=n/a");
            return;
        }

        var balance = brain.Balance;
        var arbiter = brain.Slots[(int)LobeKind.Arbiter].Chip as ArbiterRom;
        string arb = arbiter?.StateName ?? "n/a";
        float comX = balance.Length > Bal.ComX ? balance[Bal.ComX] : float.NaN;
        float xi = balance.Length > Bal.Xi ? balance[Bal.Xi] : float.NaN;
        float margin = balance.Length > Bal.Margin ? balance[Bal.Margin] : float.NaN;
        float pose = ProbePose(g);

        float maxTemp = float.NegativeInfinity;
        int maxTempJoint = -1;
        float minImaxRatio = float.PositiveInfinity;
        int minImaxJoint = -1;
        for (int j = 0; j < g.Temp.Length; j++)
        {
            if (float.IsFinite(g.Temp[j]) && g.Temp[j] > maxTemp)
            {
                maxTemp = g.Temp[j];
                maxTempJoint = j;
            }
            float resistance = g.Resistance(j);
            float ratio = resistance > 0f ? 1f / resistance : float.NaN;
            if (float.IsFinite(ratio) && ratio < minImaxRatio)
            {
                minImaxRatio = ratio;
                minImaxJoint = j;
            }
        }

        float stiff = 0f;
        foreach (float value in brain.Stiff) stiff += value;
        stiff = brain.Stiff.Length > 0 ? stiff / brain.Stiff.Length : float.NaN;
        float spread = MathF.Abs(g.Bodies[g.FootNear].GlobalPosition.X -
                                 g.Bodies[g.FootFar].GlobalPosition.X);
        float pStand = brain.P.Length > Protocol.Stand ? brain.P[Protocol.Stand] : float.NaN;
        float pRecover = brain.P.Length > Protocol.Recover ? brain.P[Protocol.Recover] : float.NaN;

        string line = FormattableString.Invariant(
            $"{prefix} t={_probeElapsed:0.0} arb={arb} p=[S {pStand:0.00} R {pRecover:0.00}] comX={comX:+0.000;-0.000;0.000} xi={xi:+0.000;-0.000;0.000} margin={margin:0.00} поза={pose:0.00} Tmax={ProbeJointMetric(g, maxTemp, maxTempJoint, "0.0")} imax={ProbeJointMetric(g, minImaxRatio, minImaxJoint, "0.00")} heat={ProbeNumber(g.Heat, "0.00")} power={ProbeNumber(g.Power, "0.00")} stiffMean={ProbeNumber(stiff, "0.00")} spread={ProbeNumber(spread, "0")}px");
        DreamLog.Line(string.IsNullOrEmpty(suffix) ? line : $"{line} {suffix}");
    }

    float ProbePose(Ragdoll g)
    {
        if (_probePoseSigma <= 0f) return float.NaN;
        float err = 0f, weightSum = 0f;
        for (int j = 0; j < g.Def.Joints.Count; j++)
        {
            string name = g.Def.Joints[j].Name;
            float weight = name.StartsWith("shoulder") || name.StartsWith("elbow") ? 0.3f :
                           name == "neck" ? 0.5f : 1f;
            float difference = g.Angle[j] - MotorRom.Stand(name).Item1;
            err += weight * difference * difference;
            weightSum += weight;
        }
        return weightSum > 0f
            ? MathF.Exp(-(err / weightSum) / (_probePoseSigma * _probePoseSigma))
            : float.NaN;
    }

    static string ProbeJointMetric(Ragdoll g, float value, int joint, string format)
    {
        if (!float.IsFinite(value) || joint < 0 || joint >= g.Def.Joints.Count)
            return "n/a";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)}({g.Def.Joints[joint].Name})";
    }

    static string ProbeNumber(float value, string format) =>
        float.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "n/a";
}
