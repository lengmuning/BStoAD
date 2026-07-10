using System.DirectoryServices.Protocols;
using System.Text;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.ActiveDirectory;

public interface IAdProvisioner
{
    void EnsureOu(string ouDn);

    AdOuSnapshot? FindOuByOrgIdMarker(string orgId);
    IReadOnlyList<AdOuSnapshot> ListOusUnder(string baseOu);
    string CreateOuForOrg(string parentOuDn, string ouName, string orgId);
    string RenameOrMoveOu(string currentDn, string newParentDn, string newRdn);

    AdUserSnapshot? FindUserByEmployeeId(string employeeId);
    AdUserSnapshot? FindUserBySam(string samAccountName);
    AdUserSnapshot? FindLegacyBySam(string samAccountName);
    IReadOnlyList<AdUserSnapshot> FindUsersByMail(string mail);
    AdUserSnapshot? GetUser(string userDn);

    string CreateUser(string parentOuDn, AdUserAttributes attrs, string initialPassword);
    void UpdateUserAttributes(string userDn, AdUserAttributes attrs);
    string MoveUser(string userDn, string newParentOuDn, string? rdnUniquifier = null);

    void DisableUser(string userDn);
    void EnableUser(string userDn);

    void BackfillEmployeeId(string userDn, string employeeId);

    bool HasManagedMarker(string userDn);
    void EnsureManagedMarker(string userDn);

    IReadOnlyList<AdUserSnapshot> ListManagedUsersUnder(string baseOu);
    IReadOnlyList<AdUserSnapshot> ListAllUsersUnder(string baseOu);
}

public sealed class AdProvisioner : IAdProvisioner
{
    private readonly ILdapConnectionFactory _connFactory;
    private readonly ActiveDirectoryOptions _options;
    private readonly ILogger<AdProvisioner> _logger;

    private static readonly string[] UserAttrs =
    {
        "distinguishedName", "sAMAccountName", "userPrincipalName", "displayName",
        "mail", "telephoneNumber", "mobile", "department", "title",
        "employeeID", "adminDescription", "userAccountControl", "adminCount"
    };

    private static readonly string[] OuAttrs =
    {
        "distinguishedName", "ou", "description", "info"
    };

    public AdProvisioner(
        ILdapConnectionFactory connFactory,
        IOptions<ActiveDirectoryOptions> options,
        ILogger<AdProvisioner> logger)
    {
        _connFactory = connFactory;
        _options = options.Value;
        _logger = logger;
    }

    // ---------------- OU ----------------

    public void EnsureOu(string ouDn)
    {
        using var conn = _connFactory.Create();
        EnsureOuRecursive(conn, ouDn);
    }

    private void EnsureOuRecursive(LdapConnection conn, string ouDn)
    {
        if (Exists(conn, ouDn)) return;
        var parent = ParentOf(ouDn);
        if (!string.IsNullOrEmpty(parent) && !Exists(conn, parent))
            EnsureOuRecursive(conn, parent);

        var rdn = ouDn.Split(',')[0];
        var name = rdn[(rdn.IndexOf('=') + 1)..];
        var req = new AddRequest(ouDn,
            new DirectoryAttribute("objectClass", "organizationalUnit"),
            new DirectoryAttribute("ou", name));
        conn.SendRequest(req);
        _logger.LogInformation("创建 OU: {Dn}", ouDn);
    }

    // ---------------- OU 锚点 ----------------

    public AdOuSnapshot? FindOuByOrgIdMarker(string orgId)
    {
        if (string.IsNullOrWhiteSpace(orgId)) return null;
        using var conn = _connFactory.Create();
        var markerValue = _options.OuMarkerPrefix + orgId;
        var filter = $"(&(objectClass=organizationalUnit)({_options.OuMarkerAttribute}={Escape(markerValue)}))";
        try
        {
            var req = new SearchRequest(_options.BaseUserOu, filter, SearchScope.Subtree, OuAttrs);
            var resp = (SearchResponse)conn.SendRequest(req);
            if (resp.Entries.Count == 0) return null;
            if (resp.Entries.Count > 1)
                _logger.LogWarning("OU 锚点 {OrgId} 在 {Base} 下命中 {Count} 个 OU，使用第一个: {Dn}",
                    orgId, _options.BaseUserOu, resp.Entries.Count, resp.Entries[0].DistinguishedName);
            return ToOuSnapshot(resp.Entries[0]);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    public IReadOnlyList<AdOuSnapshot> ListOusUnder(string baseOu)
    {
        using var conn = _connFactory.Create();
        var result = new List<AdOuSnapshot>();
        try
        {
            var req = new SearchRequest(baseOu, "(objectClass=organizationalUnit)", SearchScope.Subtree, OuAttrs);
            var resp = (SearchResponse)conn.SendRequest(req);
            foreach (SearchResultEntry e in resp.Entries) result.Add(ToOuSnapshot(e));
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            // baseDn 不存在
        }
        return result;
    }

    public string CreateOuForOrg(string parentOuDn, string ouName, string orgId)
    {
        using var conn = _connFactory.Create();
        if (!Exists(conn, parentOuDn))
            throw new InvalidOperationException($"父 OU 不存在: {parentOuDn}");

        var rdnName = EscapeRdn(ouName);
        var ouDn = $"OU={rdnName},{parentOuDn}";
        var markerValue = _options.OuMarkerPrefix + orgId;

        var req = new AddRequest(ouDn,
            new DirectoryAttribute("objectClass", "organizationalUnit"),
            new DirectoryAttribute("ou", ouName),
            new DirectoryAttribute(_options.OuMarkerAttribute, markerValue));

        conn.SendRequest(req);
        _logger.LogInformation("创建 OU: {Dn} (锚点 {Attr}={Marker})", ouDn, _options.OuMarkerAttribute, markerValue);
        return ouDn;
    }

    public string RenameOrMoveOu(string currentDn, string newParentDn, string newRdn)
    {
        using var conn = _connFactory.Create();
        if (!Exists(conn, newParentDn))
            throw new InvalidOperationException($"目标父 OU 不存在: {newParentDn}");

        // ModifyDNRequest 一次性改 parent + RDN；子对象 DN 和 ou 属性由 AD 自动跟随
        var req = new ModifyDNRequest(currentDn, newParentDn, newRdn);
        conn.SendRequest(req);
        var newDn = $"{newRdn},{newParentDn}";

        _logger.LogInformation("OU 改名/移动: {Old} → {New}", currentDn, newDn);
        return newDn;
    }

    // ---------------- 查找（全域） ----------------

    public AdUserSnapshot? FindUserByEmployeeId(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return null;
        using var conn = _connFactory.Create();
        var filter = $"(&(objectCategory=person)(objectClass=user)(employeeID={Escape(employeeId)}))";
        var hits = SearchMany(conn, RootDn(), filter, SearchScope.Subtree);
        if (hits.Count == 0) return null;
        if (hits.Count > 1)
            _logger.LogWarning("employeeID={EmpId} 在 AD 命中 {Count} 个账号（数据质量问题，请人工清理），使用第一个: {Dn}",
                employeeId, hits.Count, hits[0].Dn);
        return hits[0];
    }

    public AdUserSnapshot? FindUserBySam(string samAccountName)
    {
        if (string.IsNullOrWhiteSpace(samAccountName)) return null;
        using var conn = _connFactory.Create();
        var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={Escape(samAccountName)}))";
        return SearchOne(conn, RootDn(), filter);
    }

    public AdUserSnapshot? FindLegacyBySam(string samAccountName)
    {
        var hit = FindUserBySam(samAccountName);
        if (hit == null) return null;
        if (!string.IsNullOrEmpty(hit.EmployeeId)) return null;
        return hit;
    }

    public IReadOnlyList<AdUserSnapshot> FindUsersByMail(string mail)
    {
        if (string.IsNullOrWhiteSpace(mail)) return Array.Empty<AdUserSnapshot>();
        using var conn = _connFactory.Create();
        var filter = $"(&(objectCategory=person)(objectClass=user)(mail={Escape(mail)}))";
        return SearchMany(conn, RootDn(), filter, SearchScope.Subtree);
    }

    public AdUserSnapshot? GetUser(string userDn)
    {
        using var conn = _connFactory.Create();
        try
        {
            var req = new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, UserAttrs);
            var resp = (SearchResponse)conn.SendRequest(req);
            return resp.Entries.Count == 0 ? null : ToSnapshot(resp.Entries[0]);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    // ---------------- 写：新建 / 更新 / 移动 ----------------

    public string CreateUser(string parentOuDn, AdUserAttributes attrs, string initialPassword)
    {
        EnsureOu(parentOuDn);
        using var conn = _connFactory.Create();
        var cnEscaped = EscapeRdn(attrs.DisplayName);
        var userDn = $"CN={cnEscaped},{parentOuDn}";

        // 中文重名防御：同 OU 已有同名 CN 时，用「姓名 (工号)」保证唯一
        if (Exists(conn, userDn) && !string.IsNullOrEmpty(attrs.EmployeeId))
        {
            var altCn = $"{cnEscaped} ({attrs.EmployeeId})";
            _logger.LogWarning("CN 重名: {Dn} 已存在，改用 CN={Alt}", userDn, altCn);
            userDn = $"CN={altCn},{parentOuDn}";
        }

        var add = new AddRequest(userDn,
            new DirectoryAttribute("objectClass", "top", "person", "organizationalPerson", "user"),
            new DirectoryAttribute("sAMAccountName", attrs.SamAccountName),
            new DirectoryAttribute("userPrincipalName", attrs.UserPrincipalName),
            new DirectoryAttribute("displayName", attrs.DisplayName),
            new DirectoryAttribute("name", attrs.DisplayName));

        AddIf(add, "givenName", attrs.GivenName);
        AddIf(add, "sn", attrs.Surname);
        AddIf(add, "mail", attrs.Mail);
        AddIf(add, "telephoneNumber", attrs.TelephoneNumber);
        AddIf(add, "mobile", attrs.Mobile);
        AddIf(add, "department", attrs.Department);
        AddIf(add, "title", attrs.Title);
        AddIf(add, "employeeID", attrs.EmployeeId);

        conn.SendRequest(add);
        _logger.LogInformation("创建账号: {Dn}", userDn);

        SetPassword(conn, userDn, initialPassword);

        var uac = AdUserAttributes.UF_NORMAL_ACCOUNT;
        if (_options.PasswordNeverExpires) uac |= AdUserAttributes.UF_DONT_EXPIRE_PASSWD;
        ModifyReplace(conn, userDn, "userAccountControl", uac.ToString());

        if (_options.ForceChangePasswordAtNextLogon)
            ModifyReplace(conn, userDn, "pwdLastSet", "0");

        EnsureManagedMarkerInternal(conn, userDn);
        return userDn;
    }

    public void UpdateUserAttributes(string userDn, AdUserAttributes attrs)
    {
        using var conn = _connFactory.Create();
        var mods = new List<DirectoryAttributeModification>();
        AddReplace(mods, "displayName", attrs.DisplayName);
        AddReplace(mods, "userPrincipalName", attrs.UserPrincipalName);
        AddReplace(mods, "givenName", attrs.GivenName);
        AddReplace(mods, "sn", attrs.Surname);
        AddReplace(mods, "mail", attrs.Mail);
        AddReplace(mods, "telephoneNumber", attrs.TelephoneNumber);
        AddReplace(mods, "mobile", attrs.Mobile);
        AddReplace(mods, "department", attrs.Department);
        AddReplace(mods, "title", attrs.Title);
        AddReplace(mods, "employeeID", attrs.EmployeeId);

        if (mods.Count > 0)
        {
            conn.SendRequest(new ModifyRequest(userDn, mods.ToArray()));
            _logger.LogInformation("更新属性: {Dn} ({Count} 项)", userDn, mods.Count);
        }

        EnsureManagedMarkerInternal(conn, userDn);
    }

    public string MoveUser(string userDn, string newParentOuDn, string? rdnUniquifier = null)
    {
        EnsureOu(newParentOuDn);
        using var conn = _connFactory.Create();
        var rdn = userDn.Split(',')[0];
        try
        {
            conn.SendRequest(new ModifyDNRequest(userDn, newParentOuDn, rdn));
            var newDn = $"{rdn},{newParentOuDn}";
            _logger.LogInformation("移动账号: {From} → {To}", userDn, newDn);
            return newDn;
        }
        catch (DirectoryOperationException ex)
            when (ex.Response?.ResultCode == ResultCode.EntryAlreadyExists && !string.IsNullOrEmpty(rdnUniquifier))
        {
            // 目标 OU 已有同名 CN（中文重名）：移动同时改名为「姓名 (工号)」
            var altRdn = $"{rdn} ({rdnUniquifier})";
            _logger.LogWarning("移动遇 CN 重名: {Rdn} 在 {Ou} 已存在，改用 {Alt}", rdn, newParentOuDn, altRdn);
            conn.SendRequest(new ModifyDNRequest(userDn, newParentOuDn, altRdn));
            var newDn = $"{altRdn},{newParentOuDn}";
            _logger.LogInformation("移动账号: {From} → {To}", userDn, newDn);
            return newDn;
        }
    }

    // ---------------- 启用 / 禁用 ----------------

    public void DisableUser(string userDn) => SetAccountDisabled(userDn, true);
    public void EnableUser(string userDn) => SetAccountDisabled(userDn, false);

    private void SetAccountDisabled(string userDn, bool disabled)
    {
        using var conn = _connFactory.Create();
        var search = new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, "userAccountControl");
        var resp = (SearchResponse)conn.SendRequest(search);
        if (resp.Entries.Count == 0) throw new InvalidOperationException("user not found: " + userDn);
        var current = int.Parse(Convert.ToString(resp.Entries[0].Attributes["userAccountControl"][0])!);
        var next = disabled
            ? current | AdUserAttributes.UF_ACCOUNTDISABLE
            : current & ~AdUserAttributes.UF_ACCOUNTDISABLE;
        if (next == current) return;
        ModifyReplace(conn, userDn, "userAccountControl", next.ToString());
        _logger.LogInformation("{Action} 账号: {Dn}", disabled ? "禁用" : "启用", userDn);
    }

    // ---------------- 回填 employeeID ----------------

    public void BackfillEmployeeId(string userDn, string employeeId)
    {
        using var conn = _connFactory.Create();
        ModifyReplace(conn, userDn, "employeeID", employeeId);
        _logger.LogInformation("回填 employeeID={EmpId} → {Dn}", employeeId, userDn);
    }

    // ---------------- 纳管标记 ----------------

    public bool HasManagedMarker(string userDn)
    {
        var u = GetUser(userDn);
        return u?.HasManagedMarker(_options.ManagedMarkerValue) ?? false;
    }

    public void EnsureManagedMarker(string userDn)
    {
        using var conn = _connFactory.Create();
        EnsureManagedMarkerInternal(conn, userDn);
    }

    private void EnsureManagedMarkerInternal(LdapConnection conn, string userDn)
    {
        var attr = _options.ManagedMarkerAttribute;
        var value = _options.ManagedMarkerValue;

        var search = new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, attr);
        var resp = (SearchResponse)conn.SendRequest(search);
        if (resp.Entries.Count == 0) return;

        var entry = resp.Entries[0];
        var current = entry.Attributes.Contains(attr)
            ? Convert.ToString(entry.Attributes[attr][0]) : null;

        if (string.Equals(current, value, StringComparison.Ordinal))
            return;

        ModifyReplace(conn, userDn, attr, value);
        _logger.LogInformation("打纳管标记 {Attr}={Value} → {Dn}", attr, value, userDn);
    }

    // ---------------- 列表 ----------------

    public IReadOnlyList<AdUserSnapshot> ListManagedUsersUnder(string baseOu)
    {
        using var conn = _connFactory.Create();
        var filter = "(&(objectCategory=person)(objectClass=user)" +
                     $"({_options.ManagedMarkerAttribute}={Escape(_options.ManagedMarkerValue)}))";
        return SearchMany(conn, baseOu, filter, SearchScope.Subtree);
    }

    public IReadOnlyList<AdUserSnapshot> ListAllUsersUnder(string baseOu)
    {
        using var conn = _connFactory.Create();
        var filter = "(&(objectCategory=person)(objectClass=user))";
        return SearchMany(conn, baseOu, filter, SearchScope.Subtree);
    }

    // ---------------- 私有 ----------------

    private AdUserSnapshot? SearchOne(LdapConnection conn, string baseDn, string filter)
    {
        try
        {
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, UserAttrs);
            var resp = (SearchResponse)conn.SendRequest(req);
            if (resp.Entries.Count == 0) return null;
            return ToSnapshot(resp.Entries[0]);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    private IReadOnlyList<AdUserSnapshot> SearchMany(LdapConnection conn, string baseDn, string filter, SearchScope scope)
    {
        var result = new List<AdUserSnapshot>();
        try
        {
            var req = new SearchRequest(baseDn, filter, scope, UserAttrs);
            var resp = (SearchResponse)conn.SendRequest(req);
            foreach (SearchResultEntry e in resp.Entries) result.Add(ToSnapshot(e));
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            // baseDn 不存在，返回空
        }
        return result;
    }

    private AdOuSnapshot ToOuSnapshot(SearchResultEntry e)
    {
        string? S(string a) => e.Attributes.Contains(a) ? Convert.ToString(e.Attributes[a][0]) : null;
        var desc = S(_options.OuMarkerAttribute);
        string? orgId = null;
        if (!string.IsNullOrEmpty(desc) && desc.StartsWith(_options.OuMarkerPrefix, StringComparison.Ordinal))
            orgId = desc[_options.OuMarkerPrefix.Length..];

        return new AdOuSnapshot
        {
            Dn = e.DistinguishedName,
            Ou = S("ou") ?? "",
            Description = desc,
            OrgId = orgId,
        };
    }

    private static AdUserSnapshot ToSnapshot(SearchResultEntry e)
    {
        string? S(string a) => e.Attributes.Contains(a) ? Convert.ToString(e.Attributes[a][0]) : null;
        int uac = int.TryParse(S("userAccountControl"), out var v) ? v : 0;
        return new AdUserSnapshot
        {
            Dn = e.DistinguishedName,
            SamAccountName = S("sAMAccountName") ?? "",
            UserPrincipalName = S("userPrincipalName"),
            DisplayName = S("displayName"),
            Mail = S("mail"),
            TelephoneNumber = S("telephoneNumber"),
            Mobile = S("mobile"),
            Department = S("department"),
            Title = S("title"),
            EmployeeId = S("employeeID"),
            AdminDescription = S("adminDescription"),
            UserAccountControl = uac,
            AdminCount = int.TryParse(S("adminCount"), out var ac) ? ac : null,
        };
    }

    private static void SetPassword(LdapConnection conn, string userDn, string password)
    {
        var quoted = "\"" + password + "\"";
        var bytes = Encoding.Unicode.GetBytes(quoted);
        var mod = new DirectoryAttributeModification
        {
            Name = "unicodePwd",
            Operation = DirectoryAttributeOperation.Replace
        };
        mod.Add(bytes);
        conn.SendRequest(new ModifyRequest(userDn, mod));
    }

    private static void ModifyReplace(LdapConnection conn, string dn, string attr, string value)
    {
        var mod = new DirectoryAttributeModification
        {
            Name = attr,
            Operation = DirectoryAttributeOperation.Replace
        };
        mod.Add(value);
        conn.SendRequest(new ModifyRequest(dn, mod));
    }

    private static void AddIf(AddRequest req, string attr, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            req.Attributes.Add(new DirectoryAttribute(attr, value));
    }

    private static void AddReplace(List<DirectoryAttributeModification> mods, string attr, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var m = new DirectoryAttributeModification
        {
            Name = attr,
            Operation = DirectoryAttributeOperation.Replace
        };
        m.Add(value);
        mods.Add(m);
    }

    private static bool Exists(LdapConnection conn, string dn)
    {
        try
        {
            var req = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, "1.1");
            var resp = (SearchResponse)conn.SendRequest(req);
            return resp.Entries.Count > 0;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return false;
        }
    }

    private static string ParentOf(string dn)
    {
        var idx = dn.IndexOf(',');
        return idx < 0 ? "" : dn[(idx + 1)..];
    }

    private string RootDn()
    {
        var parts = _options.BaseUserOu.Split(',');
        var dc = parts.Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase));
        return string.Join(",", dc);
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

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
