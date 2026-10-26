using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pulse.Api.Mqtt;
using Pulse.Api.Registry;
using Pulse.Core;

namespace Pulse.Tests;

public sealed class DeviceCredentialsTests
{
    private static readonly byte[] Salt = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODw==");

    [Fact]
    public void The_password_a_device_was_given_verifies_and_another_one_does_not()
    {
        var stored = DeviceCredentials.Create("s3cret-credential", 1000, Salt);

        Assert.True(DeviceCredentials.Verify("s3cret-credential", stored));
        Assert.False(DeviceCredentials.Verify("s3cret-credentiaL", stored));
        Assert.False(DeviceCredentials.Verify("", stored));
    }

    [Fact]
    public void The_format_is_the_one_the_allocation_writes_so_what_it_stores_can_be_checked_here()
    {
        // The same construction as Allocator.CredentialHash in the Greenhaus fulfilment service: PBKDF2-HMAC-SHA256 of the
        // UTF-8 password, a 16 byte salt, 32 byte hash, both in base64, joined with the iteration count.
        var hash = Rfc2898DeriveBytes.Pbkdf2("Zm9vYmFyLWJheg-_0", Salt, 100_000, HashAlgorithmName.SHA256, 32);
        var theirs = $"pbkdf2_sha256$100000${Convert.ToBase64String(Salt)}${Convert.ToBase64String(hash)}";

        Assert.True(DeviceCredentials.Verify("Zm9vYmFyLWJheg-_0", theirs));
        Assert.Equal(theirs, DeviceCredentials.Create("Zm9vYmFyLWJheg-_0", 100_000, Salt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2_sha256$100000$salt")] // too few parts
    [InlineData("pbkdf2_sha256$100000$AAAA$BBBB$extra")]
    [InlineData("md5$1000$AAAA$BBBB")] // another scheme
    [InlineData("pbkdf2_sha256$many$AAAA$BBBB")]
    [InlineData("pbkdf2_sha256$0$AAAA$BBBB")]
    [InlineData("pbkdf2_sha256$99999999$AAAA$BBBB")] // more work than any hash needs
    [InlineData("pbkdf2_sha256$1000$not base64!$BBBB")]
    [InlineData("pbkdf2_sha256$1000$AAAA$")] // an empty hash would match anything
    public void A_stored_value_that_is_not_a_credential_hash_verifies_nothing(string? stored)
    {
        Assert.False(DeviceCredentials.Verify("anything", stored));
        Assert.False(DeviceCredentials.Verify("", stored));
    }

    [Theory]
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-1/manifest", true)]
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-1/telemetry/soil-1", true)]
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-1/state", true)]
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-1/status", true)] // also what the will is published on
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-1/commands/ack", true)]
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-1/commands", false)] // Pulse commands the device, not the other way round
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-2/telemetry/soil-1", false)] // someone else's
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/+/status", false)]
    [InlineData("pot-1", "publish", "greenhaus/v1/devices/pot-1/#", false)]
    [InlineData("pot-1", "publish", "other/topic", false)]
    [InlineData("pot-1", "publish", "$SYS/brokers", false)]
    [InlineData("pot-1", "subscribe", "greenhaus/v1/devices/pot-1/commands", true)]
    [InlineData("pot-1", "subscribe", "greenhaus/v1/devices/pot-2/commands", false)]
    [InlineData("pot-1", "subscribe", "greenhaus/v1/devices/+/commands", false)] // it must not hear other devices' commands
    [InlineData("pot-1", "subscribe", "greenhaus/v1/devices/#", false)]
    [InlineData("pot-1", "subscribe", "#", false)]
    [InlineData("pot-1", "subscribe", "greenhaus/v1/devices/pot-1/telemetry/soil-1", false)]
    [InlineData("pot-1", "subscribe", "$share/g/greenhaus/v1/devices/pot-1/commands", false)]
    [InlineData("pot-1", "delete", "greenhaus/v1/devices/pot-1/status", false)]
    public void A_device_speaks_on_its_own_topics_and_hears_its_own_commands_and_nothing_else(string device, string action, string topic, bool allowed)
    {
        Assert.Equal(allowed, DeviceAccess.Allows(device, action, topic));
    }
}

/// <summary>The questions the broker asks Pulse, over HTTP, with the secret it was configured with.</summary>
[Collection(PipelineCollection.Name)]
public sealed class BrokerAuthTests(PipelineFixture stack)
{
    private const string V2 = "/api/pulse/v2";

    private static string NewId() => $"ba-{Guid.NewGuid():N}"[..14];

    private async Task<string> Pot(string password)
    {
        var id = NewId();
        using var response = await stack.Client.PostAsJsonAsync($"{V2}/devices", new
        {
            id,
            model = "greenhaus-pot-v1",
            capabilities = new { channels = new[] { new { id = "soil-1", metric = "soil_moisture" } }, actuators = Array.Empty<object>() },
            credentialHash = DeviceCredentials.Create(password, 1000, RandomNumberGenerator.GetBytes(16)),
            claimTokenHash = ClaimTokens.Hash("t"),
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private async Task<HttpResponseMessage> Ask(string path, object body, string? secret = PipelineFixture.BrokerSecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (secret is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return await stack.Client.SendAsync(request);
    }

    private async Task<(string Result, bool Superuser)> Authenticate(string? user, string? password)
    {
        using var response = await Ask("/mqtt/auth", new { username = user, password, clientid = "c" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("result").GetString()!, body.GetProperty("is_superuser").GetBoolean());
    }

    private async Task<string> Authorize(string user, string action, string topic)
    {
        using var response = await Ask("/mqtt/acl", new { username = user, action, topic });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString()!;
    }

    [Fact]
    public async Task A_device_with_its_credential_is_let_in_and_with_anything_else_is_not()
    {
        var pot = await Pot("the-real-password");

        Assert.Equal(("allow", false), await Authenticate(pot, "the-real-password"));
        Assert.Equal(("deny", false), await Authenticate(pot, "the-wrong-password"));
        Assert.Equal(("deny", false), await Authenticate(pot, ""));
        Assert.Equal(("deny", false), await Authenticate(pot, null));
        Assert.Equal(("deny", false), await Authenticate(null, "the-real-password"));
        Assert.Equal(("deny", false), await Authenticate("nobody", "the-real-password"));
        Assert.Equal(("deny", false), await Authenticate(NewId(), "the-real-password")); // a device nobody registered
    }

    [Fact]
    public async Task One_devices_credential_does_not_open_the_door_for_another()
    {
        var first = await Pot("password-of-first");
        var second = await Pot("password-of-second");

        Assert.Equal(("deny", false), await Authenticate(first, "password-of-second"));
        Assert.Equal(("allow", false), await Authenticate(second, "password-of-second"));
    }

    [Fact]
    public async Task A_retired_device_is_turned_away_even_with_the_right_password()
    {
        var pot = await Pot("the-real-password");
        Assert.Equal("allow", (await Authenticate(pot, "the-real-password")).Result);

        using var retire = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/retire", new { });

        Assert.Equal("deny", (await Authenticate(pot, "the-real-password")).Result);
    }

    [Fact]
    public async Task A_stored_value_in_another_format_lets_nobody_in()
    {
        var pot = await Pot("x");
        await using (var cmd = stack.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().CreateCommand("UPDATE devices SET credential_hash = 'plain-text' WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", pot);
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Equal("deny", (await Authenticate(pot, "plain-text")).Result);
        Assert.Equal("deny", (await Authenticate(pot, "x")).Result);
    }

    [Fact]
    public async Task The_bridges_own_account_is_let_in_as_a_superuser_and_only_with_its_password()
    {
        var access = new BrokerAccess(
            stack.Services.GetRequiredService<DeviceRegistry>(),
            Options.Create(new MqttOptions { UserName = "pulse-bridge", Password = "bridge-password" }));

        Assert.Equal(BrokerVerdict.Superuser, await access.AuthenticateAsync("pulse-bridge", "bridge-password", default));
        Assert.Equal(BrokerVerdict.Deny, await access.AuthenticateAsync("pulse-bridge", "bridge-passworD", default));
        Assert.Equal(BrokerVerdict.Deny, await access.AuthenticateAsync("pulse-bridge", "", default));

        // With no account configured, nobody gets in by naming it.
        var none = new BrokerAccess(stack.Services.GetRequiredService<DeviceRegistry>(), Options.Create(new MqttOptions()));
        Assert.Equal(BrokerVerdict.Deny, await none.AuthenticateAsync("", "", default));
        Assert.Equal(BrokerVerdict.Deny, await none.AuthenticateAsync("pulse-bridge", "bridge-password", default));
    }

    [Fact]
    public async Task What_a_device_may_do_is_decided_per_topic_and_action()
    {
        var pot = await Pot("pw");

        Assert.Equal("allow", await Authorize(pot, "publish", $"greenhaus/v1/devices/{pot}/telemetry/soil-1"));
        Assert.Equal("allow", await Authorize(pot, "subscribe", $"greenhaus/v1/devices/{pot}/commands"));
        Assert.Equal("deny", await Authorize(pot, "publish", $"greenhaus/v1/devices/{pot}/commands"));
        Assert.Equal("deny", await Authorize(pot, "publish", "greenhaus/v1/devices/someone-else/status"));
        Assert.Equal("deny", await Authorize(pot, "subscribe", "greenhaus/v1/devices/+/commands"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    [InlineData("")]
    public async Task Only_the_broker_may_ask(string? secret)
    {
        using var auth = await Ask("/mqtt/auth", new { username = "x", password = "y" }, secret);
        using var acl = await Ask("/mqtt/acl", new { username = "x", action = "publish", topic = "t" }, secret);

        Assert.Equal(HttpStatusCode.Unauthorized, auth.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, acl.StatusCode);
    }

    [Fact]
    public async Task A_request_that_is_not_what_the_broker_sends_is_a_denial_and_not_an_error()
    {
        using var empty = await stack.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/mqtt/auth")
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", PipelineFixture.BrokerSecret) },
        });

        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("deny", (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
    }
}
