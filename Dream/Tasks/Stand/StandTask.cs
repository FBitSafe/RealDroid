using Godot;
using System;
using System.Collections.Generic;

// ───────── STAND: стоять в позе экономно, только ветер, без толчков ─────────
public sealed class StandTask : IDreamTask
{
    public float EpisodeSeconds = 6f;
    public float WindLevel = 0.03f, WindTau = 0.5f;          // доля веса тела
    public float Alive = 0.2f, Upright = 0.2f, PoseWeight = 1f, PoseSigma = 0.2f;
    public float EnergyCost = 0.5f, EffortCost = 1.0f, HeatCost = 0.3f, SlideCost = 0.3f;
    public float DiscomfortCost = 0.1f, PainCost = 0.5f, AcuteCost = 1.5f;
    public float Fall = 0.5f;

    public int Sector => Protocol.Stand;
    public float Seconds => EpisodeSeconds;
    public int ExamCount => 2;                                // 0 — ветер, 1 — штиль
    public float FallPenalty => Fall;
    public IChip MakeArbiter() => new LockArbiter(Protocol.Stand);
    public IEpisode Begin(Ragdoll g, int seed, int examIndex) => new StandEpisode(this, g, seed, examIndex);
}
