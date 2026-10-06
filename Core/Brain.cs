using System;

public sealed class Brain
{
    public const float BootTime = 0.6f;
    public static bool GetupEnabled = false;
    public static float LimpStiff = 0.05f;
    static readonly LobeKind[] Order =
        { LobeKind.Vestibular, LobeKind.Arbiter, LobeKind.Motor, LobeKind.Reflex };

    public readonly Ragdoll Body;
    public readonly Bus Bus = new();
    public readonly Slot[] Slots = new Slot[5];

    public readonly float[] Angle, Vel, Balance, P, Cmd, Threat,
                            MotorTarget, MotorStiff, ReflexTarget, ReflexStiff, ReflexInhibit,
                            Target, Stiff;
    public readonly bool[] SwingingJoints;

    public Brain(Ragdoll body, int nj, int tps)
    {
        Body = body;
        Angle         = Bus.Add("proprio.angle", nj);
        Vel           = Bus.Add("proprio.vel", nj);
        Balance       = Bus.Add("vest.balance", Bal.Size);
        P             = Bus.Add("arb.p", Protocol.Count);
        Cmd           = Bus.Add("cmd", 4);
        Threat        = Bus.Add("threat", 1);
        MotorTarget   = Bus.Add("motor.target", nj);
        MotorStiff    = Bus.Add("motor.stiff", nj);
        ReflexTarget  = Bus.Add("reflex.dtarget", nj);
        ReflexStiff   = Bus.Add("reflex.stiff", nj);
        ReflexInhibit = Bus.Add("reflex.inhibit", 1);
        Target        = Bus.Add("final.target", nj);
        Stiff         = Bus.Add("final.stiff", nj);
        SwingingJoints = new bool[nj];
        P[Protocol.Stand] = 1f;

        Make(LobeKind.Firmware,   "ПРОШИВКА",      tps, tps, false);
        Make(LobeKind.Vestibular, "ВЕСТИБУЛЯР",    120, tps, false, "vest.balance");
        Make(LobeKind.Reflex,     "РЕФЛЕКСЫ",      120, tps, false, "reflex.dtarget", "reflex.stiff", "reflex.inhibit");
        Make(LobeKind.Arbiter,    "АРБИТР",         10, tps, true,  "arb.p");
        Make(LobeKind.Motor,      "МОТОРНАЯ КОРА",  30, tps, false, "motor.target", "motor.stiff");
    }

    void Make(LobeKind k, string title, float hz, int tps, bool hold, params string[] outs)
        => Slots[(int)k] = new Slot
        {
            Kind = k, Title = title, Hz = hz, HoldOnEmpty = hold, Outputs = outs,
            Divider = Math.Max(1, (int)MathF.Round(tps / hz)),
        };

    public bool Insert(IChip c, bool instant = false)
    {
        var s = Slots[(int)c.Kind];
        if (s.Chip != null) return false;
        s.Chip = c;
        s.Boot = instant ? 1f : 0f;
        c.Reset(this);
        return true;
    }

    public IChip Eject(LobeKind k)
    {
        var s = Slots[(int)k];
        var c = s.Chip;
        if (c == null) return null;
        s.Chip = null;
        s.Boot = 0f;
        if (!s.HoldOnEmpty) foreach (var n in s.Outputs) Array.Clear(Bus.Channels[n]);
        if (k == LobeKind.Firmware) Body.CutPower();
        return c;
    }

    public void Tick(int tick, float dt)
    {
        foreach (var k in Order) Run(Slots[(int)k], tick, dt);
        Compose();
        Run(Slots[(int)LobeKind.Firmware], tick, dt);
    }

    void Run(Slot s, int tick, float dt)
    {
        if (s.Chip == null) return;
        s.Boot = Math.Min(1f, s.Boot + dt / BootTime);
        if (tick % s.Divider == 0) s.Chip.Tick(this, dt * s.Divider);
    }

    // target = мотор + Δрефлекс;  stiff = max(мотор·(1−подавление), рефлекс)
    void Compose()
    {
        float bm = Slots[(int)LobeKind.Motor].Boot;
        float br = Slots[(int)LobeKind.Reflex].Boot;
        float inh = Math.Clamp(ReflexInhibit[0], 0f, 1f);
        float limp = GetupEnabled ? 0f : Math.Clamp(P[Protocol.Getup], 0f, 1f);
        for (int j = 0; j < Target.Length; j++)
        {
            float mt = MotorTarget[j], ms = MotorStiff[j];
            if (limp > 0f) { mt += (Angle[j] - mt) * limp; ms += (LimpStiff - ms) * limp; }
            Target[j] = mt + ReflexTarget[j] * br;
            Stiff[j]  = Math.Max(ms * bm * (1f - inh), ReflexStiff[j] * br);
        }
    }
}
