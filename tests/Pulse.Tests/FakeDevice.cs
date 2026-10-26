using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>A device on the broker, as far as Pulse can tell: it publishes on its own topics and listens for its commands.</summary>
public sealed class FakeDevice(string deviceId, PipelineFixture stack) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly ConcurrentQueue<MqttApplicationMessage> _received = new();

    // Once, however many times the device connects: a second handler would hear every message twice.
    private bool _listening;

    public string Id => deviceId;

    /// <summary>The commands that reached the device, as it parsed them.</summary>
    public IReadOnlyList<JsonElement> Commands =>
        [.. _received.Where(m => m.Topic == DeviceTopics.Commands(deviceId)).Select(m => JsonDocument.Parse(Encoding.UTF8.GetString(m.Payload.ToArray())).RootElement.Clone())];

    public IReadOnlyList<MqttApplicationMessage> Messages => [.. _received];

    /// <param name="persistent">Keep the session while away, so a command sent meanwhile is waiting when the device is back.</param>
    /// <param name="withWill">The broker publishes "offline" on the status topic if the connection is lost without a goodbye.</param>
    public async Task ConnectAsync(bool persistent = false, bool withWill = false)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(stack.MqttHost, stack.MqttPort)
            .WithClientId(deviceId)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(!persistent)
            .WithSessionExpiryInterval(persistent ? 600u : 0u);
        if (withWill)
        {
            options.WithWillTopic($"{DeviceTopics.Prefix}/{deviceId}/status")
                .WithWillPayload(Json(new { status = "offline", ts = DateTimeOffset.UtcNow }))
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        }

        if (!_listening)
        {
            _client.ApplicationMessageReceivedAsync += e =>
            {
                _received.Enqueue(e.ApplicationMessage);
                return Task.CompletedTask;
            };
            _listening = true;
        }

        await _client.ConnectAsync(options.Build());
        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(DeviceTopics.Commands(deviceId), MqttQualityOfServiceLevel.AtLeastOnce).Build());
    }

    public Task PublishAsync(string suffix, object payload) => PublishRawAsync(suffix, Json(payload));

    public async Task PublishRawAsync(string suffix, byte[] payload) =>
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic($"{DeviceTopics.Prefix}/{deviceId}/{suffix}")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

    public Task Online() => PublishAsync("status", new { status = "online", ts = DateTimeOffset.UtcNow });

    public Task Reading(string channel, double value, DateTimeOffset? at = null) =>
        PublishAsync($"telemetry/{channel}", new { ts = at ?? DateTimeOffset.UtcNow, value });

    public Task Ack(Guid commandId, string status, string? reason = null) =>
        PublishAsync("commands/ack", new { commandId, status, reason, ts = DateTimeOffset.UtcNow });

    /// <summary>Goes away without saying goodbye, which is when the broker publishes the will.</summary>
    public async Task LoseConnectionAsync() =>
        await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().WithReason(MqttClientDisconnectOptionsReason.DisconnectWithWillMessage).Build());

    public async Task DisconnectAsync()
    {
        if (_client.IsConnected) await _client.DisconnectAsync();
    }

    private static byte[] Json(object payload) => JsonSerializer.SerializeToUtf8Bytes(payload, Web);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _client.Dispose();
    }
}
