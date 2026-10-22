namespace Pulse.Api.Endpoints;

/// <summary>
/// Errors of the v2 API are <c>application/problem+json</c> with a stable <c>code</c> that clients can switch on,
/// as the contract's Problem schema requires. The title and detail are for people and may change.
/// </summary>
public static class ApiProblems
{
    public static IResult Problem(int status, string code, string title, string? detail = null) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult NotFound(string what = "device") =>
        Problem(StatusCodes.Status404NotFound, "not_found", $"The {what} was not found.");

    public static IResult Invalid(IEnumerable<string> errors) =>
        Problem(StatusCodes.Status400BadRequest, "validation_failed", "The request is invalid.", string.Join("; ", errors));

    /// <summary>The status a framework-generated problem (a malformed body, a thrown exception) gets, as a stable code.</summary>
    public static string CodeFor(int? status) => status switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        >= 500 => "internal_error",
        _ => "error",
    };
}
