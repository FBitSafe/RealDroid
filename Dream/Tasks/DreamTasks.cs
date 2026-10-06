public static class DreamTasks
{
    public static IDreamTask Make(int sector) => sector switch
    {
        Protocol.Stand => new StandTask(),
        Protocol.Recover => new RecoverTask(),
        _ => null,
    };
}
