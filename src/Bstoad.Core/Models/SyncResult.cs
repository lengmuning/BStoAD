namespace Bstoad.Core.Models;

public sealed class SyncResult
{
    public int OrgsFetched { get; set; }
    public int EmployeesFetched { get; set; }
    public int OuCreated { get; set; }
    public int OuRenamedOrMoved { get; set; }
    public int OrgsAbandoned { get; set; }
    public int UsersCreated { get; set; }
    public int UsersUpdated { get; set; }
    public int UsersMoved { get; set; }
    public int UsersDisabled { get; set; }
    public int LegacyAdopted { get; set; }
    public int LeaversReconciled { get; set; }
    public int ScenarioE { get; set; }
    public int ProtectedSkipped { get; set; }
    public int Failures { get; set; }
    public List<string> Errors { get; } = new();
    public TimeSpan Duration { get; set; }
}
