public interface IDreamTask
{
    int Sector { get; }
    float Seconds { get; }
    int ExamCount { get; }
    float FallPenalty { get; }
    IChip MakeArbiter();
    IEpisode Begin(Ragdoll g, int seed, int examIndex);   // examIndex < 0 — популяция
}
