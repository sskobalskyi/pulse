using System.Text;
using System.Text.Json;
using Pulse.Core;

namespace Pulse.Tests;

public sealed class DeviceMqttTests
{
    private static readonly AsyncApiContract Contract = new(ContractPath());

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "device-mqtt.yaml");
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Theory]
    [InlineData("greenhaus/v1/devices/pot-1/manifest", "pot-1", DeviceTopicKind.Manifest, null)]
    [InlineData("greenhaus/v1/devices/pot-1/state", "pot-1", DeviceTopicKind.State, null)]
    [InlineData("greenhaus/v1/devices/pot-1/status", "pot-1", DeviceTopicKind.Status, null)]
    [InlineData("greenhaus/v1/devices/pot-1/telemetry/soil_moisture_z1", "pot-1", DeviceTopicKind.Telemetry, "soil_moisture_z1")]
    [InlineData("greenhaus/v1/devices/pot-1/commands/ack", "pot-1", DeviceTopicKind.CommandAck, null)]
    public void The_topics_of_the_contract_say_which_device_and_what(string topic, string device, DeviceTopicKind kind, string? channel)
    {
        Assert.Equal(new DeviceTopic(device, kind, channel), DeviceTopics.Parse(topic));
    }

    [Theory]
    [InlineData("greenhaus/v1/devices/pot-1/commands")] // what Pulse sends, not what a device says
    [InlineData("greenhaus/v1/devices/pot-1/telemetry")] // no channel
    [InlineData("greenhaus/v1/devices/pot-1/telemetry/")] // an empty channel
    [InlineData("greenhaus/v1/devices/pot-1/telemetry/a/b")] // too deep
    [InlineData("greenhaus/v1/devices//status")] // no device
    [InlineData("greenhaus/v2/devices/pot-1/status")] // another version of the contract
    [InlineData("other/v1/devices/pot-1/status")]
    [InlineData("greenhaus/v1/devices/pot-1/status/extra")]
    [InlineData("greenhaus/v1/devices/pot-1/commands/other")]
    [InlineData("")]
    public void A_topic_that_is_not_one_a_device_publishes_on_is_not_understood(string topic)
    {
        Assert.Null(DeviceTopics.Parse(topic));
    }

    [Fact]
    public void Every_filter_the_bridge_subscribes_to_is_a_topic_the_parser_understands()
    {
        foreach (var filter in DeviceTopics.Filters)
            Assert.NotNull(DeviceTopics.Parse(filter.Replace("+", "x")));
    }

    [Fact]
    public void A_reading_needs_a_time_and_a_finite_value_and_gets_the_metric_of_its_channel()
    {
        var message = DeviceMessages.Read<MqttReading>(Bytes("""{"ts":"2026-10-26T12:00:00Z","value":41.5,"raw":612,"seq":7}"""))!;

        var reading = DeviceMessages.ToReading("pot-1", "soil-1", "soil_moisture", message)!;

        Assert.Equal(("pot-1", "soil_moisture", 41.5, "soil-1"), (reading.DeviceId, reading.Metric, reading.Value, reading.Channel));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), reading.Timestamp);
        Assert.Null(DeviceMessages.ToReading("pot-1", "soil-1", "soil_moisture", DeviceMessages.Read<MqttReading>(Bytes("""{"value":1}"""))!));
        Assert.Null(DeviceMessages.ToReading("pot-1", "soil-1", "soil_moisture", DeviceMessages.Read<MqttReading>(Bytes("""{"ts":"2026-10-26T12:00:00Z"}"""))!));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{\"value\":\"high\",\"ts\":\"2026-10-26T12:00:00Z\"}")]
    [InlineData("{\"ts\":\"yesterday\",\"value\":1}")]
    public void A_payload_that_is_not_a_reading_is_nothing_and_never_an_exception(string payload)
    {
        Assert.Null(DeviceMessages.Read<MqttReading>(Bytes(payload)));
    }

    [Theory]
    [InlineData("online", true)]
    [InlineData("offline", false)]
    [InlineData("ONLINE", null)]
    [InlineData("away", null)]
    [InlineData(null, null)]
    public void Only_online_and_offline_are_a_status(string? status, bool? expected)
    {
        Assert.Equal(expected, DeviceMessages.IsOnline(new MqttStatus(status, null)));
    }

    [Theory]
    [InlineData("accepted", true, CommandStatus.Accepted)]
    [InlineData("completed", true, CommandStatus.Completed)]
    [InlineData("rejected", true, CommandStatus.Rejected)]
    [InlineData("failed", true, CommandStatus.Failed)]
    [InlineData("expired", true, CommandStatus.Expired)]
    [InlineData("queued", false, default(CommandStatus))] // only Pulse says queued and sent
    [InlineData("sent", false, default(CommandStatus))]
    [InlineData("done", false, default(CommandStatus))]
    [InlineData(null, false, default(CommandStatus))]
    public void A_device_may_report_accepted_or_an_ending_and_nothing_else(string? status, bool ok, CommandStatus expected)
    {
        var id = Guid.NewGuid();

        Assert.Equal(ok, DeviceMessages.TryAck(new MqttAck(id, status, null, null), out var commandId, out var parsed));
        if (ok) Assert.Equal((id, expected), (commandId, parsed));
    }

    [Fact]
    public void An_answer_without_a_command_id_is_not_an_answer()
    {
        Assert.False(DeviceMessages.TryAck(new MqttAck(null, "completed", null, null), out _, out _));
        Assert.False(DeviceMessages.TryAck(new MqttAck(Guid.Empty, "completed", null, null), out _, out _));
    }

    [Fact]
    public void The_command_a_device_receives_fits_the_contract()
    {
        var run = new DeviceCommand(Guid.NewGuid(), "pot-1", CommandTypes.RunActuator, "pump-1", 10, null, DateTimeOffset.UtcNow.AddMinutes(2));
        var kill = new DeviceCommand(Guid.NewGuid(), "pot-1", CommandTypes.SetKillSwitch, "pump-1", null, true, DateTimeOffset.UtcNow.AddMinutes(2));
        var stop = new DeviceCommand(Guid.NewGuid(), "pot-1", CommandTypes.StopActuator, "pump-1", null, null, DateTimeOffset.UtcNow.AddMinutes(2));

        foreach (var command in new[] { run, kill, stop })
        {
            var payload = JsonDocument.Parse(DeviceMessages.CommandPayload(command)).RootElement;
            Assert.Empty(Contract.ValidatePayload("command", payload));
        }

        // What does not apply is left out, not sent as null.
        var sent = JsonDocument.Parse(DeviceMessages.CommandPayload(stop)).RootElement;
        Assert.False(sent.TryGetProperty("durationSeconds", out _));
        Assert.False(sent.TryGetProperty("enabled", out _));
        Assert.Equal("stop_actuator", sent.GetProperty("type").GetString());
    }

    [Fact]
    public void The_messages_a_device_sends_in_these_tests_fit_the_contract_so_the_tests_mean_something()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Empty(Contract.ValidatePayload("reading", JsonSerializer.SerializeToElement(new { ts = now, value = 41.5, raw = 612, seq = 7 })));
        Assert.Empty(Contract.ValidatePayload("status", JsonSerializer.SerializeToElement(new { status = "online", ts = now })));
        Assert.Empty(Contract.ValidatePayload("ack", JsonSerializer.SerializeToElement(new { commandId = Guid.NewGuid(), status = "rejected", reason = "min_interval", ts = now })));
        Assert.NotEmpty(Contract.ValidatePayload("ack", JsonSerializer.SerializeToElement(new { commandId = Guid.NewGuid(), status = "queued", ts = now })));
    }
}
