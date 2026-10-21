using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Pulse.Api.Endpoints;

public sealed class ClaimLimitOptions
{
    /// <summary>Attempts per minute against one device, whoever makes them. Stops guessing a token.</summary>
    public int PerDevicePerMinute { get; set; } = 5;

    /// <summary>Attempts per minute from one caller, across devices. Stops sweeping through devices.</summary>
    public int PerCallerPerMinute { get; set; } = 30;
}

/// <summary>Marks the endpoints the claim limits apply to.</summary>
public sealed class ClaimRateLimited;

/// <summary>
/// A claim presents a token that someone may be guessing, so attempts are limited twice: per device and per caller.
/// Both count every attempt, successful or not, and both must have room. The counters live in memory of this
/// instance, so with several instances the effective limit is multiplied by their number.
/// </summary>
public static class ClaimRateLimiting
{
    public static IServiceCollection AddClaimRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<ClaimLimitOptions>().BindConfiguration("Registry:Claim");
        services.AddRateLimiter();
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<ClaimLimitOptions>>((limiter, limits) =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                Partitioned(limits.Value.PerDevicePerMinute, ctx => "device:" + ctx.Request.RouteValues["deviceId"]),
                Partitioned(limits.Value.PerCallerPerMinute, ctx => "caller:" + (ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown")));

            limiter.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

                await ApiProblems.Problem(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many claim attempts.", "Wait a moment before trying again.")
                    .ExecuteAsync(context.HttpContext);
            };
        });

        return services;
    }

    private static PartitionedRateLimiter<HttpContext> Partitioned(int permitsPerMinute, Func<HttpContext, string> key) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.GetEndpoint()?.Metadata.GetMetadata<ClaimRateLimited>() is null
                ? RateLimitPartition.GetNoLimiter("not-a-claim")
                : RateLimitPartition.GetFixedWindowLimiter(key(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
}
