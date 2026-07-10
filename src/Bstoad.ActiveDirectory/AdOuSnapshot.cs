namespace Bstoad.ActiveDirectory;

public sealed class AdOuSnapshot
{
    public required string Dn { get; init; }
    public required string Ou { get; init; }
    public string? Description { get; init; }
    public string? OrgId { get; init; }

    public string ParentDn => Dn.IndexOf(',') is var i && i > 0 ? Dn[(i + 1)..] : Dn;
    public string Rdn => Dn.Split(',')[0];
}
