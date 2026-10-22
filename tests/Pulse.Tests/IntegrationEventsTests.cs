using System.Text.Json;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>What Pulse publishes and consumes, checked against <c>contracts/integration-events.yaml</c> without a broker.</summary>
public class IntegrationEventsTests
{
    private static readonly AsyncApiContract Contract = new(ContractPath());
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AlertId = Guid.Parse("6f1f0d6e-3a52-4f0e-9a3a-3f1f5b7c9d11");

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "integration-events.yaml");
    }

    private static void AssertFits(string eventName, object payload)
    {
        var errors = Contract.ValidatePayload(eventName, JsonSerializer.SerializeToElement(payload, Web));
        Assert.True(errors.Count == 0, $"{eventName} does not fit the contract: {string.Join("; ", errors)}\n{JsonSerializer.Serialize(payload, Web)}");
    }

    [Fact]
    public void Every_event_name_pulse_uses_is_one_the_contract_defines()
    {
        foreach (var name in new[] { EventNames.AlertRaised, EventNames.AlertResolved, EventNames.DeviceStatus, EventNames.DeviceRetired, EventNames.DeviceAllocated })
            Assert.Contains(name, Contract.EventNames);
    }

    [Fact]
    public void The_exchange_and_schema_version_are_the_ones_the_contract_describes()
    {
        Assert.Equal("integration.events", EventNames.Exchange);
        Assert.Equal("1", EventNames.SchemaVersion);
    }

    // --- alerts ---

    [Fact]
    public void A_raised_threshold_alert_says_which_way_the_value_went()
    {
        var alert = new Alert("pot", "soil_moisture", "soil_moisture outside [40, 70]", 31, T0, AlertState.Raised, AlertKind.Threshold, "soil-1", "below");

        var (key, payload) = IntegrationEvents.ForAlert(alert, AlertId);

        Assert.Equal("alert.raised.v1", key);
        Assert.Equal(("raised", "threshold", "below", 31.0, "soil-1"), (payload.State, payload.Rule, payload.Direction, payload.Value, payload.ChannelId));
        Assert.Equal(AlertId.ToString(), payload.AlertId);
        AssertFits(key, payload);
    }

    [Fact]
    public void A_raised_alert_above_the_range_fits_the_contract()
    {
        var alert = new Alert("pot", "temperature", "temperature outside [-10, 80]", 95, T0, AlertState.Raised, AlertKind.Threshold, "air-t", "above");

        var (key, payload) = IntegrationEvents.ForAlert(alert, AlertId);

        Assert.Equal("above", payload.Direction);
        AssertFits(key, payload);
    }

    [Fact]
    public void A_resolved_alert_carries_the_same_alert_id_and_no_direction()
    {
        var raised = new Alert("pot", "soil_moisture", "r", 31, T0, AlertState.Raised, AlertKind.Threshold, "soil-1", "below");
        var resolved = new Alert("pot", "soil_moisture", "r", 55, T0.AddMinutes(5), AlertState.Resolved, AlertKind.Threshold, "soil-1");

        var (_, first) = IntegrationEvents.ForAlert(raised, AlertId);
        var (key, second) = IntegrationEvents.ForAlert(resolved, AlertId);

        Assert.Equal("alert.resolved.v1", key);
        Assert.Equal(first.AlertId, second.AlertId); // how a consumer ties the two together
        Assert.Null(second.Direction);
        Assert.Equal("resolved", second.State);
        AssertFits(key, second);
    }

    [Fact]
    public void A_no_data_alert_is_a_no_data_rule_with_no_value()
    {
        // The alert's own value is how many seconds the channel has been silent. That is not a reading, so the event leaves it out.
        var alert = new Alert("pot", "soil_moisture", "no data for 60s", 75.5, T0, AlertState.Raised, AlertKind.NoData, "soil-1");

        var (key, payload) = IntegrationEvents.ForAlert(alert, AlertId);

        Assert.Equal("no_data", payload.Rule);
        Assert.Null(payload.Value);
        Assert.Null(payload.Direction);
        AssertFits(key, payload);
    }

    [Fact]
    public void An_alert_on_a_channel_nobody_named_is_on_the_channel_called_after_its_metric()
    {
        var alert = new Alert("demo", "temperature", "r", 95, T0, AlertState.Raised, AlertKind.Threshold, Direction: "above");

        Assert.Equal("temperature", IntegrationEvents.ForAlert(alert, AlertId).Payload.ChannelId);
    }

    [Fact]
    public void Absent_fields_are_left_out_of_the_json_rather_than_written_as_null()
    {
        var alert = new Alert("pot", "soil_moisture", "no data for 60s", 75.5, T0, AlertState.Raised, AlertKind.NoData, "soil-1");

        var json = JsonSerializer.Serialize(IntegrationEvents.ForAlert(alert, AlertId).Payload, Web);

        Assert.DoesNotContain("direction", json);
        Assert.DoesNotContain("\"value\"", json);
    }

    // --- devices ---

    [Fact]
    public void A_retired_device_event_fits_the_contract() =>
        AssertFits(EventNames.DeviceRetired, IntegrationEvents.ForRetired("ghx-0001", T0));

    [Theory]
    [InlineData(true, "online", "reconnect")]
    [InlineData(false, "offline", "no_data")]
    [InlineData(false, "offline", "last_will")]
    public void A_status_change_fits_the_contract(bool online, string expected, string cause)
    {
        var payload = IntegrationEvents.ForStatus("ghx-0001", online, T0, cause);

        Assert.Equal(expected, payload.Status);
        AssertFits(EventNames.DeviceStatus, payload);
    }

    [Fact]
    public void The_contract_rejects_a_status_it_does_not_define()
    {
        var errors = Contract.ValidatePayload(EventNames.DeviceStatus, JsonSerializer.SerializeToElement(new { deviceId = "d", status = "sleeping", at = T0 }, Web));

        Assert.NotEmpty(errors); // the checker really checks
    }

    // --- device.allocated.v1, which Pulse consumes ---

    private static object AllocatedPayload(object? actuators = null) => new
    {
        deviceId = "ghx-demo-0001",
        orderId = Guid.NewGuid(),
        customerId = "user-1",
        sku = "pot-v1",
        model = "greenhaus-pot-v1",
        claimTokenHash = ClaimTokens.Hash("token"),
        claimTokenExpiresAt = T0.AddDays(30),
        credentialHash = "pbkdf2$salt$hash",
        capabilities = new
        {
            channels = new object[]
            {
                new { id = "soil-1", metric = "soil_moisture", label = "Zone 1", unit = "%" },
                new { id = "air-t", metric = "temperature" },
            },
            actuators = actuators ?? new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 20, minIntervalSeconds = 600, dailyCapSeconds = 120 } } },
        },
    };

    [Fact]
    public void A_device_allocated_message_that_fits_the_contract_becomes_a_registration_that_passes_validation()
    {
        var payload = JsonSerializer.SerializeToElement(AllocatedPayload(), Web);
        Assert.Empty(Contract.ValidatePayload(EventNames.DeviceAllocated, payload));

        var message = payload.Deserialize<DeviceAllocatedMessage>(Web)!;
        var registration = message.ToRegistration();

        Assert.Equal("ghx-demo-0001", registration.Id);
        Assert.Equal("greenhaus-pot-v1", registration.Model);
        Assert.Equal(["soil-1", "air-t"], registration.Capabilities.Channels.Select(c => c.Id));
        Assert.Equal("Zone 1", registration.Capabilities.Channels[0].Label);
        Assert.Equal(20, registration.Capabilities.Actuators.Single().HardLimits.MaxRunSeconds);
        Assert.Empty(RegistrationValidator.Validate(registration, T0));
    }

    [Fact]
    public void An_actuator_without_hard_limits_is_refused_by_the_contract_and_by_the_registry()
    {
        // The defect found while building the registry: the event once left the limits out, and then no device could be registered from it.
        var payload = JsonSerializer.SerializeToElement(AllocatedPayload(new[] { new { id = "pump-1", kind = "pump" } }), Web);

        Assert.NotEmpty(Contract.ValidatePayload(EventNames.DeviceAllocated, payload));
        var registration = payload.Deserialize<DeviceAllocatedMessage>(Web)!.ToRegistration();
        Assert.Contains(RegistrationValidator.Validate(registration, T0), e => e.Contains("hardLimits"));
    }

    [Fact]
    public void A_claim_token_hash_in_the_wrong_format_is_refused_by_both()
    {
        var bad = JsonSerializer.SerializeToElement(new
        {
            deviceId = "d", orderId = Guid.NewGuid(), customerId = "u", sku = "s", model = "m", claimTokenHash = "a-plain-token",
            claimTokenExpiresAt = T0.AddDays(1), credentialHash = "h", capabilities = new { channels = Array.Empty<object>(), actuators = Array.Empty<object>() },
        }, Web);

        Assert.NotEmpty(Contract.ValidatePayload(EventNames.DeviceAllocated, bad));
        Assert.Contains(RegistrationValidator.Validate(bad.Deserialize<DeviceAllocatedMessage>(Web)!.ToRegistration(), T0), e => e.Contains("claimTokenHash"));
    }
}
