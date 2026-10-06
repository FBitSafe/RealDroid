using Godot;
using System;


// ───────── RECOVER: после толчка вернуть ξ в опору и вернуться в STAND ─────────
public sealed class RecoverTask : IDreamTask, ICurriculum
{
    public float EpisodeSeconds = 7.5f, SecondPushDelay = 3f;
    public float PushMax = 3000f;                       // импульс в грудь при уровне 100%
    public float LevelFwd = 0f, LevelBack = 0f, LevelStep = 0.05f;   // Fwd: в спину (+x), Back: в грудь (−x)
    public float MinExamLevel;
    public float ImpactCost = 2f, ImpactFree = 60f;     // удар стопы, px/s
    public float Alive = 3f, Upright = 0.2f, PoseWeight = 0.4f, PoseSigma = 0.25f;
    public float MarginCost = 0.5f, ReturnBonus = 3f;
    public float FootMarginCost = 3f, FootPad = 10f;
    public float FrontierShare = 0.6f;
    public float PoseDelay = 1.5f, PoseRamp = 1f;          // поза после толчка: включается плавно, без привязки к протоколу
    public float CheckWindow = 0.75f;                      // окно итога перед 2-м толчком и в конце эпизода
    public float SettleSpread = 20f, SettleV = 15f;        // px, px/s
    public float ForceShare = 0.8f;                        // доля тренировок с принудительным RECOVER при толчке
    public float StanceCost = 1.5f, CalmVel = 50f;         // штраф за широкую стойку, когда уже спокойно (px/s)
    public float EnergyCost = 0.3f, EffortCost = 0.5f, HeatCost = 0.3f;
    public float PainCost = 0.4f, AcuteCost = 1f;
    public float Fall = 3f;

    public int Sector => Protocol.Recover;
    public float Seconds => EpisodeSeconds;
    public int ExamCount => 4;                          // 0,2 — в спину; 1,3 — в грудь
    public float FallPenalty => Fall;
    public IChip MakeArbiter() => new ArbiterRom();
    public IEpisode Begin(Ragdoll g, int seed, int examIndex) => new RecoverEpisode(this, g, seed, examIndex);

    public float LevelOf(int dir) => dir == 0 ? LevelFwd : LevelBack;
    public string Status => $"спина {LevelFwd:P0} / грудь {LevelBack:P0} (мин. {MinExamLevel:P0})";
    public float Bonus(int ok, int total) => 0.5f * (LevelFwd + LevelBack) * ok / Math.Max(total, 1);

    readonly int[] _examN = new int[2], _examFail = new int[2];
    internal void ExamBegun(int dir) => _examN[dir]++;
    internal void ExamFailed(int dir) => _examFail[dir]++;

    public void Update(int ok, int total)
    {
        for (int d = 0; d < 2; d++)
        {
            if (_examN[d] == 0) continue;
            int pass = _examN[d] - _examFail[d];
            float lv = LevelOf(d);
            if (pass == _examN[d]) lv = Math.Min(1f, lv + LevelStep);
            else if (pass == 0)    lv = Math.Max(MinExamLevel, lv - LevelStep);
            if (d == 0) LevelFwd = lv; else LevelBack = lv;
        }
        Array.Clear(_examN); Array.Clear(_examFail);
    }
}
