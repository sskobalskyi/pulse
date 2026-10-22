using Microsoft.AspNetCore.Diagnostics;

namespace Pulse.Api.Endpoints;

/// <summary>
/// A request the framework could not read (a body that is not JSON, a value of the wrong type) is the caller's mistake,
/// a 400, not a server error. Without this the exception handler turns it into a 500.
/// Anything else is left to the default handler, which answers 500.
/// </summary>
public sealed class ProblemExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException bad) return false;

        httpContext.Response.StatusCode = bad.StatusCode;
        await ApiProblems.Problem(bad.StatusCode, ApiProblems.CodeFor(bad.StatusCode), "The request could not be read.", "The body or a parameter is missing or has the wrong shape.")
            .ExecuteAsync(httpContext);
        return true;
    }
}
