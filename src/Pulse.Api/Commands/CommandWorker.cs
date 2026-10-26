using Microsoft.Extensions.Options;

namespace Pulse.Api.Commands;

/// <summary>Delivers queued commands to the device link and ends the ones that ran out of time. Safe to run in every instance: rows are locked while they are handled.</summary>
public sealed class CommandWorker(CommandStore store, ICommandSender sender, IOptions<CommandsOptions> options, ILogger<CommandWorker> logger) : BackgroundService
{
    /// <summary>One round. Expiring comes first, so a command that is out of time is never handed over.</summary>
    public async Task<(int Sent, int Expired)> RunOnceAsync(CancellationToken ct)
    {
        var expired = await store.ExpireAsync(options.Value.AcceptedGrace, ct);
        var sent = sender.Available ? await store.DeliverAsync(sender, logger, ct) : 0;
        return (sent, expired);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "A round of the command worker failed, trying again");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
