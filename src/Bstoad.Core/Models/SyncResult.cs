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
    public List<SyncEvent> Events { get; } = new();
    public TimeSpan Duration { get; set; }

    public void AddEvent(SyncEventLevel level, string category, string key, string name, string detail, string? dn = null)
        => Events.Add(new SyncEvent { Level = level, Category = category, Key = key, Name = name, Detail = detail, Dn = dn });
}
