using Godot;
using System;
using System.Collections.Generic;

public partial class Ragdoll : Node2D
{
    public const float Gravity = 980f;
    public const float RefHeight = 100f;
    const uint GirlLayers = 0b1110;

    [ExportGroup("Механика")]
    [Export] public float StopStrength = 2f;

    [ExportGroup("Катушки")]
    [Export] public float SupplyVoltage = 1.5f;
    [Export] public float Ambient = 25f;
    [Export] public float HeatGain = 100f;
    [Export] public float ThermalTau = 20f;
    [Export] public float Alpha = 0.004f;
    [Export] public float CritTemp = 120f;
    [Export] public float OverheatDamage = 0.002f;
    [Export] public float WearRate = 2e-6f;

    [ExportGroup("Гироскоп (аугментация)")]
    [Export] public bool GyroOn = true;
    [Export] public float GyroStrength = 0.6f;
    [Export] public float GyroZeta = 0.7f;

    public RagdollDef Def;
    public PartBody[] Bodies;
    public Brain Brain;
    public Durability Dur;
    public Nociception Nerves;
    public int Pelvis, Chest, Head, FootNear, FootFar;
    public int[] Hips, Ankles, Spine, Legs;
    public float Mgh, TotalMass, HeadHeight0;

    // датчики (истина физики; мозг видит их через Dur.Sense)
    public float[] Angle, AngVel, JointInertia;
    public bool[] Grounded;
    public Vector2 Com, ComVel;
    public bool HasSupport;
    public float SupportMin, SupportMax;

    // катушки
    public float[] CurrentCmd, Current, Torque, Temp, Damage;
    public float Power, Heat, Effort;

    public bool Broken;
    public int Clamps;

    int[] _jp, _jc, _feet, _partJoint;
    Vector2[] _anchorInChild, _pos;
    float[] _mass, _inertia;
    int _tick, _visTick;
    float _lastGlow = -1f;
    readonly Random _rng = new(1);

    public int JointParent(int j) => _jp[j];
    public int JointChild(int j) => _jc[j];
    public Vector2 JointPos(int j) => Bodies[_jc[j]].ToGlobal(_anchorInChild[j]);
    public float RatedTorque(int j) => Def.Joints[j].Strength * Mgh;
    public float Resistance(int j) =>
        (1f + Alpha * (Temp[j] - Ambient)) * (1f + (Dur != null && Dur.Effects ? Damage[j] : 0f));
    public float Tilt(int part) => Mathf.Wrap(Bodies[part].GlobalRotation, -Mathf.Pi, Mathf.Pi);
    public void CutPower() => Array.Clear(CurrentCmd);
    public void UntwistAngles() { for (int j = 0; j < Angle.Length; j++) Angle[j] = Mathf.Wrap(Angle[j], -Mathf.Pi, Mathf.Pi); }

    public static float Spd(float err, float w, float kp, float kd, float I, float dt)
        => (kp * err - (kp * dt + kd) * w) / (1f + (kp * dt * dt + kd * dt) / I);

    public void Build(RagdollDef def, int girlIndex)
    {
        Def = def;
        int np = def.Parts.Count, nj = def.Joints.Count;
        Bodies = new PartBody[np];
        _mass = new float[np]; _inertia = new float[np]; _pos = new Vector2[np];
        Grounded = new bool[np];

        uint layer = 1u << (1 + girlIndex);
        uint mask = 1u | (GirlLayers & ~layer);

        var feet = new List<int>();
        for (int i = 0; i < np; i++)
        {
            var p = def.Parts[i];
            var b = new PartBody
            {
                Name = p.Name, Position = p.Center,
                CollisionLayer = layer, CollisionMask = mask, CanSleep = false,
                PhysicsMaterialOverride = new PhysicsMaterial { Friction = p.IsFoot ? 1.2f : 0.5f, Bounce = 0f },
            };
            b.Setup(p);
            if (p.IsFoot) { b.ContactMonitor = true; b.MaxContactsReported = 4; feet.Add(i); }
            AddChild(b);
            Bodies[i] = b; _mass[i] = p.Mass; _inertia[i] = b.Inertia; TotalMass += p.Mass;
        }
        _feet = feet.ToArray();

        Pelvis = def.PartIndex("pelvis"); Chest = def.PartIndex("chest"); Head = def.PartIndex("head");
        FootNear = def.PartIndex("foot_n"); FootFar = def.PartIndex("foot_f");
        if (Pelvis < 0 || Chest < 0 || Head < 0 || FootNear < 0 || FootFar < 0)
        {
            GD.PushError("RagdollDef устарел: нужны pelvis, chest, head, foot_n, foot_f");
            Broken = true;
            return;
        }
        Mgh = TotalMass * Gravity * RefHeight;
        HeadHeight0 = -def.Parts[Head].Center.Y;

        _jp = new int[nj]; _jc = new int[nj]; _anchorInChild = new Vector2[nj];
        _partJoint = new int[np];
        Array.Fill(_partJoint, -1);
        var hips = new List<int>(); var ankles = new List<int>();
        var spine = new List<int>(); var legs = new List<int>();
        for (int j = 0; j < nj; j++)
        {
            var jd = def.Joints[j];
            _jp[j] = def.PartIndex(jd.Parent);
            _jc[j] = def.PartIndex(jd.Child);
            _partJoint[_jc[j]] = j;
            _anchorInChild[j] = jd.Anchor - def.Parts[_jc[j]].Center;
            string n = jd.Name;
            if (n.StartsWith("hip")) hips.Add(j);
            if (n.StartsWith("ankle")) ankles.Add(j);
            if (n == "lumbar" || n == "thoracic") spine.Add(j);
            if (n.StartsWith("hip") || n.StartsWith("knee") || n.StartsWith("ankle")) legs.Add(j);

            var pin = new PinJoint2D { Name = jd.Name, Position = jd.Anchor, Softness = 0f };
            AddChild(pin);
            pin.NodeA = pin.GetPathTo(Bodies[_jp[j]]);
            pin.NodeB = pin.GetPathTo(Bodies[_jc[j]]);
        }
        Hips = hips.ToArray(); Ankles = ankles.ToArray(); Spine = spine.ToArray(); Legs = legs.ToArray();

        Angle = new float[nj]; AngVel = new float[nj]; JointInertia = new float[nj];
        CurrentCmd = new float[nj]; Current = new float[nj]; Torque = new float[nj];
        Temp = new float[nj]; Damage = new float[nj];
        Array.Fill(Temp, Ambient);

        Dur = new Durability(this);
        Brain = new Brain(this, nj, Engine.PhysicsTicksPerSecond);
        Nerves = new Nociception(this, Brain);
        AddChild(new DebugOverlay { Girl = this });
        AddChild(new DamageFx { Girl = this });
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Bodies == null || Broken || Brain == null) return;
        if (!StateIsFinite()) return;
        float dt = (float)delta;
        ReadSensors();
        Dur.Tick(dt);
        Dur.Sense(Brain);
        Nerves.Tick(dt);
        Brain.Tick(_tick++, dt);
        DriveCoils(dt);
        ApplyStops(dt);
        ApplyGyro(dt);
        UpdateVisuals();
    }

    bool StateIsFinite()
    {
        foreach (var b in Bodies)
        {
            if (b.GlobalPosition.IsFinite() && b.LinearVelocity.IsFinite()
                && float.IsFinite(b.GlobalRotation) && float.IsFinite(b.AngularVelocity)) continue;

            Broken = true;
            var idx = new int[_jp.Length];
            for (int j = 0; j < idx.Length; j++) idx[j] = j;
            Array.Sort(idx, (x, y) => Math.Abs(AngVel[y]).CompareTo(Math.Abs(AngVel[x])));

            var sb = new System.Text.StringBuilder();
            sb.Append($"Ragdoll взорвался: первое битое тело '{b.Name}'. Последний целый такт:\n");
            for (int k = 0; k < Math.Min(3, idx.Length); k++)
            {
                int j = idx[k];
                var jd = Def.Joints[j];
                sb.Append($"  {jd.Name,-12} угол {Angle[j],6:F2} (предел {jd.Lower:F1}..{jd.Upper:F1})  " +
                          $"ω {AngVel[j],7:F1}  ток {Current[j],5:F2}  T {Temp[j]:F0}°C  " +
                          $"упор {1f - Dur.StopWear[j]:P0}\n");
            }
            var slots = string.Join(" ", Array.ConvertAll(Brain.Slots, s => s.Chip == null ? "—" : "+"));
            sb.Append($"  слоты [{slots}]  износ {Dur.Mode}");
            GD.PushError(sb.ToString());
            return false;
        }
        return true;
    }

    public void ClampVelocities(float maxAng, float maxLin)
    {
        foreach (var b in Bodies)
        {
            if (Math.Abs(b.AngularVelocity) > maxAng)
            { b.AngularVelocity = Math.Sign(b.AngularVelocity) * maxAng; Clamps++; }
            if (b.LinearVelocity.LengthSquared() > maxLin * maxLin)
            { b.LinearVelocity = b.LinearVelocity.LimitLength(maxLin); Clamps++; }
        }
    }

    float IAbout(int p, Vector2 pivot) => _inertia[p] + _mass[p] * (_pos[p] - pivot).LengthSquared();

    void ReadSensors()
    {
        Vector2 com = Vector2.Zero, vel = Vector2.Zero;
        for (int p = 0; p < Bodies.Length; p++)
        {
            _pos[p] = Bodies[p].GlobalPosition;
            com += _pos[p] * _mass[p];
            vel += Bodies[p].LinearVelocity * _mass[p];
        }
        Com = com / TotalMass;
        ComVel = vel / TotalMass;

        HasSupport = false;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (int f in _feet)
        {
            Grounded[f] = Bodies[f].GetContactCount() > 0;
            if (!Grounded[f]) continue;
            var d = Def.Parts[f];
            float half = (d.Length * 0.5f + d.Radius) * Mathf.Abs(Mathf.Cos(Bodies[f].GlobalRotation));
            lo = Math.Min(lo, _pos[f].X - half);
            hi = Math.Max(hi, _pos[f].X + half);
            HasSupport = true;
        }
        if (HasSupport) { SupportMin = lo; SupportMax = hi; }

        for (int j = 0; j < _jp.Length; j++)
        {
            var a = Bodies[_jp[j]]; var c = Bodies[_jc[j]];
            float raw = c.GlobalRotation - a.GlobalRotation;
            Angle[j] += Mathf.Wrap(raw - Angle[j], -Mathf.Pi, Mathf.Pi);   // непрерывный угол
            AngVel[j] = c.AngularVelocity - a.AngularVelocity;

            Vector2 pivot = c.ToGlobal(_anchorInChild[j]);
            float Ia = IAbout(_jp[j], pivot), Ic = IAbout(_jc[j], pivot);
            JointInertia[j] = Ia * Ic / (Ia + Ic);
        }
    }

    void DriveCoils(float dt)
    {
        float power = 0f, heat = 0f, effort = 0f;
        for (int j = 0; j < _jp.Length; j++)
        {
            float r = Resistance(j);
            float iAvail = SupplyVoltage / r;
            float i = Math.Clamp(Dur.Drive(j, CurrentCmd[j], iAvail), -iAvail, iAvail);
            float tau = i * RatedTorque(j);
            Current[j] = i;
            Torque[j] = tau;

            float q = i * i * r;
            Temp[j] += dt * (HeatGain * q - Dur.Cooling(j) * (Temp[j] - Ambient)) / ThermalTau;
            if (Dur.Accumulate)
            {
                if (Temp[j] > CritTemp) Damage[j] += OverheatDamage * (Temp[j] - CritTemp) * dt;
                Damage[j] += WearRate * Math.Abs(i * AngVel[j]) * dt;
            }

            power += Math.Abs(tau * AngVel[j]);
            heat += q;
            effort += i * i;

            if (tau == 0f || !float.IsFinite(tau)) continue;
            Bodies[_jc[j]].ApplyTorque(tau);
            Bodies[_jp[j]].ApplyTorque(-tau);
        }
        Power = power / Mgh;
        Heat = heat / _jp.Length;
        Effort = effort / _jp.Length;
    }

    // механические упоры: работают без прошивки, изнашиваются, ломаются
    void ApplyStops(float dt)
    {
        for (int j = 0; j < _jp.Length; j++)
        {
            var jd = Def.Joints[j];
            float over = Angle[j] < jd.Lower ? jd.Lower - Angle[j]
                       : Angle[j] > jd.Upper ? jd.Upper - Angle[j] : 0f;
            if (over == 0f || Dur.StopBroken(j)) continue;
            Dur.OnStop(j, over, dt);

            float f  = Dur.StopFactor(j);
            float I  = JointInertia[j];
            float k0 = Math.Min(StopStrength * Mgh, 0.3f * I / (dt * dt));
            float kl = f * k0;
            float kd = f * Math.Min(2f * Mathf.Sqrt(k0 * I), 0.6f * I / dt);
            float tau = kl * over - kd * AngVel[j];
            tau = over > 0f ? Math.Max(tau, 0f) : Math.Min(tau, 0f);
            if (!float.IsFinite(tau)) continue;
            Bodies[_jc[j]].ApplyTorque(tau);
            Bodies[_jp[j]].ApplyTorque(-tau);
        }
    }

    void ApplyGyro(float dt)
    {
        if (!GyroOn || Brain.Slots[(int)LobeKind.Firmware].Chip == null) return;
        float s = 0f;
        foreach (int j in Legs) s += Brain.Stiff[j];
        s /= Math.Max(Legs.Length, 1);
        float kg = GyroStrength * Mgh * s;
        if (kg < 1f) return;

        var t = Bodies[Chest];
        float I = _inertia[Chest] * 2f;
        float kd = 2f * GyroZeta * Mathf.Sqrt(kg * I);
        float tau = Math.Clamp(Spd(-Tilt(Chest), t.AngularVelocity, kg, kd, I, dt), -kg, kg);
        if (!float.IsFinite(tau)) return;
        t.ApplyTorque(tau);
        Power += Math.Abs(tau * t.AngularVelocity) / Mgh;
    }

    void UpdateVisuals()
    {
        var fw = Brain.Slots[(int)LobeKind.Firmware];
        var mo = Brain.Slots[(int)LobeKind.Motor];
        float glow = fw.Chip == null ? 0f
                   : mo.Chip == null ? 0.15f
                   : mo.Boot < 1f ? mo.Boot * (0.4f + 0.6f * (float)_rng.NextDouble())
                   : 1f;
        if (Math.Abs(glow - _lastGlow) > 0.01f)
        {
            Bodies[Head].EyeGlow = glow;
            Bodies[Head].QueueRedraw();
            _lastGlow = glow;
        }

        var headBody = Bodies[Head];
        float sq = Nerves == null ? 0f : Math.Clamp(0.4f * Nerves.Total[1] + Nerves.Total[2], 0f, 1f);
        if (Math.Abs(sq - headBody.Squint) > 0.03f) { headBody.Squint = sq; headBody.QueueRedraw(); }

        if (++_visTick % 12 != 0) return;
        for (int p = 0; p < Bodies.Length; p++)
        {
            int j = _partJoint[p];
            if (j < 0) continue;
            float h = Math.Clamp((Temp[j] - Ambient) / (CritTemp - Ambient), 0f, 1f);
            float w = Math.Clamp(Dur.Effects ? Damage[j] : 0f, 0f, 1f);
            var b = Bodies[p];
            if (Math.Abs(h - b.Heat) > 0.02f || Math.Abs(w - b.Wear) > 0.02f)
            {
                b.Heat = h; b.Wear = w;
                b.QueueRedraw();
            }
        }
    }
}
