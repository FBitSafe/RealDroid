using Godot;
using System;

public partial class PartBody : RigidBody2D
{
    public PartDef Def;
    public bool IsHead;
    public float EyeGlow, Heat, Wear, Squint;

    static readonly Color Hot = new(1f, 0.35f, 0.12f);
    Color _fill, _line;
    Vector2[] _poly, _outline;

    public void Setup(PartDef d)
    {
        Def = d;
        IsHead = d.Name == "head";
        Mass = d.Mass;
        float h = d.Length + 2f * d.Radius, w = 2f * d.Radius;
        Inertia = d.Mass * (h * h + w * w) / 12f;

        var shape = new CapsuleShape2D { Radius = d.Radius, Height = h };
        var cs = new CollisionShape2D { Shape = shape };
        if (d.Horizontal) cs.Rotation = Mathf.Pi / 2f;
        AddChild(cs);

        (_fill, ZIndex) = d.Side switch
        {
            LimbSide.Far  => (new Color(0.58f, 0.61f, 0.68f), 0),
            LimbSide.Near => (new Color(0.93f, 0.94f, 0.97f), 2),
            _             => (new Color(0.84f, 0.86f, 0.91f), 1),
        };
        _line = new Color(0.12f, 0.13f, 0.17f);

        _poly = Capsule(d.Length * 0.5f, d.Radius, d.Horizontal, 8);
        _outline = new Vector2[_poly.Length + 1];
        _poly.CopyTo(_outline, 0);
        _outline[^1] = _poly[0];
    }

    static Vector2[] Capsule(float a, float r, bool horiz, int seg)
    {
        var pts = new Vector2[(seg + 1) * 2];
        int k = 0;
        for (int i = 0; i <= seg; i++)
        {
            float t = Mathf.Pi + Mathf.Pi * i / seg;
            pts[k++] = new Vector2(r * Mathf.Cos(t), -a + r * Mathf.Sin(t));
        }
        for (int i = 0; i <= seg; i++)
        {
            float t = Mathf.Pi * i / seg;
            pts[k++] = new Vector2(r * Mathf.Cos(t), a + r * Mathf.Sin(t));
        }
        if (horiz)
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(-pts[i].Y, pts[i].X);
        return pts;
    }

    public override void _Draw()
    {
        var fill = _fill.Lerp(Hot, Heat * 0.8f).Darkened(Wear * 0.6f);
        DrawColoredPolygon(_poly, fill);
        DrawPolyline(_outline, _line, 1.2f, true);

        if (IsHead)
        {
            DrawRect(new Rect2(1f, -5f, 9f, 8f), new Color(0.05f, 0.06f, 0.09f));
            var off = new Color(0.12f, 0.15f, 0.2f);
            var on = new Color(0.3f, 0.95f, 1f);
            var eye = off.Lerp(on, Math.Clamp(EyeGlow, 0f, 1f));
            float eh = Mathf.Lerp(2.6f, 0.5f, Math.Clamp(Squint, 0f, 1f));   // жмурится от боли
            DrawRect(new Rect2(2.9f, -1f - eh * 0.5f, 2.6f, eh), eye);
            DrawRect(new Rect2(6.5f, -1f - eh * 0.5f, 2.6f, eh), eye);
        }
    }
}