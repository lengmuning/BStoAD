using System.DirectoryServices.Protocols;
using System.Net;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.ActiveDirectory;

public interface ILdapConnectionFactory
{
    LdapConnection Create();
    void TestBind();
}

public sealed class LdapConnectionFactory : ILdapConnectionFactory
{
    private readonly ActiveDirectoryOptions _options;
    private readonly ILogger<LdapConnectionFactory> _logger;

    public LdapConnectionFactory(IOptions<ActiveDirectoryOptions> options, ILogger<LdapConnectionFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public LdapConnection Create()
    {
        var id = new LdapDirectoryIdentifier(_options.LdapHost, _options.Port);
        var creds = BuildCredential();
        var conn = new LdapConnection(id, creds, AuthType.Negotiate)
        {
            SessionOptions = { ProtocolVersion = 3 }
        };
        if (_options.UseSsl)
        {
            conn.SessionOptions.SecureSocketLayer = true;
            conn.SessionOptions.VerifyServerCertificate = (c, cert) => true;
        }
        else
        {
            // 389 端口必须开 Kerberos/NTLM 签名+封装加密：
            // 域控拒绝在未加密连接上写 unicodePwd（设置初始密码会报 unwillingToPerform）
            conn.SessionOptions.Signing = true;
            conn.SessionOptions.Sealing = true;
        }
        conn.Timeout = TimeSpan.FromSeconds(30);
        return conn;
    }

    public void TestBind()
    {
        using var conn = Create();
        conn.Bind();
        _logger.LogInformation("AD bind 成功: {Host}:{Port} SSL={Ssl} as {Account}",
            _options.LdapHost, _options.Port, _options.UseSsl, _options.ServiceAccount);
    }

    // SSPI 不一定认「域\用户」整串塞在用户名字段的写法（ADSI 会自动拆，LdapConnection 不会）
    // 这里把 DOMAIN\user 拆成 (user, 密码, 域)；user@domain 的 UPN 形式原样传
    private NetworkCredential BuildCredential()
    {
        var acct = _options.ServiceAccount;
        var idx = acct.IndexOf('\\');
        if (idx > 0)
            return new NetworkCredential(acct[(idx + 1)..], _options.ServicePassword, acct[..idx]);
        return new NetworkCredential(acct, _options.ServicePassword);
    }
}
