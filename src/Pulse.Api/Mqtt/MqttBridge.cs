using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using Pulse.Api.Commands;
using Pulse.Core;

namespace Pulse.Api.Mqtt;

/// <summary>
/// The link between Pulse and the devices over MQTT. It listens to what devices publish (readings, status, answers to
/// commands, their manifest) and hands it to <see cref="DeviceMessageHandler"/>, and it is the <see cref="ICommandSender"/>
/// that puts a command on the device's topic. It reconnects on its own, and commands wait in the queue while it cannot.
/// </summary>
public sealed class MqttBridge(
    IOptions<MqttOptions> options, DeviceMessageHandler handler, TimeProvider time, ILogger<MqttBridge> logger)
    : BackgroundService, ICommandSender
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    public bool Available => _client.IsConnected;

    public async Task SendAsync(DeviceCommand command, CancellationToken ct)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("The MQTT bridge is not connected to the broker.");

        // A command that is late is of no use: tell the broker to drop it instead of handing it to a device that comes back
        // after it expired. The device checks expiresAt as well, this just saves the trip.
        var lifetime = (command.ExpiresAt - time.GetUtcNow()).TotalSeconds;
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(DeviceTopics.Commands(command.DeviceId))
            .WithPayload(DeviceMessages.CommandPayload(command))
            .WithContentType("application/json")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithMessageExpiryInterval((uint)Math.Clamp(Math.Ceiling(lifetime), 1, uint.MaxValue));
        if (Activity.Current?.Id is { } traceparent) message.WithUserProperty("traceparent", traceparent);

        var result = await _client.PublishAsync(message.Build(), ct);
        if (!result.IsSuccess) throw new InvalidOperationException($"The broker refused the command: {result.ReasonCode}.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                await handler.HandleAsync(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad message, or the database being away for a moment, must not take the bridge down. A reading lost
                // here is one among many, and a lost answer to a command ends that command as failed, never as completed.
                logger.LogError(ex, "Could not handle a message on {Topic}", e.ApplicationMessage.Topic);
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(stoppingToken);
                logger.LogInformation("Connected to the MQTT broker at {Host}:{Port}", options.Value.Host, options.Value.Port);

                // Wait until the connection is lost, then go round again.
                while (_client.IsConnected && !stoppingToken.IsCancellationRequested) await Task.Delay(500, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The MQTT broker at {Host}:{Port} is not reachable, trying again", options.Value.Host, options.Value.Port);
            }

            try
            {
                await Task.Delay(options.Value.ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_client.IsConnected) await _client.DisconnectAsync(cancellationToken: CancellationToken.None);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var o = options.Value;
        var persistent = !string.IsNullOrWhiteSpace(o.ClientId);

        var connect = new MqttClientOptionsBuilder()
            .WithTcpServer(o.Host, o.Port)
            .WithClientId(persistent ? o.ClientId : $"pulse-bridge-{Guid.NewGuid():N}")
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanStart(!persistent)
            .WithSessionExpiryInterval(persistent ? 3600u : 0u)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
        if (!string.IsNullOrEmpty(o.UserName)) connect.WithCredentials(o.UserName, o.Password);

        await _client.ConnectAsync(connect.Build(), ct);

        var subscribe = new MqttClientSubscribeOptionsBuilder();
        foreach (var filter in DeviceTopics.Filters)
            subscribe.WithTopicFilter($"$share/{o.SharedGroup}/{filter}", MqttQualityOfServiceLevel.AtLeastOnce);
        await _client.SubscribeAsync(subscribe.Build(), ct);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
