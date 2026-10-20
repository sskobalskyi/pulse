using System.Diagnostics;
using System.Text;
using Confluent.Kafka;

namespace Pulse.Api.Observability;

/// <summary>Carries the W3C trace context through Kafka message headers, so a trace can follow a reading past the broker.</summary>
public static class KafkaTraceContext
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";

    public static void Inject(Headers headers, Activity? activity)
    {
        if (activity?.Id is not { } traceParent) return;

        headers.Add(TraceParent, Encoding.UTF8.GetBytes(traceParent));
        if (!string.IsNullOrEmpty(activity.TraceStateString))
            headers.Add(TraceState, Encoding.UTF8.GetBytes(activity.TraceStateString));
    }

    public static bool TryExtract(Headers? headers, out ActivityContext context)
    {
        context = default;
        if (headers is null || !headers.TryGetLastBytes(TraceParent, out var parent)) return false;

        string? state = headers.TryGetLastBytes(TraceState, out var stateBytes) ? Encoding.UTF8.GetString(stateBytes) : null;
        return ActivityContext.TryParse(Encoding.UTF8.GetString(parent), state, isRemote: true, out context);
    }
}

/// <summary>
/// Collects the distinct producer spans behind a batch. A batch mixes messages from many requests, so the
/// consumer span links to them instead of picking one parent. Capped so a big batch cannot bloat the span.
/// </summary>
public sealed class BatchLinks(int max = 10)
{
    private readonly Dictionary<ActivityTraceId, ActivityContext> _contexts = [];

    public void Add(Headers? headers)
    {
        if (_contexts.Count < max && KafkaTraceContext.TryExtract(headers, out var context))
            _contexts.TryAdd(context.TraceId, context);
    }

    public IEnumerable<ActivityLink> ToLinks() => _contexts.Values.Select(c => new ActivityLink(c));

    public void Clear() => _contexts.Clear();
}
