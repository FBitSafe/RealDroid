public static class ArbiterInfo
{
    public static string State(Brain b) => b.Slots[(int)LobeKind.Arbiter].Chip switch
    {
        ArbiterRom a => a.StateName,
        LockArbiter l => "LOCK " + Protocol.Names[l.Sector],
        _ => "заклинило",
    };
}
