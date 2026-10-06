using Godot;

public interface IChip
{
    LobeKind Kind { get; }
    string Label { get; }
    void Reset(Brain b);
    void Tick(Brain b, float dt);
}
