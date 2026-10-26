using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pulse.Core;
using static Pulse.Tests.AuthTestKit;

namespace Pulse.Tests;

/// <summary>
/// With authentication on, only the platform makes a device do something. A user can follow the commands of their own
/// devices, and a device that is someone else's does not show that it exists.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class CommandAuthTests : IDisposable
{
    private const string V2 = "/api/pulse/v2";

    private readonly WebApplicationFactory<Program> _host;
    private readonly HttpClient _client;

    public CommandAuthTests(PipelineFixture stack)
    {
        _host = CreateHost(stack);
        _client = _host.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<string> PotOwnedBy(string owner)
    {
        var id = $"cau-{Guid.NewGuid():N}"[..14];
        var service = ServiceToken();
        using var registered = await Send(HttpMethod.Post, $"{V2}/devices", service, new
        {
            id,
            model = "greenhaus-pot-v1",
            capabilities = new
            {
                channels = new[] { new { id = "soil-1", metric = "soil_moisture" } },
                actuators = new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 20, minIntervalSeconds = 600, dailyCapSeconds = 120 } } },
            },
            credentialHash = "h",
            claimTokenHash = ClaimTokens.Hash("t"),
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        using var shipped = await Send(HttpMethod.Post, $"{V2}/devices/{id}/ship", service);
        using var claimed = await Send(HttpMethod.Post, $"{V2}/devices/{id}/claim", service, new { token = "t", ownerId = owner });
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        return id;
    }

    private static object Run(Guid id) => new
    {
        commandId = id,
        type = "run_actuator",
        actuatorId = "pump-1",
        durationSeconds = 10,
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
    };

    [Fact]
    public async Task Only_a_service_caller_can_send_a_command_not_even_the_owner()
    {
        var alice = $"alice-{Guid.NewGuid():N}";
        var pot = await PotOwnedBy(alice);
        var body = Run(Guid.NewGuid());

        using var anonymous = await Send(HttpMethod.Post, $"{V2}/devices/{pot}/commands", null, body);
        using var owner = await Send(HttpMethod.Post, $"{V2}/devices/{pot}/commands", Token(alice), body);
        using var service = await Send(HttpMethod.Post, $"{V2}/devices/{pot}/commands", ServiceToken(), body);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, service.StatusCode);
    }

    [Fact]
    public async Task An_owner_follows_the_commands_of_their_own_device_and_nobody_elses()
    {
        var alice = $"alice-{Guid.NewGuid():N}";
        var bob = $"bob-{Guid.NewGuid():N}";
        var alicePot = await PotOwnedBy(alice);
        var bobPot = await PotOwnedBy(bob);
        var command = Guid.NewGuid();
        using var queued = await Send(HttpMethod.Post, $"{V2}/devices/{alicePot}/commands", ServiceToken(), Run(command));

        using var list = await Send(HttpMethod.Get, $"{V2}/devices/{alicePot}/commands", Token(alice));
        using var one = await Send(HttpMethod.Get, $"{V2}/devices/{alicePot}/commands/{command}", Token(alice));
        using var theirList = await Send(HttpMethod.Get, $"{V2}/devices/{alicePot}/commands", Token(bob));
        using var theirOne = await Send(HttpMethod.Get, $"{V2}/devices/{alicePot}/commands/{command}", Token(bob));
        using var ownOneOfBob = await Send(HttpMethod.Get, $"{V2}/devices/{bobPot}/commands/{command}", Token(bob));
        using var asService = await Send(HttpMethod.Get, $"{V2}/devices/{alicePot}/commands/{command}", ServiceToken());

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement[]>())!);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, theirList.StatusCode); // not "forbidden": the device is not revealed
        Assert.Equal(HttpStatusCode.NotFound, theirOne.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ownOneOfBob.StatusCode); // a command is found under its own device only
        Assert.Equal(HttpStatusCode.OK, asService.StatusCode);
    }
}
