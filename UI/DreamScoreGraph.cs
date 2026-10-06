using Godot;
using System.Collections.Generic;

public partial class DreamScoreGraph : Control
{
    const int PanelWidth = 340;
    const int PanelHeight = 360;
    IReadOnlyList<DreamScoreTerm> _terms = System.Array.Empty<DreamScoreTerm>();

    public bool IsPenaltyGraph { get; set; }

    public DreamScoreGraph()
    {
        CustomMinimumSize = new Vector2(PanelWidth, PanelHeight);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void SetTerms(IReadOnlyList<DreamScoreTerm> terms)
    {
        _terms = terms ?? System.Array.Empty<DreamScoreTerm>();
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        int fontSize = GetThemeDefaultFontSize();
        Color accent = IsPenaltyGraph ? new Color(1f, 0.28f, 0.24f, 0.95f) : new Color(0.28f, 1f, 0.48f, 0.95f);
        Color bodyText = new(0.9f, 0.92f, 0.96f, 0.95f);
        Color mutedText = new(0.72f, 0.76f, 0.82f, 0.9f);

        DrawRect(new Rect2(Vector2.Zero, new Vector2(PanelWidth, PanelHeight)), new Color(0.025f, 0.035f, 0.055f, 0.72f));
        DrawRect(new Rect2(0, 0, PanelWidth, PanelHeight), new Color(accent.R, accent.G, accent.B, 0.45f), false, 1f);
        DrawString(font, new Vector2(10, 22), IsPenaltyGraph ? "PENALTIES · per physics step" : "REWARDS · per physics step",
            HorizontalAlignment.Left, -1, fontSize, accent);

        float maxValue = 0f;
        foreach (var term in _terms)
            if (float.IsFinite(term.Value)) maxValue = Mathf.Max(maxValue, Mathf.Abs(term.Value));

        const float chartTop = 33f;
        const float rowHeight = 22f;
        float barX = 112f;
        float barWidth = 167f;
        for (int i = 0; i < _terms.Count; i++)
        {
            var term = _terms[i];
            float y = chartTop + i * rowHeight;
            DrawString(font, new Vector2(9, y + 15), term.Name, HorizontalAlignment.Left, 99, 12, bodyText);
            DrawRect(new Rect2(barX, y + 5, barWidth, 11), new Color(0.6f, 0.65f, 0.72f, 0.2f));
            if (maxValue > 0f && float.IsFinite(term.Value))
            {
                float width = barWidth * Mathf.Clamp(Mathf.Abs(term.Value) / maxValue, 0f, 1f);
                DrawRect(new Rect2(barX, y + 5, width, 11), new Color(accent.R, accent.G, accent.B, 0.7f));
            }
            string value = float.IsFinite(term.Value) ? term.Value.ToString("0.000") : "n/a";
            DrawString(font, new Vector2(284, y + 15), value, HorizontalAlignment.Right, 46, 12, bodyText);
        }

        float explanationTop = chartTop + _terms.Count * rowHeight + 5f;
        DrawLine(new Vector2(9, explanationTop), new Vector2(PanelWidth - 9, explanationTop), new Color(1f, 1f, 1f, 0.2f));
        for (int i = 0; i < _terms.Count; i++)
        {
            float y = explanationTop + 16f + i * 17f;
            DrawString(font, new Vector2(10, y), _terms[i].Explanation, HorizontalAlignment.Left, PanelWidth - 20, 11, mutedText);
        }
    }
}
