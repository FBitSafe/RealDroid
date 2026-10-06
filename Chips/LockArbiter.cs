using Godot;

/// Арбитр, жёстко держащий один сектор (для сна и отладки)
public sealed class LockArbiter : IChip
{
    public readonly int Sector;
    public LockArbiter(int sector) { Sector = sector; }
    public LobeKind Kind => LobeKind.Arbiter;
    public string Label => $"ARB LOCK {Protocol.Names[Sector]}";
    public void Reset(Brain b) => Set(b);
    public void Tick(Brain b, float dt) => Set(b);
    void Set(Brain b) { for (int k = 0; k < Protocol.Count; k++) b.P[k] = k == Sector ? 1f : 0f; }
}
