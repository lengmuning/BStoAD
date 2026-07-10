using System.Text;
using Bstoad.Core.Models;

namespace Bstoad.Sync.Mapping;

public sealed class OrgPathBuilder
{
    private readonly Dictionary<string, BeisenOrg> _byId;
    private readonly string _baseOuDn;

    public OrgPathBuilder(IEnumerable<BeisenOrg> orgs, string baseOuDn)
    {
        _byId = orgs.ToDictionary(o => o.OrgId);
        _baseOuDn = baseOuDn;
    }

    public string BuildOuDn(string orgId)
    {
        var chain = new List<BeisenOrg>();
        var seen = new HashSet<string>();
        var current = orgId;
        while (!string.IsNullOrEmpty(current) && _byId.TryGetValue(current, out var org))
        {
            if (!seen.Add(current)) break;
            chain.Add(org);
            current = org.ParentOrgId ?? "";
        }
        chain.Reverse();

        var sb = new StringBuilder();
        foreach (var org in chain)
            sb.Append("OU=").Append(EscapeRdn(org.Name)).Append(',');
        sb.Append(_baseOuDn);
        return sb.ToString();
    }

    public IEnumerable<string> AllOuDns()
    {
        foreach (var org in _byId.Values)
            yield return BuildOuDn(org.OrgId);
    }

    private static string EscapeRdn(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (",+\"\\<>;=".IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
