using System.Net;
using System.Net.Http.Json;
using KeelAssistant.Models;

namespace KeelAssistant.Usage;

/// <summary>
/// 呼叫官方未公開的 OAuth usage 端點，取得與 /usage 指令相同的精準額度。
/// 注意：必帶 User-Agent: claude-code/&lt;version&gt;，否則進入嚴格 429 桶。
/// </summary>
public sealed class OAuthUsageClient
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string OAuthBeta = "oauth-2025-04-20";

    private readonly HttpClient _http = new();
    private readonly CredentialStore _store;
    private readonly AppSettings _settings;
    private readonly string _userAgent;

    public OAuthUsageClient(CredentialStore store, AppSettings settings, string claudeVersion)
    {
        _store = store;
        _settings = settings;
        _userAgent = $"claude-code/{claudeVersion}";
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 取得目前用量。會在 token 過期或收到 401 時自動換新後重試一次。
    /// </summary>
    public async Task<OAuthUsageResponse> GetUsageAsync(CancellationToken ct = default)
    {
        var cred = _store.Load()
            ?? throw new InvalidOperationException("找不到 .credentials.json，請先以 claude 登入。");

        if (cred.IsExpired)
            cred = await RefreshAsync(cred, ct);

        var resp = await SendUsageRequestAsync(cred.AccessToken, ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            cred = await RefreshAsync(cred, ct);
            resp = await SendUsageRequestAsync(cred.AccessToken, ct);
        }

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException("usage 端點回 429，請拉長輪詢間隔（>=180 秒）。");

        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<OAuthUsageResponse>(cancellationToken: ct);
        return body ?? new OAuthUsageResponse();
    }

    private async Task<HttpResponseMessage> SendUsageRequestAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        req.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return await _http.SendAsync(req, ct);
    }

    /// <summary>用 refresh token 換新 access token，並寫回 .credentials.json。</summary>
    private async Task<OAuthCredentials> RefreshAsync(OAuthCredentials cred, CancellationToken ct)
    {
        var payload = new
        {
            grant_type = "refresh_token",
            refresh_token = cred.RefreshToken,
            client_id = _settings.OAuthClientId
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        req.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new TokenRefreshException(
                $"Token 換新失敗（{(int)resp.StatusCode}），請重新執行 claude 登入。");

        var refreshed = await resp.Content.ReadFromJsonAsync<TokenRefreshResponse>(cancellationToken: ct)
            ?? throw new TokenRefreshException("Token 換新回應無法解析。");

        cred.AccessToken = refreshed.AccessToken;
        if (!string.IsNullOrEmpty(refreshed.RefreshToken))
            cred.RefreshToken = refreshed.RefreshToken;
        cred.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn).ToUnixTimeMilliseconds();

        _store.Save(cred);
        return cred;
    }
}

public sealed class RateLimitedException(string message) : Exception(message);
public sealed class TokenRefreshException(string message) : Exception(message);
