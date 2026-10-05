using Godot;
using System;
using System.Collections.Generic;

public partial class DamageFx : Node2D
{
    public Ragdoll Girl;

    sealed class Drop { public Vector2 P, V; public float Life; }
    readonly List<Drop> _drops = new();
    readonly Random _rng = new(5);
    static readonly Color Coolant = new(0.3f, 0.75f, 1f, 0.8f), Spark = new(1f, 0.9f, 0.4f);

    public override void _Ready() { TopLevel = true; ZIndex = 40; }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var d = Girl?.Dur;
        if (d == null || Girl.Bodies == null || Girl.Broken) return;

        if (d.Effects && d.Coolant > 0f)
            for (int j = 0; j < d.HoseTorn.Length; j++)
                if (d.HoseTorn[j] && _rng.NextDouble() < 25 * dt * d.Coolant)
                    _drops.Add(new Drop
                    {
                        P = Girl.JointPos(j),
                        V = new Vector2((float)_rng.NextDouble() * 60 - 30, -(float)_rng.NextDouble() * 40),
                        Life = 3f,
                    });

        for (int i = _drops.Count - 1; i >= 0; i--)
        {
            var p = _drops[i];
            p.Life -= dt;
            if (p.P.Y < 0f) { p.V.Y += Ragdoll.Gravity * dt; p.P += p.V * dt; }
            else { p.P.Y = 0f; p.V = Vector2.Zero; }             // лужица
            if (p.Life <= 0f) _drops.RemoveAt(i);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var d = Girl?.Dur;
        if (d == null || Girl.Bodies == null || Girl.Broken) return;

        foreach (var p in _drops)
            DrawCircle(p.P, p.P.Y >= 0f ? 2f : 1f, new Color(Coolant, Math.Min(1f, p.Life)));

        if (!d.Effects) return;
        for (int j = 0; j < d.ShortTime.Length; j++)
        {
            if (d.ShortTime[j] <= 0f) continue;
            var c = Girl.JointPos(j);
            for (int k = 0; k < 4; k++)
            {
                float a = (float)(_rng.NextDouble() * Math.Tau);
                float r = 3f + (float)_rng.NextDouble() * 6f;
                DrawLine(c, c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r, Spark, 1f);
            }
        }
    }
}
