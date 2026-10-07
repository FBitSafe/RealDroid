using Godot;
using System;

public partial class BrainPanel : Control
{
    public Ragdoll Girl;
        public LobeKind Selected = LobeKind.Motor;
        public string Message = "";
        public float MessageTime;
        public float PushLevel = 0.45f;

    public static readonly LobeKind[] Order =
        { LobeKind.Firmware, LobeKind.Vestibular, LobeKind.Reflex, LobeKind.Arbiter, LobeKind.Motor };

    static readonly Color Dim = new(0.6f, 0.62f, 0.7f), Off = new(0.35f, 0.36f, 0.42f),
                          Booting = new(0.95f, 0.8f, 0.25f), On = new(0.35f, 0.95f, 0.55f),
                          Cold = new(0.35f, 0.6f, 1f), Warm = new(1f, 0.6f, 0.15f), Burn = new(1f, 0.2f, 0.15f),
                          PainC = new(1f, 0.3f, 0.5f), Discomf = new(0.85f, 0.75f, 0.45f);

    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; Position = new Vector2(10, 10); }

    public override void _Process(double d)
    {
        if (MessageTime > 0) MessageTime -= (float)d;
        QueueRedraw();
    }

    void Text(float x, float y, string s, Color c, int size = 12)
        => DrawString(ThemeDB.FallbackFont, new Vector2(x, y), s, HorizontalAlignment.Left, -1, size, c);

    void Bar(float x, float y, float w, float v, Color c)
    {
        DrawRect(new Rect2(x, y - 8, w, 7), new Color(1, 1, 1, 0.08f));
        DrawRect(new Rect2(x, y - 8, w * Math.Clamp(v, 0f, 1f), 7), c);
    }

    static Color Health(float h) => h > 0.6f ? Dim : h > 0f ? Warm : Burn;

















    static string ModeName(DamageMode m) => m switch
        {
            DamageMode.Off => "ВЫКЛ (идеальное тело)",
            DamageMode.Frozen => "ЗАМОРОЖЕН",
            _ => "ВКЛ",
        };

        bool SteppingOn()
        {
            if (Girl == null || !IsInstanceValid(Girl) || Girl.Brain == null) return false;
            return Girl.Brain.Slots[(int)LobeKind.Motor].Chip switch
            {
                MotorRom rom => rom.CapturePointStepping,
                NeuralMotorChip nn => nn.BaseStepping,
                _ => false,
            };
        }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, 470, 700), new Color(0.02f, 0.03f, 0.05f, 0.7f));
        float y = 20;
        Text(10, y, "МОЗГ", Colors.White, 14);
        if (Girl == null || !IsInstanceValid(Girl) || Girl.Brain == null)
        {
            Text(10, y + 24, "тела нет · [R] заново", Burn);
            return;
        }
        var b = Girl.Brain;
        var d = Girl.Dur;
        var n = Girl.Nerves;

        y += 6;
        for (int i = 0; i < Order.Length; i++)
        {
            var s = b.Slots[(int)Order[i]];
            y += 24;
            var col = s.Chip == null ? Off : s.Boot < 1f ? Booting : On;
            var r = new Rect2(8, y - 16, 454, 22);
            DrawRect(r, new Color(col, 0.15f));
            if (Order[i] == Selected) DrawRect(r, Colors.White, false, 1.5f);
            DrawCircle(new Vector2(19, y - 5), 4f, col);
            Text(30, y, $"{i + 1} {s.Title}", Colors.White);
            string label = s.Chip == null ? "— пусто —"
                         : s.Boot < 1f ? $"{s.Chip.Label} {s.Boot:P0}" : s.Chip.Label;
            Text(165, y, label, col);
            Text(430, y, $"{s.Hz:0}", Dim, 10);
        }

        y += 30;
        string st = ArbiterInfo.State(b);
        Text(10, y, $"Протокол: {st}", Colors.White);
        for (int k = 0; k < 4; k++)
        {
            y += 16;
            Text(18, y, Protocol.Names[k], Dim, 11);
            Bar(100, y, 200, b.P[k], On);
            if (b.Slots[(int)LobeKind.Motor].Chip is NeuralMotorChip nm)
                Text(310, y, $"зрел. {nm.Maturity[k]:P0}", Dim, 10);
        }

        y += 24;
        var bl = b.Balance;
        string pose = bl[Bal.Lying] > 0.5f ? "ЛЕЖИТ" : bl[Bal.Support] > 0.5f ? "на ногах" : "нет опоры";
        Text(10, y, $"Запас ξ: {bl[Bal.Margin] * Ragdoll.RefHeight:+0;-0} px   {pose}", Colors.White);

        y += 20;
        float[] pt = n?.Total;
        if (pt != null)
        {
            Text(10, y, "Дискомфорт", Dim, 11);  Bar(85, y, 60, pt[0], Discomf);
            Text(155, y, "Боль", Dim, 11);       Bar(190, y, 60, pt[1], PainC);
            Text(260, y, "Острая", Dim, 11);     Bar(310, y, 60, pt[2], Burn);
        }

        y += 20;
        Text(10, y, $"Износ: {ModeName(d.Mode)}", d.Mode == DamageMode.Live ? Warm : Dim);
        Text(230, y, "хладагент", Dim, 11);
        Bar(295, y, 120, d.Coolant, d.Coolant > 0.3f ? Cold : Burn);

        y += 24;
        Text(10, y, "Сустав", Dim, 11); Text(145, y, "°C", Dim, 11); Text(175, y, "R", Dim, 11);
        Text(210, y, "ток", Dim, 11);   Text(262, y, "упор", Dim, 11); Text(302, y, "изол", Dim, 11);
        Text(342, y, "боль", Dim, 11);
        for (int j = 0; j < Girl.Temp.Length; j++)
        {
            y += 15;
            float T = Girl.Temp[j];
            float t = (T - Girl.Ambient) / (Girl.CritTemp - Girl.Ambient);
            var c = t < 1f ? Cold.Lerp(Warm, Math.Clamp(t, 0f, 1f)) : Burn;
            float stop = 1f - d.StopWear[j], ins = d.Insulation[j];

            Text(10, y, Girl.Def.Joints[j].Name, Dim, 11);
            Bar(85, y, 55, t, c);
            Text(145, y, $"{T:0}", c, 11);
            Text(175, y, $"{Girl.Resistance(j):0.00}", Girl.Damage[j] > 0.05f ? Burn : Dim, 11);
            Bar(210, y, 45, Math.Abs(Girl.Current[j]) / Girl.SupplyVoltage, Warm);
            Text(262, y, stop <= 0f ? "СЛОМ" : $"{stop:P0}", Health(stop), 11);
            Text(302, y, $"{ins:P0}", Health(ins - 0.4f), 11);

            if (d.Effects && d.CableCut[j]) Text(342, y, "онем.", Off, 11);
            else if (n != null)
            {
                var pc = n.Acute[j] > 0.05f ? Burn : n.Ache[j] > 0.05f ? PainC : Discomf;
                Bar(342, y, 35, n.Level(j) / 3f, pc);
            }

            string flags = (d.ShortTime[j] > 0f ? "КЗ " : "") + (d.HoseTorn[j] ? "Т " : "") + (d.CableCut[j] ? "X " : "");
            if (Math.Abs(d.Twist[j]) > 0.05f) flags += $"{d.Twist[j]:+0.0;-0.0}об";
            if (flags.Length > 0) Text(382, y, flags, Burn, 11);
        }

        y += 22;
                if (MessageTime > 0) Text(10, y, "» " + Message, Booting);
                y += 20;
                int pushPct = Mathf.RoundToInt(Math.Clamp(PushLevel, 0.05f, 1f) * 100f);
                Text(10, y, $"толчок {pushPct}% [ ]  Q=грудь E=спина  шаг: {(SteppingOn() ? "вкл" : "выкл")}", Dim, 11);
                y += 16; Text(10, y, "[1-5] слот  [Space] вынуть/вставить  [Tab] другой чип", Dim, 11);
                y += 16; Text(10, y, "[ЛКМ] тащить  [Q/E] толкнуть  [G] гироскоп  [D] схема", Dim, 11);
                y += 16; Text(10, y, "[K] режим износа  [J] состарить упоры  [H] полный ремонт  [R] заново", Dim, 11);
                y += 16; Text(10, y, "[T] сон STAND   [Y] сон RECOVER (нужен STAND ≥ 50%)   во сне [Esc]", Dim, 11);
            }
        }
