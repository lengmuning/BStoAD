using Bstoad.ActiveDirectory;
using Bstoad.Core.Models;
using Bstoad.Core.Options;

namespace Bstoad.Sync.Mapping;

public sealed class EmployeeMapper
{
    private readonly ActiveDirectoryOptions _ad;

    public EmployeeMapper(ActiveDirectoryOptions adOptions)
    {
        _ad = adOptions;
    }

    public AdUserAttributes Map(BeisenEmployee emp)
    {
        var sam = !string.IsNullOrWhiteSpace(emp.EmployeeId) ? emp.EmployeeId.Trim() : emp.UserId.Trim();
        if (sam.Length > 20) sam = sam[..20];

        var display = !string.IsNullOrWhiteSpace(emp.Name) ? emp.Name.Trim() : sam;

        return new AdUserAttributes
        {
            SamAccountName = sam,
            UserPrincipalName = $"{sam}@{_ad.UpnSuffix}",
            DisplayName = display,
            GivenName = emp.GivenName,
            Surname = emp.Surname,
            Mail = emp.Email,
            TelephoneNumber = emp.Phone,
            Mobile = emp.Mobile,
            Department = emp.OrgName,
            Title = emp.Title,
            EmployeeId = emp.EmployeeId,
        };
    }
}
