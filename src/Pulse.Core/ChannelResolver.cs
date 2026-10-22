namespace Pulse.Core;

/// <summary>A channel a registered device declared: its id and the metric it measures.</summary>
public sealed record ChannelRef(string Id, string Metric);

/// <param name="Code">A stable code a caller can switch on: unknown_channel, ambiguous_channel, metric_mismatch.</param>
public sealed record ReadingProblem(string Code, string Message);

/// <summary>
/// Decides which channel each incoming reading belongs to. A device that was registered declared its channels, so its
/// readings must fit them. A device nobody registered declared nothing, so its readings are taken as they are, which is
/// how the standalone demo's simulator works.
/// </summary>
public static class ChannelResolver
{
    /// <param name="registered">The declared channels of the registered devices among the senders. Devices missing from it are unregistered.</param>
    /// <returns>
    /// Problems is empty when every reading fits. Then Readings has the same readings in the same order, with the channel
    /// filled in for registered devices.
    /// </returns>
    public static (IReadOnlyList<TelemetryReading> Readings, IReadOnlyList<ReadingProblem> Problems) Resolve(
        IReadOnlyList<TelemetryReading> readings,
        IReadOnlyDictionary<string, IReadOnlyList<ChannelRef>> registered)
    {
        var resolved = new List<TelemetryReading>(readings.Count);
        var problems = new List<ReadingProblem>();
        // One message per kind of mistake per device is enough, a thousand identical lines help nobody.
        var reported = new HashSet<(string, string, string?)>();

        void Report(string code, string message, TelemetryReading reading)
        {
            if (reported.Add((code, reading.DeviceId, reading.Channel ?? reading.Metric))) problems.Add(new ReadingProblem(code, message));
        }

        foreach (var reading in readings)
        {
            if (!registered.TryGetValue(reading.DeviceId, out var channels))
            {
                resolved.Add(reading);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(reading.Channel))
            {
                var declared = channels.FirstOrDefault(c => c.Id == reading.Channel);
                if (declared is null)
                    Report("unknown_channel", $"Device '{reading.DeviceId}' has no channel '{reading.Channel}'.", reading);
                else if (declared.Metric != reading.Metric)
                    Report("metric_mismatch", $"Channel '{reading.Channel}' of '{reading.DeviceId}' measures {declared.Metric}, not {reading.Metric}.", reading);
                else
                    resolved.Add(reading);
                continue;
            }

            var candidates = channels.Where(c => c.Metric == reading.Metric).ToList();
            switch (candidates.Count)
            {
                case 1:
                    resolved.Add(reading with { Channel = candidates[0].Id });
                    break;
                case 0:
                    Report("unknown_channel", $"Device '{reading.DeviceId}' has no channel for {reading.Metric}.", reading);
                    break;
                default:
                    Report("ambiguous_channel",
                        $"Device '{reading.DeviceId}' has {candidates.Count} {reading.Metric} channels ({string.Join(", ", candidates.Select(c => c.Id))}), so the reading must name one.",
                        reading);
                    break;
            }
        }

        return (resolved, problems);
    }
}
