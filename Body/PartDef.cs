using Godot;

public sealed class PartDef
{
    public string Name;
    public Vector2 Center;   // px, (0,0) = пол под голеностопом, вверх = −y
    public float Length;
    public float Radius;
    public float Mass;
    public LimbSide Side;
    public bool Horizontal;
    public bool IsFoot;
}
