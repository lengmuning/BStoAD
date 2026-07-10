namespace Bstoad.ActiveDirectory;

public sealed class AdUserAttributes
{
    public required string SamAccountName { get; init; }
    public required string UserPrincipalName { get; init; }
    public required string DisplayName { get; init; }
    public string? GivenName { get; init; }
    public string? Surname { get; init; }
    public string? Mail { get; init; }
    public string? TelephoneNumber { get; init; }
    public string? Mobile { get; init; }
    public string? Department { get; init; }
    public string? Title { get; init; }
    public string? EmployeeId { get; init; }

    public const int UF_NORMAL_ACCOUNT = 0x0200;
    public const int UF_ACCOUNTDISABLE = 0x0002;
    public const int UF_DONT_EXPIRE_PASSWD = 0x10000;
}
