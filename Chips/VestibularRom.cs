using Godot;
using System;

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
