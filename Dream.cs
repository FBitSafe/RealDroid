using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;

public partial class Dream : Node2D
{
    [Export] public int Pairs = 16;
    [Export] public int Hidden1 = 32;
    [Export] public int Hidden2 = 32;
    [Export] public float Sigma = 0.04f;
    [Export] public float LearningRate = 0.03f;
    [Export] public float ReplayShare = 0.5f;
    [Export] public float MatureThreshold = 0.5f;
    [Export] public float SharedDamp = 4f;
    [Export] public float ReplayFloor = 0.6f;
    [Export] public float GyroOffShare = 0.3f;
    [Export] public int MaxGenerations = 0;
    [Export] public int CpStepFadeGenerations = 100;
    [Export] public bool UseDamage = true;

    const uint CloneLayer = 1u << 5;
    static readonly Color Ghost = new(0.55f, 0.75f, 1f, 0.08f);
    static readonly Color ExamMain = Colors.White;
    static readonly Color ExamOther = new(1f, 1f, 1f, 0.3f);
    static readonly Color ReplayColor = new(0.6f, 1f, 0.6f, 0.5f);

    sealed class Run
    {
        public Ragdoll G;
        public IEpisode Ep;
        public IDreamTask Task;
        public int Role;              // 0 популяция, 1 экзамен, 2 повторение
        public bool Alive = true, Fell;
        public float Fit;
        public int Ticks, Limit;
    }

    RagdollDef _def;
    MotorSpec _spec;
    NeuralMotorChip _chip;
    IDreamTask _main;
    readonly List<IDreamTask> _replay = new();
    EsOptimizer _es;
    List<Run> _runs;
    float[] _theta;
    int _N, _sector, _tick, _gen, _startGen, _examOk, _examTotal;
    float _best = float.MinValue, _exam, _examQ, _replayScore, _mean, _shared;
    float _replayStart = float.NaN;
    bool _headless, _fresh, _failed;
    bool _protocolTest, _allowSwingLegReflex;
    bool _disableCapturePointStepping;
    float[] _protocolLevels;
    int _protocolLevelIndex = -1;
    bool _probeMode, _probeInvalid, _probeFinished;
    bool _probeLiveWear, _probeGyro;
    float _probeSeconds, _probeElapsed, _probeNextLog = 2f, _probePoseSigma;
    int _probeTick;
    string _probeMotor = "rom";
    Ragdoll _probeGirl;
    IEpisode _probeEpisode;
    float _sharedOverride = -1f, _levelOverride = -1f, _minLevelOverride = -1f;
    bool _protocolTestFinished;
    string _chipPath;
    readonly Random _rng = new(7);
    Label _hud;
    DreamScoreGraph _penaltyGraph, _rewardGraph;
    Camera2D _cam;
    Run _examRun;
    int _lastGenTicks, _failBroken, _failHead, _failTilt, _quickGens;
    ulong _rateT0; int _rateTicks; float _rate;
    float _scoreUiTimer;

    public override void _Ready()
    {
        _headless = DisplayServer.GetName() == "headless";
        if (_headless) OS.LowProcessorUsageModeSleepUsec = 0;
        _sector = DreamJob.Sector;
        _chipPath = DreamJob.ChipPath;
        ParseArgs();
        if (_probeInvalid)
        {
            GetTree().Quit(1);
            return;
        }

        var floor = new StaticBody2D
        {
            CollisionLayer = 1, CollisionMask = 0,
            PhysicsMaterialOverride = new PhysicsMaterial { Friction = 1f }
        };
        floor.AddChild(new CollisionShape2D { Shape = new WorldBoundaryShape2D() });
        AddChild(floor);

        if (!_headless)
        {
            RenderingServer.SetDefaultClearColor(new Color(0.05f, 0.05f, 0.09f));
            _cam = new Camera2D { Zoom = new Vector2(2.5f, 2.5f), Position = new Vector2(0, -90) };
            AddChild(_cam); _cam.MakeCurrent();
            var ui = new CanvasLayer();
            _hud = new Label { Position = new Vector2(12, 10) };
            ui.AddChild(_hud); AddChild(ui);
            _penaltyGraph = new DreamScoreGraph
            {
                IsPenaltyGraph = true,
                AnchorLeft = 0f, AnchorTop = 1f, AnchorRight = 0f, AnchorBottom = 1f,
                OffsetLeft = 12f, OffsetTop = -372f, OffsetRight = 352f, OffsetBottom = -12f,
            };
            _rewardGraph = new DreamScoreGraph
            {
                AnchorLeft = 1f, AnchorTop = 1f, AnchorRight = 1f, AnchorBottom = 1f,
                OffsetLeft = -352f, OffsetTop = -372f, OffsetRight = -12f, OffsetBottom = -12f,
            };
            ui.AddChild(_penaltyGraph);
            ui.AddChild(_rewardGraph);
        }

        _def = RagdollDef.Girl();
        _spec = NeuralMotorChip.SpecFor(_def, Hidden1, Hidden2);

        if (_probeMode)
        {
            StartProbe();
            return;
        }

        bool incompatibleIoHash = false;
        _chip = _fresh || _protocolTest ? null : ChipFile.LoadMotor(_chipPath, _def, out incompatibleIoHash);
        if (incompatibleIoHash)
        {
            _failed = true;
            GD.PushError("MOTOR NN: файл v3 несовместим, нужен сон с --fresh");
            if (_headless) GetTree().Quit(1);
            return;
        }
        if (_chip != null && _chip.Spec != _spec) _chip = null;
        if (_chip == null)
        {
            _chip = NeuralMotorChip.Blank(_def, 1, Hidden1, Hidden2);
            _chip.Name = "MOTOR NN";
            GD.Print($"Пустой чип, {_chip.W.Length} параметров");
        }
        else GD.Print($"Продолжаю: {_chip.Label}");
        _gen = _startGen = _chip.Generation;

        _main = DreamTasks.Make(_sector);
        if (_main == null)
        {
            _failed = true;
            GD.PushError($"Нет учебной программы для сектора {Protocol.Names[_sector]}");
            if (_headless) GetTree().Quit(1);
            return;
        }
        if (_protocolTest && _sector != Protocol.Recover)
        {
            _failed = true;
            GD.PushError("The Motor ROM stepping test requires the RECOVER sector.");
            if (_headless) GetTree().Quit(1);
            return;
        }
        if (_main is RecoverTask recoverTask && _levelOverride >= 0f)
            recoverTask.LevelFwd = recoverTask.LevelBack = _levelOverride;
        if (_main is RecoverTask recoverWithFloor && _minLevelOverride >= 0f)
        {
            recoverWithFloor.MinExamLevel = _minLevelOverride;
            recoverWithFloor.LevelFwd = Math.Max(recoverWithFloor.LevelFwd, _minLevelOverride);
            recoverWithFloor.LevelBack = Math.Max(recoverWithFloor.LevelBack, _minLevelOverride);
        }

        float other = 0f;
        for (int k = 0; k < Protocol.Count; k++)
        {
            if (k == _sector) continue;
            other += _chip.Maturity[k];
            if (_chip.Maturity[k] >= MatureThreshold && DreamTasks.Make(k) is { } t) _replay.Add(t);
        }
        _shared = _sharedOverride >= 0f ? _sharedOverride : 1f / (1f + SharedDamp * other);
        _es = new EsOptimizer(_chip.W, Pairs, Sigma, LearningRate, seed: 1) { Scale = _chip.Mask(_sector, _shared) };

        GD.Print($"Сектор {Protocol.Names[_sector]}, общие веса x{_shared:F2}, повторение: {_replay.Count}, " +
                 $"травмы: {(UseDamage && DreamJob.Damage != null ? "да (Frozen)" : "нет")}");
        if (_protocolTest)
            GD.Print($"ROM test, levels={(_protocolLevels == null ? (_main as RecoverTask)?.Status : string.Join(",", _protocolLevels))}; " +
                     "neural weights will not be used or saved.");
        else
            GD.Print($"MOTOR ROM capture-point stepping: {(_disableCapturePointStepping ? "OFF" : $"{CapturePointAssistForGeneration():P0} now, fade over {CpStepFadeGenerations} Recover generations")}; " +
                     $"сохраняю в {ProjectSettings.GlobalizePath(_chipPath)}");
        StartGeneration();
    }

    void ParseArgs()
    {
        var a = OS.GetCmdlineUserArgs();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--probe":
                    _probeMode = true;
                    if (i + 1 < a.Length &&
                        float.TryParse(a[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) &&
                        float.IsFinite(seconds) && seconds > 0f)
                        _probeSeconds = seconds;
                    else
                    {
                        GD.PushError("--probe requires a positive number of seconds.");
                        _probeInvalid = true;
                    }
                    break;
                case "--motor":
                    if (i + 1 < a.Length && (a[i + 1] == "rom" || a[i + 1] == "nn"))
                        _probeMotor = a[++i];
                    else
                    {
                        GD.PushError("--motor must be rom or nn.");
                        _probeInvalid = true;
                    }
                    break;
                case "--wear":
                    if (i + 1 < a.Length && (a[i + 1] == "off" || a[i + 1] == "live"))
                        _probeLiveWear = a[++i] == "live";
                    else
                    {
                        GD.PushError("--wear must be off or live.");
                        _probeInvalid = true;
                    }
                    break;
                case "--gyro":
                    if (i + 1 < a.Length && (a[i + 1] == "off" || a[i + 1] == "on"))
                        _probeGyro = a[++i] == "on";
                    else
                    {
                        GD.PushError("--gyro must be off or on.");
                        _probeInvalid = true;
                    }
                    break;
                case "--gens":
                    if (i + 1 < a.Length) MaxGenerations = a[++i].ToInt();
                    break;
                case "--cp-step-fade-gens":
                    if (i + 1 < a.Length && int.TryParse(a[++i], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int fadeGenerations) && fadeGenerations >= 0)
                        CpStepFadeGenerations = fadeGenerations;
                    else
                    {
                        GD.PushError("--cp-step-fade-gens must be a non-negative integer.");
                        _probeInvalid = true;
                    }
                    break;
                case "--sector":
                    if (i + 1 < a.Length)
                    {
                        int s = Array.IndexOf(Protocol.Names, a[++i].ToUpper());
                        if (s >= 0) _sector = s; else GD.PushError($"Неизвестный сектор {a[i]}");
                    }
                    break;
                case "--chip":
                    if (i + 1 < a.Length) _chipPath = a[++i];
                    break;
                case "--shared":
                    if (i + 1 < a.Length && float.TryParse(a[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float shared) &&
                        float.IsFinite(shared) && shared >= 0f && shared <= 1f)
                        _sharedOverride = shared;
                    else GD.PushError("--shared must be a number from 0 to 1.");
                    break;
                case "--level":
                    if (i + 1 < a.Length && float.TryParse(a[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float level) &&
                        float.IsFinite(level) && level >= 0f && level <= 1f)
                        _levelOverride = level;
                    else GD.PushError("--level must be a number from 0 to 1.");
                    break;
                case "--min-level":
                    if (i + 1 < a.Length && float.TryParse(a[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float minLevel) &&
                        float.IsFinite(minLevel) && minLevel >= 0f && minLevel <= 1f)
                        _minLevelOverride = minLevel;
                    else GD.PushError("--min-level must be a number from 0 to 1.");
                    break;
                case "--motor-rom-step-test":
                    _protocolTest = true;
                    break;
                case "--no-cp-step":
                    _disableCapturePointStepping = true;
                    break;
                case "--levels":
                    if (i + 1 >= a.Length)
                    {
                        GD.PushError("--levels requires comma-separated values from 0 to 1.");
                        _probeInvalid = true;
                        break;
                    }
                    var levels = new List<float>();
                    bool validLevels = true;
                    foreach (string text in a[++i].Split(','))
                    {
                        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                            !float.IsFinite(value) || value < 0f || value > 1f)
                        {
                            validLevels = false;
                            break;
                        }
                        levels.Add(value);
                    }
                    if (validLevels && levels.Count > 0)
                        _protocolLevels = levels.ToArray();
                    else
                    {
                        GD.PushError("--levels requires comma-separated values from 0 to 1.");
                        _probeInvalid = true;
                    }
                    break;
                case "--allow-swing-leg-reflex":
                    _allowSwingLegReflex = true;
                    break;
                case "--fresh": _fresh = true; break;
                case "--pristine": UseDamage = false; break;
            }
        }
    }

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

    void StartGeneration()
    {
        if (_runs != null) foreach (var r in _runs) if (IsInstanceValid(r.G)) r.G.QueueFree();
        _runs = new List<Run>();
        _failBroken = _failHead = _failTilt = 0;

        _theta = (float[])_es.Theta.Clone();
        if (_protocolTest)
        {
            _N = 0;
            _protocolLevelIndex = 0;
            StartProtocolLevel();
            return;
        }

        var pop = _es.Ask();
        _N = pop.Length;
        for (int p = 0; p < _N / 2; p++)
        {
            int seed = _rng.Next();
            var task = _replay.Count > 0 && _rng.NextDouble() < ReplayShare ? _replay[_rng.Next(_replay.Count)] : _main;
            _runs.Add(MakeRun(pop[2 * p], task, seed, -1, 0));
            _runs.Add(MakeRun(pop[2 * p + 1], task, seed, -1, 0));
        }

        for (int e = 0; e < _main.ExamCount; e++) _runs.Add(MakeRun(_theta, _main, 1000 + e, e, 1));
        _examRun = _runs[_N];
        foreach (var t in _replay) _runs.Add(MakeRun(_theta, t, 2000, 0, 2));
        _tick = 0;
    }

    void StartProtocolLevel()
    {
        if (_protocolLevels != null)
            _main = DreamTasks.Make(Protocol.Recover);

        if (_main is RecoverTask task)
        {
            float level = _protocolLevels == null
                ? (_levelOverride >= 0f ? _levelOverride : task.LevelFwd)
                : _protocolLevels[_protocolLevelIndex];
            task.LevelFwd = task.LevelBack = level;
            if (_minLevelOverride >= 0f) task.MinExamLevel = _minLevelOverride;
        }

        _protocolTestFinished = false;
        _runs.Clear();
        for (int e = 0; e < _main.ExamCount; e++)
            _runs.Add(MakeRun(_theta, _main, 1000 + e, e, 1));
        _examRun = _runs[0];
        _tick = 0;
    }

    Run MakeRun(float[] w, IDreamTask task, int seed, int examIdx, int role)
    {
        var g = new Ragdoll { Position = new Vector2(0, -1f) };
        if (examIdx < 0)
            g.GyroOn = new Random(seed ^ 0x5EED).NextDouble() >= GyroOffShare;
        else
            g.GyroOn = true;
        AddChild(g);
        g.Build(_def, 0);
        var r = new Run
        {
            G = g, Task = task, Role = role,
            Limit = Math.Max(1, (int)(task.Seconds * Engine.PhysicsTicksPerSecond)),
        };
        if (g.Broken) { r.Alive = false; r.Fell = true; return r; }

        foreach (var ch in g.GetChildren()) if (ch is DebugOverlay || ch is DamageFx) ch.QueueFree();
        foreach (var b in g.Bodies) { b.CollisionLayer = CloneLayer; b.CollisionMask = 1; }

        if (UseDamage && DreamJob.Damage != null) { g.Dur.Mode = DamageMode.Frozen; DreamJob.Damage.Apply(g); }
        else g.Dur.Mode = DamageMode.Off;

        var br = g.Brain;
        br.Insert(FirmwareRom.Stock(), true);
        br.Insert(VestibularRom.Stock(), true);
        var reflex = ReflexRom.Stock();
        reflex.DoNotTouchSwingingLeg = _protocolTest && !_allowSwingLegReflex;
        br.Insert(reflex, true);
        br.Insert(task.MakeArbiter(), true);
        IChip motorChip;
        if (_protocolTest)
            motorChip = new MotorRom();
        else
        {
            var neuralMotor = new NeuralMotorChip(_spec, w)
            {
                BaseStepping = !_disableCapturePointStepping,
                BaseStepAssist = _disableCapturePointStepping ? 0f : CapturePointAssistForGeneration(),
            };
            motorChip = neuralMotor;
        }
        br.Insert(motorChip, true);

        g.Modulate = role == 0 ? Ghost : role == 2 ? ReplayColor : examIdx == 0 ? ExamMain : ExamOther;
        g.ZIndex = role == 0 ? 0 : role == 1 && examIdx == 0 ? 10 : 5;
        r.Ep = task.Begin(g, seed, examIdx);
        return r;
    }

    float CapturePointAssistForGeneration()
    {
        if (CpStepFadeGenerations <= 0) return 0f;
        int recoverGeneration = _chip.SectorGen[Protocol.Recover];
        if (_sector == Protocol.Recover)
            recoverGeneration += Math.Max(0, _gen - _startGen);
        return Math.Clamp(1f - recoverGeneration / (float)CpStepFadeGenerations, 0f, 1f);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_probeMode)
        {
            if (_probeGirl != null) StepProbe((float)delta);
            return;
        }
        if (_failed || _runs == null) return;
        CountRate();
        float dt = (float)delta;

        int alive = 0;
        foreach (var r in _runs)
        {
            if (!r.Alive) continue;
            if (r.G.Broken) { Fail(r); continue; }
            float rew = r.Ep.Step(dt, _tick, out bool failed);
            if (failed) { Fail(r); continue; }
            r.Fit += rew;
            if (++r.Ticks >= r.Limit) { r.Alive = false; continue; }
            alive++;
        }

        _tick++;
        if (alive == 0)
        {
            if (_protocolTest) EndProtocolTest();
            else EndGeneration();
        }
    }

    void EndProtocolTest()
    {
        if (_protocolTestFinished) return;
        _examTotal = _runs.Count;
        _examOk = 0;
        float score = 0f;
        float maxKnee = 0f, maxImpact = 0f;
        var steps = new List<int>();
        var timeouts = new List<int>();
        var backStatus = new List<string>();
        for (int i = 0; i < _runs.Count; i++)
        {
            var r = _runs[i];
            float fit = r.Fit / r.Limit;
            score += fit;
            bool passed = !r.Fell && (r.Ep is not IExamVerdict verdict || verdict.Passed);
            if (passed) _examOk++;
            var motor = r.G?.Brain.Slots[(int)LobeKind.Motor].Chip as MotorRom;
            int stepCount = motor?.CapturePointSteps ?? 0;
            int timeoutCount = motor?.CapturePointTimeouts ?? 0;
            steps.Add(stepCount);
            timeouts.Add(timeoutCount);
            if (r.Ep is RecoverEpisode recover)
            {
                maxKnee = Math.Max(maxKnee, recover.MaxKnee);
                maxImpact = Math.Max(maxImpact, recover.MaxImpact);
            }
            if ((i & 1) == 1)
                backStatus.Add(r.Fell ? "ПАДЕНИЕ" : passed ? "СТОИТ" : "НЕ ВЕРНУЛАСЬ");
            GD.Print($"ROM step exam {i + 1}/{_runs.Count}: passed={passed}, survived={!r.Fell}, " +
                     $"steps={stepCount}, timeouts={timeoutCount}, knee={((r.Ep as RecoverEpisode)?.MaxKnee ?? 0f):F2}, " +
                     $"impact={((r.Ep as RecoverEpisode)?.MaxImpact ?? 0f):F0}, ticks={r.Ticks}/{r.Limit}");
        }
        _exam = score / Math.Max(_examTotal, 1);
        var reach = _runs[0].G?.Brain.Slots[(int)LobeKind.Motor].Chip as MotorRom;
        float levelDone = _protocolLevels == null
            ? (_main as RecoverTask)?.LevelFwd ?? 0f
            : _protocolLevels[_protocolLevelIndex];
        GD.Print($"ROMSTEP level={levelDone:F2} pass={_examOk}/{_examTotal} steps=[{string.Join(",", steps)}] " +
                 $"timeouts=[{string.Join(",", timeouts)}] kneeMax={maxKnee:F2} impactMax={maxImpact:F0} " +
                 $"back={string.Join("/", backStatus)} reach(back={reach?.BackStepMax:F1}px,forward={reach?.FwdStepMax:F1}px) " +
                 $"swing-leg-reflex-protection={(!_allowSwingLegReflex ? "on" : "off")}.");

        if (_protocolLevels != null && ++_protocolLevelIndex < _protocolLevels.Length)
        {
            foreach (var r in _runs)
                if (IsInstanceValid(r.G)) r.G.QueueFree();
            StartProtocolLevel();
            return;
        }
        _protocolTestFinished = true;
        if (_headless) GetTree().Quit();
    }

    void Fail(Run r)
    {
        var fg = r.G;
        if (fg == null || !IsInstanceValid(fg) || fg.Broken || fg.Bodies == null) _failBroken++;
        else if (-fg.Bodies[fg.Head].GlobalPosition.Y < 0.4f * fg.HeadHeight0) _failHead++;
        else _failTilt++;
        r.Alive = false;
        r.Fell = true;
        r.Ep?.MarkFailed();
        r.Fit -= r.Task.FallPenalty * (r.Limit - r.Ticks);   // ранний провал хуже позднего
        if (r.Role == 1 && IsInstanceValid(r.G)) r.G.Modulate = new Color(1f, 0.5f, 0.5f, r.G.Modulate.A);
    }

    void EndGeneration()
    {
        _lastGenTicks = _tick;
        if (_tick < 5)
        {
            _quickGens++;
            if (_quickGens <= 5 || _quickGens % 100 == 0)
                GD.PushWarning($"Поколение закончилось за {_tick} тиков: взрыв {_failBroken}, голова низко {_failHead}, наклон {_failTilt}");
        }

        var fit = new float[_N];
        float mean = 0f;
        for (int i = 0; i < _N; i++) { fit[i] = _runs[i].Fit / _runs[i].Limit; mean += fit[i]; }
        _mean = mean / _N;

        _exam = 0f; _examQ = 0f; _examOk = 0; _examTotal = 0; _replayScore = 0f;
        int nr = 0;
        for (int i = _N; i < _runs.Count; i++)
        {
            var r = _runs[i];
            float f = r.Fit / r.Limit;
            if (r.Role == 1)
            {
                _exam += f;
                _examQ += r.Ep?.Quality ?? 0f;
                _examTotal++;
                if (!r.Fell && (r.Ep is not IExamVerdict verdict || verdict.Passed)) _examOk++;
            }
            else { _replayScore += f; nr++; }
        }
        _exam /= Math.Max(_examTotal, 1);
        _examQ /= Math.Max(_examTotal, 1);
        if (nr > 0) _replayScore /= nr;

        bool replaySaveAllowed = true;
        if (_main is RecoverTask && nr > 0)
        {
            if (float.IsNaN(_replayStart))
            {
                _replayStart = _replayScore;
                float initialFloor = Math.Min(ReplayFloor, _replayStart - 0.05f);
                DreamLog.Line($"replay floor {initialFloor:0.00}");
            }

            float floor = Math.Min(ReplayFloor, _replayStart - 0.05f);
            replaySaveAllowed = _replayScore >= floor;
        }

        var cur = _main as ICurriculum;
        float bonus = cur?.Bonus(_examOk, _examTotal) ?? 0f;
        string level = cur?.Status ?? "";
        cur?.Update(_examOk, _examTotal);
        float stepAssist = _disableCapturePointStepping ? 0f : CapturePointAssistForGeneration();

        _es.Tell(fit);
        _gen++;

        float score = _exam + bonus + (nr > 0 ? 0.5f * _replayScore : 0f);
        string saved = "";
        if (score > _best && replaySaveAllowed)
        {
            _best = score;
            var c = _chip.CloneWith((float[])_theta.Clone());
            c.Name = "MOTOR NN";
            c.Generation = _gen;
            c.Maturity[_sector] = _examQ * _examOk / Math.Max(_examTotal, 1);
            c.SectorGen[_sector] = _chip.SectorGen[_sector] + (_gen - _startGen);
            if (ChipFile.Save(_chipPath, c)) saved = "  ★";
        }

        DreamLog.Line($"gen {_gen,4}  {Protocol.Names[_sector]}  exam {_exam:F3} ({_examOk}/{_examTotal}) pose {_examQ:P0}" +
                      (nr > 0 ? $"  replay {_replayScore:F3}" : "") +
                      $"  {level}  CP step {stepAssist:P0}  mean {_mean:F3}  best {_best:F3}  {_rate:F0} t/s{saved}");

        if (_headless && MaxGenerations > 0 && _gen - _startGen >= MaxGenerations) { GetTree().Quit(); return; }
        StartGeneration();
    }

    int CountAlive()
    {
        int n = 0;
        if (_runs != null) foreach (var r in _runs) if (r.Alive) n++;
        return n;
    }

    bool ExamPos(out Vector2 p)
    {
        p = default;
        var g = _examRun?.G;
        if (g == null || !IsInstanceValid(g) || g.Bodies == null || g.Broken) return false;
        p = g.Bodies[g.Head].GlobalPosition;
        return p.IsFinite();
    }

    public override void _Draw()
    {
        if (_headless) return;
        DrawRect(new Rect2(-20000, 0, 40000, 2000), new Color(0.12f, 0.13f, 0.17f));
        DrawLine(new Vector2(-20000, 0), new Vector2(20000, 0), new Color(0.35f, 0.4f, 0.5f), 1f);
    }

    void CountRate()
    {
        _rateTicks++;
        ulong now = Time.GetTicksMsec();
        if (now - _rateT0 >= 1000)
        {
            _rate = _rateTicks * 1000f / (now - _rateT0);
            _rateTicks = 0; _rateT0 = now;
        }
    }

    public override void _Process(double delta)
    {
        if (_probeMode)
        {
            if (_hud != null)
                _hud.Text = $"ENDURANCE PROBE {(_probeGirl == null ? "finished" : $"{_probeElapsed:0.0}/{_probeSeconds:0.0}s")}";
            return;
        }
        if (_hud == null) return;
        _scoreUiTimer += (float)delta;
        if (_scoreUiTimer >= 0.05f)
        {
            _scoreUiTimer %= 0.05f;
            _penaltyGraph.SetTerms(_examRun?.Ep?.Penalties);
            _rewardGraph.SetTerms(_examRun?.Ep?.Rewards);
        }
        if (_cam != null && ExamPos(out var head))
            _cam.Position = _cam.Position.Lerp(new Vector2(head.X, -90), 1f - Mathf.Exp(-3f * (float)delta));
        if (_failed) { _hud.Text = "Нет учебной программы для этого сектора\n[Esc] проснуться"; return; }
        if (_protocolTestFinished)
        {
            _hud.Text = $"Motor ROM step test\nSurvived: {_examOk}/{_examTotal}\nMean score: {_exam:F3}\n[Esc] wake";
            return;
        }
        int ep = Math.Max(1, (int)(_main.Seconds * Engine.PhysicsTicksPerSecond));
        _hud.Text =
            $"СОН · сектор {Protocol.Names[_sector]}\n" +
            $"Поколение: {_gen}   Эпизод: {Math.Min(100, _tick * 100 / ep)}%\n" +
            $"Экзамен: {_exam:F3}   устояла {_examOk}/{_examTotal}   поза {_examQ:P0}\n" +
            (_replay.Count > 0 ? $"Повторение старых секторов: {_replayScore:F3}\n" : "") +
            ((_main as ICurriculum)?.Status is { } cs ? $"Сложность: {cs}\n" : "") +
            $"Среднее популяции: {_mean:F3}   Лучший: {_best:F3}\n" +
            $"Общие веса x{_shared:F2}   Травмы: {(UseDamage && DreamJob.Damage != null ? "заморожены" : "нет")}\n" +
            $"Скорость: {_rate / Engine.PhysicsTicksPerSecond:F1}×\n" +
            $"Клонов: {CountAlive()}/{_runs?.Count ?? 0}   прошлое поколение: {_lastGenTicks} тиков\n" +
            $"Падения: взрыв {_failBroken}, голова {_failHead}, наклон {_failTilt}\n" +
            (ExamPos(out var epPos) ? $"Экзамен: голова x {epPos.X:0} y {epPos.Y:0}\n\n" : "Экзамен: тела нет\n\n") +
            "Белая — экзамен с ветром, бледная — штиль\n" +
            "[Esc] проснуться (лучший чип сохранён)";
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            DreamJob.Waking = true;
            GetTree().ChangeSceneToFile("res://Main.tscn");
        }
    }
}
