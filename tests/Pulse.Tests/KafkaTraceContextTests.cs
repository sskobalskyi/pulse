using System.Diagnostics;
using Confluent.Kafka;
using Pulse.Api.Observability;

namespace Pulse.Tests;

public class KafkaTraceContextTests
{
    private static readonly ActivitySource Source = new("Pulse.Tests");

    static KafkaTraceContextTests()
    {
        // Activities are only created while something listens to the source.
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Pulse.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    /// <summary>Starts a span in a trace of its own, even when another test left an activity current.</summary>
    private static Activity StartRoot(string name, ActivityKind kind = ActivityKind.Producer)
    {
        Activity.Current = null;
        return Source.StartActivity(name, kind)!;
    }

    private static Headers HeadersFor(Activity activity)
    {
        var headers = new Headers();
        KafkaTraceContext.Inject(headers, activity);
        return headers;
    }

    [Fact]
    public void Injected_context_is_read_back_unchanged()
    {
        using var activity = Source.StartActivity("publish")!;

        Assert.True(KafkaTraceContext.TryExtract(HeadersFor(activity), out var context));

        Assert.Equal(activity.TraceId, context.TraceId);
        Assert.Equal(activity.SpanId, context.SpanId);
        Assert.True(context.IsRemote);
    }

    [Fact]
    public void No_activity_adds_no_headers()
    {
        var headers = new Headers();

        KafkaTraceContext.Inject(headers, null);

        Assert.Empty(headers);
    }

    [Fact]
    public void Missing_or_garbled_headers_are_not_a_context()
    {
        Assert.False(KafkaTraceContext.TryExtract(null, out _));
        Assert.False(KafkaTraceContext.TryExtract(new Headers(), out _));
        Assert.False(KafkaTraceContext.TryExtract(new Headers { { KafkaTraceContext.TraceParent, "not-a-traceparent"u8.ToArray() } }, out _));
    }

    [Fact]
    public void Batch_links_one_span_per_distinct_trace()
    {
        using var first = StartRoot("a");
        using var second = StartRoot("b");
        var links = new BatchLinks();

        links.Add(HeadersFor(first));
        links.Add(HeadersFor(first)); // another message from the same request
        links.Add(HeadersFor(second));
        links.Add(null);              // a message without trace headers

        Assert.Equivalent(
            new[] { first.TraceId.ToString(), second.TraceId.ToString() },
            links.ToLinks().Select(l => l.Context.TraceId.ToString()).ToArray());
    }

    [Fact]
    public void Batch_links_are_capped()
    {
        var links = new BatchLinks(max: 3);

        for (var i = 0; i < 10; i++)
        {
            using var activity = StartRoot("publish");
            links.Add(HeadersFor(activity));
        }

        Assert.Equal(3, links.ToLinks().Count());
    }

    [Fact]
    public void Clear_empties_the_batch()
    {
        using var activity = Source.StartActivity("publish")!;
        var links = new BatchLinks();
        links.Add(HeadersFor(activity));

        links.Clear();

        Assert.Empty(links.ToLinks());
    }
}
