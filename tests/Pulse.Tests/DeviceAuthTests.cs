using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>
/// The API with authentication switched on. Tokens are signed in the test with its own key, shaped like the ones
/// Keycloak issues, so the claims handling and the policies are exercised without running Keycloak.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class DeviceAuthTests : IDisposable
{
    private const string Issuer = "https://issuer.test/realms/greenhaus";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));

    private readonly PipelineFixture _stack;
    private readonly List<WebApplicationFactory<Program>> _hosts = [];
    private readonly HttpClient _client;

    public DeviceAuthTests(PipelineFixture stack)
    {
        _stack = stack;
        _client = Host();
    }

    public void Dispose()
    {
        _client.Dispose();
        foreach (var host in _hosts) host.Dispose();
    }

    private HttpClient Host(params (string, string)[] extra)
    {
        var host = _stack.CreateHttpOnlyHost(
            services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                // Validate against the test key instead of fetching the issuer's discovery document.
                jwt.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                jwt.TokenValidationParameters.IssuerSigningKey = SigningKey;
                jwt.TokenValidationParameters.ValidIssuer = Issuer;
            }),
            [("Auth:Enabled", "true"), ("Auth:Authority", Issuer), .. extra]);
        _hosts.Add(host);
        return host.CreateClient();
    }

    private static string Token(
        string subject,
        bool service = false,
        TimeSpan? lifetime = null,
        string issuer = Issuer,
        SecurityKey? key = null,
        string? audience = null,
        bool clientRole = false)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["preferred_username"] = subject };
        if (service && !clientRole) claims["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "offline_access", "pulse-service" } };
        if (service && clientRole) claims["resource_access"] = new Dictionary<string, object> { ["greenhaus-api"] = new Dictionary<string, object> { ["roles"] = new[] { "pulse-service" } } };

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = now.AddMinutes(-10),
            Expires = now + (lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    private static string NewId() => $"auth-{Guid.NewGuid():N}"[..14];

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/pulse/v2" + path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<HttpStatusCode> Status(HttpMethod method, string path, string? token, object? body = null)
    {
        using var response = await _client.SendAsync(Request(method, path, token, body));
        return response.StatusCode;
    }

    private async Task<T> Read<T>(HttpMethod method, string path, string token)
    {
        using var response = await _client.SendAsync(Request(method, path, token));
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} returned {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<T>(Web))!;
    }

    private static object Registration(string id) => new
    {
        id,
        model = "greenhaus-pot-v1",
        capabilities = new { channels = new[] { new { id = "soil-1", metric = "soil_moisture" } }, actuators = Array.Empty<object>() },
        credentialHash = "h",
        claimTokenHash = ClaimTokens.Hash("token"),
        claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    /// <summary>Registers, ships and claims a device as the service, so the tests have devices with owners.</summary>
    private async Task<string> DeviceOwnedBy(string owner)
    {
        var service = Token("greenhaus-api", service: true);
        var id = NewId();
        Assert.Equal(HttpStatusCode.Created, await Status(HttpMethod.Post, "/devices", service, Registration(id)));
        Assert.Equal(HttpStatusCode.OK, await Status(HttpMethod.Post, $"/devices/{id}/ship", service));
        Assert.Equal(HttpStatusCode.OK, await Status(HttpMethod.Post, $"/devices/{id}/claim", service, new { token = "token", ownerId = owner }));
        return id;
    }

    [Theory]
    [InlineData("GET", "/api/pulse/v2/devices")]
    [InlineData("GET", "/api/devices")]
    [InlineData("GET", "/api/alerts")]
    [InlineData("POST", "/api/telemetry")]
    [InlineData("POST", "/hubs/live/negotiate?negotiateVersion=1")]
    public async Task Without_a_token_everything_but_health_is_refused(string method, string path)
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_health_check_stays_open()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Tokens_that_are_expired_forged_or_from_another_issuer_are_refused()
    {
        var wrongKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(HttpMethod.Get, "/devices", Token("u", lifetime: TimeSpan.FromMinutes(-5))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(HttpMethod.Get, "/devices", Token("u", key: wrongKey)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(HttpMethod.Get, "/devices", Token("u", issuer: "https://evil.test/realms/x")));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(HttpMethod.Get, "/devices", "not.a.token"));
        Assert.Equal(HttpStatusCode.OK, await Status(HttpMethod.Get, "/devices", Token("u")));
    }

    [Fact]
    public async Task A_user_sees_only_their_own_devices()
    {
        var alice = $"alice-{Guid.NewGuid():N}";
        var bob = $"bob-{Guid.NewGuid():N}";
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(bob);

        var seenByAlice = await Read<Device[]>(HttpMethod.Get, "/devices", Token(alice));

        Assert.Equal([aliceDevice], seenByAlice.Select(d => d.Id));
        Assert.Equal(alice, seenByAlice.Single().OwnerId);
        Assert.Equal(HttpStatusCode.OK, await Status(HttpMethod.Get, $"/devices/{aliceDevice}", Token(alice)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(HttpMethod.Get, $"/devices/{bobDevice}", Token(alice))); // not "forbidden": it is not revealed to exist
    }

    [Fact]
    public async Task A_user_cannot_look_at_someone_elses_devices_by_asking_for_their_owner_id()
    {
        var alice = $"alice-{Guid.NewGuid():N}";
        var bob = $"bob-{Guid.NewGuid():N}";
        await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(bob);

        var devices = await Read<Device[]>(HttpMethod.Get, $"/devices?ownerId={bob}", Token(alice));

        Assert.DoesNotContain(devices, d => d.Id == bobDevice);
        Assert.All(devices, d => Assert.Equal(alice, d.OwnerId));
    }

    [Fact]
    public async Task A_user_with_no_devices_gets_an_empty_list_not_everything()
    {
        await DeviceOwnedBy($"someone-{Guid.NewGuid():N}");

        var devices = await Read<Device[]>(HttpMethod.Get, "/devices", Token($"newcomer-{Guid.NewGuid():N}"));

        Assert.Empty(devices);
    }

    [Fact]
    public async Task A_service_sees_every_device_and_can_filter_by_owner()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var mine = await DeviceOwnedBy(owner);
        var other = await DeviceOwnedBy($"other-{Guid.NewGuid():N}");
        var service = Token("greenhaus-api", service: true);

        var all = await Read<Device[]>(HttpMethod.Get, "/devices", service);
        var filtered = await Read<Device[]>(HttpMethod.Get, $"/devices?ownerId={owner}", service);

        Assert.Contains(all, d => d.Id == mine);
        Assert.Contains(all, d => d.Id == other);
        Assert.Equal([mine], filtered.Select(d => d.Id));
    }

    [Fact]
    public async Task Client_roles_count_as_well_as_realm_roles()
    {
        var token = Token("greenhaus-api", service: true, clientRole: true);

        Assert.Equal(HttpStatusCode.Created, await Status(HttpMethod.Post, "/devices", token, Registration(NewId())));
    }

    [Fact]
    public async Task A_user_cannot_do_what_only_services_may()
    {
        var user = Token($"user-{Guid.NewGuid():N}");
        var id = await DeviceOwnedBy("someone");

        Assert.Equal(HttpStatusCode.Forbidden, await Status(HttpMethod.Post, "/devices", user, Registration(NewId())));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(HttpMethod.Post, $"/devices/{id}/ship", user));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(HttpMethod.Post, $"/devices/{id}/claim", user, new { token = "token", ownerId = "me" }));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(HttpMethod.Post, $"/devices/{id}/retire", user));
    }

    [Fact]
    public async Task Only_a_service_may_send_telemetry()
    {
        using var asUser = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry") { Content = JsonContent.Create(Array.Empty<object>()) };
        asUser.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("user"));
        using var asService = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry") { Content = JsonContent.Create(Array.Empty<object>()) };
        asService.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("greenhaus-api", service: true));

        using var userResponse = await _client.SendAsync(asUser);
        using var serviceResponse = await _client.SendAsync(asService);

        Assert.Equal(HttpStatusCode.Forbidden, userResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, serviceResponse.StatusCode); // allowed in, then rejected for the empty batch
    }

    [Fact]
    public async Task A_user_can_calibrate_their_own_device_but_not_another_users()
    {
        var alice = $"alice-{Guid.NewGuid():N}";
        var bob = $"bob-{Guid.NewGuid():N}";
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(bob);
        var body = new { dryRaw = 800, wetRaw = 300 };

        Assert.Equal(HttpStatusCode.OK, await Status(HttpMethod.Put, $"/devices/{aliceDevice}/channels/soil-1/calibration", Token(alice), body));
        Assert.Equal(HttpStatusCode.NotFound, await Status(HttpMethod.Put, $"/devices/{bobDevice}/channels/soil-1/calibration", Token(alice), body));
    }

    [Fact]
    public async Task The_live_hub_accepts_a_token_in_the_query_string_as_browsers_must_send_it()
    {
        using var withToken = await _client.PostAsync($"/hubs/live/negotiate?negotiateVersion=1&access_token={Token("user")}", null);
        using var withBadToken = await _client.PostAsync("/hubs/live/negotiate?negotiateVersion=1&access_token=garbage", null);

        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withBadToken.StatusCode);
    }

    [Fact]
    public async Task A_configured_audience_is_enforced()
    {
        using var client = Host(("Auth:Audience", "pulse"));

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOn(client, "/devices", Token("u")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOn(client, "/devices", Token("u", audience: "another-api")));
        Assert.Equal(HttpStatusCode.OK, await StatusOn(client, "/devices", Token("u", audience: "pulse")));
    }

    private static async Task<HttpStatusCode> StatusOn(HttpClient client, string path, string token)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, path, token));
        return response.StatusCode;
    }

}
