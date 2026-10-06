/// Передача состояния между Main и Dream (статика переживает смену сцены)
public static class DreamJob
{
    public static int Sector = Protocol.Stand;
    public static string ChipPath = ChipFile.MotorPath;
    public static DamageSnapshot Damage;
    public static bool Waking;
}
