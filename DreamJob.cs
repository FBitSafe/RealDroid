using System;

/// Передача состояния между Main и Dream (статика переживает смену сцены)
public static class DreamJob
{
    public static int Sector = Protocol.Stand;
    public static string ChipPath = ChipFile.MotorPath;
    public static DamageSnapshot Damage;
    public static bool Waking;
}

/// Слепок травм тела: во сне клоны учатся с ними (режим Frozen)
public sealed class DamageSnapshot
{
    public float[] StopWear, Insulation, Coil;
    public bool[] HoseTorn, CableCut;
    public float Coolant;
    public bool Gyro;

    public static DamageSnapshot Capture(Ragdoll g) => new()
    {
        StopWear   = (float[])g.Dur.StopWear.Clone(),
        Insulation = (float[])g.Dur.Insulation.Clone(),
        Coil       = (float[])g.Damage.Clone(),
        HoseTorn   = (bool[])g.Dur.HoseTorn.Clone(),
        CableCut   = (bool[])g.Dur.CableCut.Clone(),
        Coolant    = g.Dur.Coolant,
        Gyro       = g.GyroOn,
    };

    public void Apply(Ragdoll g)
    {
        if (g?.Dur == null || StopWear.Length != g.Dur.StopWear.Length) return;
        Array.Copy(StopWear, g.Dur.StopWear, StopWear.Length);
        Array.Copy(Insulation, g.Dur.Insulation, Insulation.Length);
        Array.Copy(Coil, g.Damage, Coil.Length);
        Array.Copy(HoseTorn, g.Dur.HoseTorn, HoseTorn.Length);
        Array.Copy(CableCut, g.Dur.CableCut, CableCut.Length);
        g.Dur.Coolant = Coolant;
        g.GyroOn = Gyro;
    }
}
