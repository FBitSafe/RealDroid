using System.Collections.Generic;

public interface IEpisode
{
    float Step(float dt, int tick, out bool failed);
    float Quality { get; }          // 0..1, для зрелости сектора
    IReadOnlyList<DreamScoreTerm> Rewards { get; }
    IReadOnlyList<DreamScoreTerm> Penalties { get; }
    void MarkFailed();
}
