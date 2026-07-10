namespace Bstoad.Core.Models;

public sealed class BeisenOrg
{
    public string OrgId { get; set; } = "";
    public string? ParentOrgId { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ModifiedTime { get; set; }
}
