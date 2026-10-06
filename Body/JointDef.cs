using Godot;

public sealed class JointDef
{
    public string Name, Parent, Child;
    public Vector2 Anchor;
    public float Lower, Upper;  // пределы угла (child − parent), рад. + = наклон вперёд / сгиб
    public float Strength;      // в долях M·g·H
}
