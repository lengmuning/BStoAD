using Bstoad.Core.Models;

namespace Bstoad.Sync.Mapping;

public static class OrgTopologicalSorter
{
    /// <summary>
    /// 自顶向下排序：父 org 必然在子 org 之前。
    /// 父不在集合里的视为顶层。环、缺父、自引用都安全处理。
    /// </summary>
    public static List<BeisenOrg> Sort(IEnumerable<BeisenOrg> orgs)
    {
        var list = orgs.ToList();
        var byId = list.ToDictionary(o => o.OrgId, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BeisenOrg>(list.Count);

        void Visit(BeisenOrg org)
        {
            if (visited.Contains(org.OrgId)) return;
            if (!visiting.Add(org.OrgId)) return;
            if (!string.IsNullOrEmpty(org.ParentOrgId)
                && !string.Equals(org.ParentOrgId, org.OrgId, StringComparison.Ordinal)
                && byId.TryGetValue(org.ParentOrgId, out var parent))
            {
                Visit(parent);
            }
            visiting.Remove(org.OrgId);
            visited.Add(org.OrgId);
            result.Add(org);
        }

        foreach (var org in list) Visit(org);
        return result;
    }
}
