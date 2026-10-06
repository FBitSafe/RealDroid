using Godot;
using System;

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
