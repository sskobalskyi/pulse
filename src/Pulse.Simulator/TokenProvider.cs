using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Pulse.Simulator;

public sealed class SimulatorAuthOptions
{
    /// <summary>The identity provider's token endpoint. Empty means no authentication, which is how the standalone demo runs.</summary>
    public string? TokenUrl { get; set; }

    public string ClientId { get; set; } = "pulse-service";

    public string? ClientSecret { get; set; }
}

/// <summary>
/// Gets an access token with the client-credentials grant and keeps it until shortly before it expires, so the
/// simulator can talk to an API that requires authentication without asking for a token on every request.
/// </summary>
public sealed class TokenProvider(IHttpClientFactory httpFactory, IOptions<SimulatorOptions> options, TimeProvider time)
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    /// <summary>The token to send, or null when authentication is not configured.</summary>
    public async Task<string?> GetAsync(CancellationToken ct)
    {
        var auth = options.Value.Auth;
        if (string.IsNullOrWhiteSpace(auth.TokenUrl)) return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && time.GetUtcNow() < _expiresAt - RenewBefore) return _token;

            using var response = await httpFactory.CreateClient().PostAsync(auth.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = auth.ClientId,
                ["client_secret"] = auth.ClientSecret ?? "",
            }), ct);
            response.EnsureSuccessStatusCode();

            var granted = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new InvalidOperationException("The token endpoint returned an empty response.");
            _token = granted.AccessToken;
            _expiresAt = time.GetUtcNow().AddSeconds(granted.ExpiresIn);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
