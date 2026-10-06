using Godot;

public partial class DebugOverlay : Node2D
{
    public Ragdoll Girl;

    public override void _Ready() { TopLevel = true; ZIndex = 50; }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.D }) Visible = !Visible;
    }

    public override void _Process(double delta) { if (Visible) QueueRedraw(); }

    public override void _Draw()
    {
        if (Girl?.Bodies == null || Girl.Brain == null) return;
        var yellow = new Color(1f, 0.85f, 0.2f);
        var magenta = new Color(1f, 0.3f, 0.9f);
        var cyan = new Color(0.3f, 0.95f, 1f);

        var com = Girl.Com;
        DrawCircle(com, 3f, yellow);
        DrawDashedLine(com, new Vector2(com.X, 0), yellow, 0.6f, 3f);

        // настоящий capture point
        float w0 = Mathf.Sqrt(Ragdoll.Gravity / Mathf.Max(-com.Y, 20f));
        float xi = com.X + Girl.ComVel.X / w0;
        DrawLine(new Vector2(xi, -6), new Vector2(xi, 6), magenta, 1.5f);

        if (Girl.HasSupport)
        {
            var green = new Color(0.3f, 1f, 0.4f);
            DrawLine(new Vector2(Girl.SupportMin, 1.5f), new Vector2(Girl.SupportMax, 1.5f), green, 2f);

            // как его видит вестибулярная доля
            var bl = Girl.Brain.Balance;
            if (bl[Bal.Support] > 0.5f)
            {
                float c = 0.5f * (Girl.SupportMin + Girl.SupportMax);
                float bx = c + bl[Bal.Xi] * Ragdoll.RefHeight;
                DrawLine(new Vector2(bx, -12), new Vector2(bx, -4), cyan, 1.5f);
            }
        }

        string st = ArbiterInfo.State(Girl.Brain);
        DrawString(ThemeDB.FallbackFont, Girl.Bodies[Girl.Head].GlobalPosition + new Vector2(-14, -18),
                   st, HorizontalAlignment.Left, -1, 8, yellow);
    }
}