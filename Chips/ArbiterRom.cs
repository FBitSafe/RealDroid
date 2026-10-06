using Godot;
using System;

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
