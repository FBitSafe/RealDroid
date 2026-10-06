using Godot;
using System.Collections.Generic;

public sealed class RagdollDef
{
    public readonly List<PartDef> Parts = new();
    public readonly List<JointDef> Joints = new();

    public int PartIndex(string n) => Parts.FindIndex(p => p.Name == n);
    public int JointIndex(string n) => Joints.FindIndex(j => j.Name == n);

    void P(string n, Vector2 c, float len, float r, float m,
           LimbSide s = LimbSide.Center, bool horiz = false, bool foot = false)
        => Parts.Add(new PartDef { Name = n, Center = c, Length = len, Radius = r, Mass = m,
                                   Side = s, Horizontal = horiz, IsFoot = foot });

    void J(string n, string a, string b, Vector2 anchor, float lo, float hi, float str)
        => Joints.Add(new JointDef { Name = n, Parent = a, Child = b, Anchor = anchor,
                                     Lower = lo, Upper = hi, Strength = str });

    public static RagdollDef Girl()
    {
        var d = new RagdollDef();
        // ── позвоночник ──
        d.P("pelvis", new(0, -92),   6, 10f,  7f);
        d.P("waist",  new(0, -112),  8, 7.5f, 4f);
        d.P("chest",  new(0, -131), 14, 10f,  8f);
        d.P("head",   new(0, -153),  4, 10f,  4f);

        d.J("lumbar",   "pelvis", "waist", new(0, -103), -0.4f, 0.7f, 0.60f);
        d.J("thoracic", "waist",  "chest", new(0, -121), -0.3f, 0.5f, 0.50f);
        d.J("neck",     "chest",  "head",  new(0, -143), -0.5f, 0.5f, 0.15f);

        foreach (var (s, side) in new[] { ("f", LimbSide.Far), ("n", LimbSide.Near) })
        {
            d.P($"thigh_{s}", new(0, -69),  42, 6f,   6.0f, side);
            d.P($"shin_{s}",  new(0, -28),  40, 5f,   3.0f, side);
            d.P($"foot_{s}",  new(5, -4),   18, 4f,   1.2f, side, horiz: true, foot: true);
            d.P($"uarm_{s}",  new(0, -121), 26, 4f,   1.6f, side);
            d.P($"farm_{s}",  new(0, -95),  26, 3.5f, 1.2f, side);

            d.J($"hip_{s}",      "pelvis",     $"thigh_{s}", new(0, -90),  -2.0f, 0.6f, 1.00f);
            d.J($"knee_{s}",     $"thigh_{s}", $"shin_{s}",  new(0, -48),   0.0f, 2.4f, 1.00f);
            d.J($"ankle_{s}",    $"shin_{s}",  $"foot_{s}",  new(0, -8),   -0.5f, 0.8f, 1.20f);
            d.J($"shoulder_{s}", "chest",      $"uarm_{s}",  new(0, -134), -3.0f, 1.0f, 0.08f);
            d.J($"elbow_{s}",    $"uarm_{s}",  $"farm_{s}",  new(0, -108), -2.5f, 0.0f, 0.04f);
        }
        return d;
    }
}
