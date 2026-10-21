using Pulse.Core;

namespace Pulse.Tests;

public class DeviceLifecycleTests
{
    [Theory]
    [InlineData(Lifecycle.Allocated, true)]
    [InlineData(Lifecycle.Shipped, false)]
    [InlineData(Lifecycle.Claimed, false)]
    [InlineData(Lifecycle.Active, false)]
    [InlineData(Lifecycle.Retired, false)]
    public void Only_an_allocated_device_can_be_shipped(Lifecycle state, bool expected) =>
        Assert.Equal(expected, DeviceLifecycle.CanShip(state));

    [Theory]
    [InlineData(Lifecycle.Allocated, false)]
    [InlineData(Lifecycle.Shipped, true)]
    [InlineData(Lifecycle.Claimed, false)]
    [InlineData(Lifecycle.Active, false)]
    [InlineData(Lifecycle.Retired, false)]
    public void Only_a_shipped_device_can_be_claimed(Lifecycle state, bool expected) =>
        Assert.Equal(expected, DeviceLifecycle.CanClaim(state));

    [Theory]
    [InlineData(Lifecycle.Allocated, true)]
    [InlineData(Lifecycle.Shipped, true)]
    [InlineData(Lifecycle.Claimed, true)]
    [InlineData(Lifecycle.Active, true)]
    [InlineData(Lifecycle.Retired, false)]
    public void Any_device_that_is_not_retired_can_be_retired(Lifecycle state, bool expected) =>
        Assert.Equal(expected, DeviceLifecycle.CanRetire(state));

    [Theory]
    [InlineData(Lifecycle.Claimed, true)]
    [InlineData(Lifecycle.Active, false)]
    [InlineData(Lifecycle.Shipped, false)]
    [InlineData(Lifecycle.Retired, false)]
    public void Only_a_claimed_device_becomes_active_on_its_first_reading(Lifecycle state, bool expected) =>
        Assert.Equal(expected, DeviceLifecycle.ActivatesOnReading(state));

    [Fact]
    public void The_wire_format_is_lower_case_and_round_trips()
    {
        foreach (var state in Enum.GetValues<Lifecycle>())
        {
            Assert.Equal(state.ToString().ToLowerInvariant(), state.ToWire());
            Assert.Equal(state, DeviceLifecycle.Parse(state.ToWire()));
        }
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("7")]
    public void Unknown_states_are_not_parsed(string? wire) =>
        Assert.False(DeviceLifecycle.TryParse(wire, out _));
}

public class ClaimTokensTests
{
    [Fact]
    public void The_right_token_matches_its_hash() =>
        Assert.True(ClaimTokens.Matches("ghx-3f9a-7c21", ClaimTokens.Hash("ghx-3f9a-7c21")));

    [Fact]
    public void A_different_token_does_not() =>
        Assert.False(ClaimTokens.Matches("ghx-3f9a-7c22", ClaimTokens.Hash("ghx-3f9a-7c21")));

    [Fact]
    public void The_hash_may_be_prefixed_and_in_any_case()
    {
        var hash = ClaimTokens.Hash("secret");

        Assert.True(ClaimTokens.Matches("secret", "sha256:" + hash));
        Assert.True(ClaimTokens.Matches("secret", "SHA256:" + hash.ToUpperInvariant()));
        Assert.True(ClaimTokens.Matches("secret", "  " + hash + "  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("abcd")]
    [InlineData("sha256:")]
    public void A_malformed_stored_hash_never_matches(string stored) =>
        Assert.False(ClaimTokens.Matches("secret", stored));

    [Fact]
    public void An_empty_token_never_matches() =>
        Assert.False(ClaimTokens.Matches("", ClaimTokens.Hash("")));

    [Fact]
    public void The_hash_is_the_well_known_sha256() =>
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", ClaimTokens.Hash("hello"));
}

public class RegistrationValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static DeviceRegistration Valid() => new(
        "ghx-demo-0001",
        "greenhaus-pot-v1",
        new Capabilities(
            [new Channel("soil-1", "soil_moisture", "Zone 1"), new Channel("air-t", "temperature")],
            [new ActuatorCapability("pump-1", "pump", new HardLimits(20, 600, 120))]),
        "pbkdf2$salt$hash",
        ClaimTokens.Hash("token"),
        Now.AddDays(30));

    [Fact]
    public void A_complete_registration_is_valid() =>
        Assert.Empty(RegistrationValidator.Validate(Valid(), Now));

    [Fact]
    public void A_device_with_no_channels_and_no_actuators_is_valid() =>
        Assert.Empty(RegistrationValidator.Validate(Valid() with { Capabilities = new Capabilities([], []) }, Now));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("slash/not/allowed")]
    [InlineData("wild+card")]
    [InlineData("hash#")]
    public void Ids_that_would_break_topics_or_urls_are_rejected(string id) =>
        Assert.NotEmpty(RegistrationValidator.Validate(Valid() with { Id = id }, Now));

    [Fact]
    public void An_overlong_id_is_rejected() =>
        Assert.Contains(RegistrationValidator.Validate(Valid() with { Id = new string('a', 65) }, Now), e => e.Contains("longer than"));

    [Fact]
    public void An_unknown_metric_is_rejected() =>
        Assert.Contains(
            RegistrationValidator.Validate(Valid() with { Capabilities = new Capabilities([new Channel("c", "humidity")], []) }, Now),
            e => e.Contains("unknown metric 'humidity'"));

    [Fact]
    public void Every_documented_metric_is_accepted()
    {
        foreach (var metric in new[] { "temperature", "air_humidity", "soil_moisture", "ph", "light", "airflow", "tank_level", "overflow", "flow" })
        {
            var capabilities = new Capabilities([new Channel("c", metric)], []);
            Assert.Empty(RegistrationValidator.Validate(Valid() with { Capabilities = capabilities }, Now));
        }
    }

    [Fact]
    public void Duplicate_channel_ids_are_rejected() =>
        Assert.Contains(
            RegistrationValidator.Validate(Valid() with { Capabilities = new Capabilities([new Channel("c", "ph"), new Channel("c", "light")], []) }, Now),
            e => e.Contains("used more than once"));

    [Fact]
    public void Two_channels_of_one_metric_are_fine_when_their_ids_differ() =>
        Assert.Empty(RegistrationValidator.Validate(
            Valid() with { Capabilities = new Capabilities([new Channel("soil-1", "soil_moisture"), new Channel("soil-2", "soil_moisture")], []) }, Now));

    [Fact]
    public void An_unknown_actuator_kind_is_rejected() =>
        Assert.Contains(
            RegistrationValidator.Validate(
                Valid() with { Capabilities = new Capabilities([], [new ActuatorCapability("x", "laser", new HardLimits(1, 0, 1))]) }, Now),
            e => e.Contains("unknown kind 'laser'"));

    [Theory]
    [InlineData(0, 0, 10, "maxRunSeconds must be at least 1")]
    [InlineData(5, -1, 10, "minIntervalSeconds cannot be negative")]
    [InlineData(5, 0, 0, "dailyCapSeconds must be at least 1")]
    [InlineData(50, 0, 10, "cannot exceed dailyCapSeconds")]
    public void Hard_limits_must_make_sense(int run, int interval, int daily, string message) =>
        Assert.Contains(
            RegistrationValidator.Validate(
                Valid() with { Capabilities = new Capabilities([], [new ActuatorCapability("pump-1", "pump", new HardLimits(run, interval, daily))]) }, Now),
            e => e.Contains(message));

    [Fact]
    public void A_claim_token_hash_that_is_not_sha256_hex_is_rejected() =>
        Assert.Contains(RegistrationValidator.Validate(Valid() with { ClaimTokenHash = "plain-token" }, Now), e => e.Contains("claimTokenHash"));

    [Fact]
    public void A_claim_token_that_has_already_expired_is_rejected() =>
        Assert.Contains(RegistrationValidator.Validate(Valid() with { ClaimTokenExpiresAt = Now }, Now), e => e.Contains("in the future"));

    [Fact]
    public void A_missing_credential_hash_is_rejected() =>
        Assert.Contains(RegistrationValidator.Validate(Valid() with { CredentialHash = "" }, Now), e => e.Contains("credentialHash"));

    [Fact]
    public void All_problems_are_reported_together()
    {
        var errors = RegistrationValidator.Validate(
            Valid() with { Id = "", Model = "", CredentialHash = "", ClaimTokenHash = "x", ClaimTokenExpiresAt = Now.AddDays(-1) }, Now);

        Assert.True(errors.Count >= 5, string.Join("; ", errors));
    }
}
