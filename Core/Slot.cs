public sealed class Slot
{
    public LobeKind Kind;
    public string Title;
    public float Hz;
    public int Divider = 1;
    public IChip Chip;
    public float Boot;
    public bool HoldOnEmpty;     // true: при извлечении выход «застывает», false: обнуляется
    public string[] Outputs;
}
