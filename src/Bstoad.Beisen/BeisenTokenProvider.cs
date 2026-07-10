using System.Text.Json;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Beisen;

public interface IBeisenTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
    Task<string> ForceRefreshAsync(CancellationToken ct = default);
}

public sealed class BeisenTokenProvider : IBeisenTokenProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BeisenOptions _options;
    private readonly ILogger<BeisenTokenProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public BeisenTokenProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<BeisenOptions> options,
        ILogger<BeisenTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow.AddSeconds(_options.TokenRefreshSkewSeconds) < _expiresAt)
            return _accessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow.AddSeconds(_options.TokenRefreshSkewSeconds) < _expiresAt)
                return _accessToken;

            await RefreshInternalAsync(ct);
            return _accessToken!;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<string> ForceRefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            await RefreshInternalAsync(ct);
            return _accessToken!;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshInternalAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret))
            throw new InvalidOperationException("Beisen AppKey / AppSecret 未配置。请在 appsettings 或环境变量中提供。");

        var client = _httpClientFactory.CreateClient(nameof(BeisenTokenProvider));
        var url = new Uri(new Uri(_options.BaseUrl), _options.TokenEndpoint);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("app_key", _options.AppKey),
            new KeyValuePair<string, string>("app_secret", _options.AppSecret),
        });

        _logger.LogInformation("向北森请求 access_token: {Url}", url);
        using var resp = await client.PostAsync(url, form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("取 token 失败 {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Beisen token endpoint returned {(int)resp.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = TryGetString(root, "access_token", "Access_Token", "AccessToken");
        var expiresIn = TryGetInt(root, "expires_in", "Expires_In", "ExpiresIn") ?? 7200;

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("token 响应缺少 access_token 字段: " + body);

        _accessToken = accessToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        _logger.LogInformation("取 token 成功，{ExpiresIn}s 后过期", expiresIn);
    }

    private static string? TryGetString(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static int? TryGetInt(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
                return n;
        return null;
    }
}
