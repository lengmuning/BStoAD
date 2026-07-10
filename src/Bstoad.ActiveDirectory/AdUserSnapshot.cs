namespace Bstoad.ActiveDirectory;

public sealed class AdUserSnapshot
{
    public required string Dn { get; init; }
    public required string SamAccountName { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? DisplayName { get; init; }
    public string? Mail { get; init; }
    public string? TelephoneNumber { get; init; }
    public string? Mobile { get; init; }
    public string? Department { get; init; }
    public string? Title { get; init; }
    public string? EmployeeId { get; init; }
    public string? AdminDescription { get; init; }
    public int UserAccountControl { get; init; }
    public int? AdminCount { get; init; }

    public string ParentOu => Dn.IndexOf(',') is var i && i > 0 ? Dn[(i + 1)..] : Dn;
    public bool IsDisabled => (UserAccountControl & AdUserAttributes.UF_ACCOUNTDISABLE) != 0;
    public bool HasManagedMarker(string markerValue) =>
        string.Equals(AdminDescription, markerValue, StringComparison.Ordinal);
}
