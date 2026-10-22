using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Endpoints;

namespace Pulse.Tests;

/// <summary>The claim limiter on its own, in a tiny host: no database, no broker, and limits small enough to hit.</summary>
public sealed class ClaimRateLimitingTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Registry:Claim:PerDevicePerMinute"] = "2",
            ["Registry:Claim:PerCallerPerMinute"] = "5",
        });
        builder.Services.AddClaimRateLimiting();

        _app = builder.Build();
        _app.UseRateLimiter();
        _app.MapPost("/devices/{deviceId}/claim", () => Results.Ok()).WithMetadata(new ClaimRateLimited());
        _app.MapPost("/devices/{deviceId}/ship", () => Results.Ok());
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> Claim(string device) => _client.PostAsync($"/devices/{device}/claim", null);

    [Fact]
    public async Task One_device_allows_a_few_attempts_a_minute_and_then_says_to_wait()
    {
        using var first = await Claim("dev-1");
        using var second = await Claim("dev-1");
        using var third = await Claim("dev-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter?.Delta);
        Assert.InRange(third.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task The_refusal_is_a_problem_with_a_stable_code()
    {
        await Claim("dev-1");
        await Claim("dev-1");

        using var refused = await Claim("dev-1");
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Limiting_one_device_does_not_limit_another()
    {
        await Claim("dev-1");
        await Claim("dev-1");

        using var other = await Claim("dev-2");

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task One_caller_cannot_sweep_through_many_devices()
    {
        for (var i = 0; i < 5; i++)
        {
            using var response = await Claim($"dev-{i}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var sixth = await Claim("dev-fresh");

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode); // each device is fresh, the caller is not
    }

    [Fact]
    public async Task Other_endpoints_are_never_limited()
    {
        for (var i = 0; i < 20; i++)
        {
            using var response = await _client.PostAsync("/devices/dev-1/ship", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
