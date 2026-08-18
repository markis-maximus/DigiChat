using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigiChat.Infrastructure.Twitch;

/// <summary>
/// Twitch OAuth via the Device Code Grant flow (the current recommended flow
/// for local/standalone apps; verified against dev.twitch.tv docs 2026-08-10).
/// Public client: no client secret anywhere. Scope is the minimum for
/// channel.chat.message over WebSocket: user:read:chat.
/// </summary>
public class TwitchAuthService(
    IHttpClientFactory httpFactory,
    TwitchTokenStore tokenStore,
    IOptions<TwitchOptions> options,
    ILogger<TwitchAuthService> logger)
{
    public const string Scopes = "user:read:chat";
    private const string IdBase = "https://id.twitch.tv/oauth2";

    private string TokenPath => options.Value.TokenFile;

    /// <summary>Raised when the user must visit twitch.tv/activate; surfaced in the admin UI.</summary>
    public event Action<string /*userCode*/, string /*verificationUri*/>? DeviceCodePrompt;

    public sealed record ValidatedToken(string AccessToken, string UserId, string Login, int ExpiresInSeconds);

    /// <summary>
    /// Returns a valid access token: stored token if it validates, refreshed if
    /// possible, otherwise runs the interactive device flow (logging the code).
    /// </summary>
    public async Task<ValidatedToken> GetValidTokenAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient("twitch");
        var stored = tokenStore.Load(TokenPath);

        if (stored is not null)
        {
            var validated = await ValidateAsync(http, stored.AccessToken, ct);
            if (validated is not null) return validated;

            logger.LogInformation("Access token invalid/expired; attempting refresh");
            var refreshed = await TryRefreshAsync(http, stored.RefreshToken, ct);
            if (refreshed is not null)
            {
                var v = await ValidateAsync(http, refreshed.AccessToken, ct);
                if (v is not null) return v;
            }
            logger.LogWarning("Token refresh failed; falling back to device authorization");
        }

        return await RunDeviceFlowAsync(http, ct);
    }

    private async Task<ValidatedToken?> ValidateAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{IdBase}/validate");
        req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        var body = await res.Content.ReadFromJsonAsync<ValidateResponse>(ct);
        if (body is null || string.IsNullOrEmpty(body.UserId)) return null;
        var requiredScopes = Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(body.ClientId, options.Value.ClientId, StringComparison.Ordinal)
            || body.Scopes is null
            || requiredScopes.Except(body.Scopes, StringComparer.Ordinal).Any())
        {
            logger.LogWarning(
                "Stored Twitch token belongs to a different client or lacks the required scope; re-authorizing");
            return null;
        }
        return new ValidatedToken(accessToken, body.UserId, body.Login ?? "", body.ExpiresIn);
    }

    private async Task<StoredTokens?> TryRefreshAsync(HttpClient http, string refreshToken, CancellationToken ct)
    {
        using var res = await http.PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.Value.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }), ct);
        if (!res.IsSuccessStatusCode) return null;
        var body = await res.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (body?.AccessToken is null || body.RefreshToken is null) return null;

        // Device-flow refresh tokens are single-use: always persist the new pair.
        var tokens = new StoredTokens(body.AccessToken, body.RefreshToken, DateTime.UtcNow);
        tokenStore.Save(TokenPath, tokens);
        return tokens;
    }

    private async Task<ValidatedToken> RunDeviceFlowAsync(HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ClientId))
            throw new InvalidOperationException(
                "Twitch:ClientId is not configured. Register an app (client type: Public) at " +
                "https://dev.twitch.tv/console/apps and put its Client ID in appsettings.Local.json.");

        using var res = await http.PostAsync($"{IdBase}/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.Value.ClientId,
            ["scopes"] = Scopes,
        }), ct);
        res.EnsureSuccessStatusCode();
        var device = await res.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct)
                     ?? throw new InvalidOperationException("Empty device code response from Twitch.");

        logger.LogWarning(
            "TWITCH AUTHORIZATION REQUIRED: open {Uri} and enter code {Code} (expires in {Min} minutes)",
            device.VerificationUri, device.UserCode, device.ExpiresIn / 60);
        DeviceCodePrompt?.Invoke(device.UserCode, device.VerificationUri);

        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 5));
        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct);

            using var poll = await http.PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.Value.ClientId,
                ["scopes"] = Scopes,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }), ct);

            if (poll.IsSuccessStatusCode)
            {
                var body = await poll.Content.ReadFromJsonAsync<TokenResponse>(ct);
                if (body?.AccessToken is null || body.RefreshToken is null)
                    throw new InvalidOperationException("Twitch token response was missing tokens.");
                tokenStore.Save(TokenPath, new StoredTokens(body.AccessToken, body.RefreshToken, DateTime.UtcNow));
                logger.LogInformation("Twitch authorization complete");
                var v = await ValidateAsync(http, body.AccessToken, ct)
                        ?? throw new InvalidOperationException("Fresh token failed validation.");
                return v;
            }
            // 400 authorization_pending is the expected "keep waiting" answer.
        }
        throw new TimeoutException("Twitch device authorization was not completed before the code expired.");
    }

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record ValidateResponse(
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("scopes")] string[]? Scopes);
}
