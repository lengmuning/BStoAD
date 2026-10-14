using System.DirectoryServices.Protocols;
using Bstoad.ActiveDirectory;

namespace Bstoad.Worker.Gui;

internal sealed record OuNode(string Dn, string Name, string? Description);

/// <summary>
/// 图形界面专用的 AD 辅助操作（浏览 OU、清理空 OU）。
/// 注意：删除只存在于这里、且只在人工点击并确认后执行；同步引擎（命令行/计划任务）不含任何删除操作。
/// </summary>
internal static class AdBrowser
{
    public static string GetDefaultNamingContext(ILdapConnectionFactory f)
    {
        using var c = f.Create();
        var resp = (SearchResponse)c.SendRequest(
            new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));
        return Convert.ToString(resp.Entries[0].Attributes["defaultNamingContext"][0])
               ?? throw new InvalidOperationException("读取域根失败");
    }

    public static List<OuNode> ListChildOus(ILdapConnectionFactory f, string parentDn)
    {
        using var c = f.Create();
        var resp = (SearchResponse)c.SendRequest(new SearchRequest(parentDn,
            "(objectClass=organizationalUnit)", SearchScope.OneLevel, "ou", "description"));
        var list = new List<OuNode>();
        foreach (SearchResultEntry e in resp.Entries)
        {
            string? S(string a) => e.Attributes.Contains(a) ? Convert.ToString(e.Attributes[a][0]) : null;
            list.Add(new OuNode(e.DistinguishedName, S("ou") ?? e.DistinguishedName, S("description")));
        }
        return list.OrderBy(n => n.Name, StringComparer.CurrentCulture).ToList();
    }

    public static bool Exists(ILdapConnectionFactory f, string dn)
    {
        using var c = f.Create();
        try
        {
            var resp = (SearchResponse)c.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, "1.1"));
            return resp.Entries.Count > 0;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return false;
        }
    }

    public static int CountChildren(ILdapConnectionFactory f, string dn)
    {
        using var c = f.Create();
        var resp = (SearchResponse)c.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.OneLevel, "1.1"));
        return resp.Entries.Count;
    }

    /// <summary>只删除没有任何子对象的 OU；AD 本身也会拒绝删除非空 OU（双重保险）。</summary>
    public static void DeleteEmptyOu(ILdapConnectionFactory f, string dn)
    {
        var n = CountChildren(f, dn);
        if (n > 0) throw new InvalidOperationException($"OU 里还有 {n} 个对象，不能删除");
        using var c = f.Create();
        try
        {
            c.SendRequest(new DeleteRequest(dn));
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.InsufficientAccessRights)
        {
            throw new InvalidOperationException(
                "权限不足。通常是 OU 勾选了「防止对象被意外删除」：ADUC → 查看 → 高级功能 → OU 属性 → 对象 → 取消勾选后重试。");
        }
    }
}
