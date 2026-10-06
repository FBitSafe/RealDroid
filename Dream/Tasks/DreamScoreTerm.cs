public sealed class DreamScoreTerm
{
    public string Name { get; }
    public string Explanation { get; }
    public float Value { get; set; }

    public DreamScoreTerm(string name, string explanation)
    {
        Name = name;
        Explanation = explanation;
    }
}
