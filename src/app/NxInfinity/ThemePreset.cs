namespace NxInfinity;

public sealed record ThemePreset(string Id, string Name, bool Night, string Canvas, string Surface, string Ink, string Muted, string Line, string Hover, string Accent)
{
    public override string ToString()
    {
        return Name;
    }
}
