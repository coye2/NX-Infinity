namespace NxInfinity;

public sealed record AvatarCategory(string Id, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}
