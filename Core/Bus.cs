using System.Collections.Generic;

public sealed class Bus
{
    public readonly Dictionary<string, float[]> Channels = new();
    public float[] Add(string name, int size) { var a = new float[size]; Channels[name] = a; return a; }
}
