namespace Bstoad.Core.Models;

public sealed class BeisenEmployee
{
    public string UserId { get; set; } = "";
    public string EmployeeId { get; set; } = "";

    public string Name { get; set; } = "";
    public string? GivenName { get; set; }
    public string? Surname { get; set; }

    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string? Phone { get; set; }

    public string? OrgId { get; set; }
    public string? OrgName { get; set; }
    public string? Title { get; set; }

    public int EmpStatus { get; set; }
    public bool IsLeaver { get; set; }

    public DateTime? ModifiedTime { get; set; }
}
