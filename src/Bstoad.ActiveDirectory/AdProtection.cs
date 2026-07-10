using Bstoad.Core.Options;

namespace Bstoad.ActiveDirectory;

public static class AdProtection
{
    /// <summary>
    /// 保护判定：特权账号（adminCount=1）或位于禁区 OU 下的账号，
    /// sync 永不接管/修改/移动/禁用。
    /// </summary>
    public static bool IsProtected(AdUserSnapshot u, ActiveDirectoryOptions opt, out string reason)
    {
        if (u.AdminCount == 1)
        {
            reason = "特权账号(adminCount=1)";
            return true;
        }
        foreach (var dn in opt.NeverTouchOuDns)
        {
            if (string.IsNullOrWhiteSpace(dn)) continue;
            if (u.Dn.EndsWith("," + dn, StringComparison.OrdinalIgnoreCase))
            {
                reason = "位于禁区: " + dn;
                return true;
            }
        }
        reason = "";
        return false;
    }
}
