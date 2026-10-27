namespace NxInfinity;

public sealed record TranslationLanguage(string Id, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}
