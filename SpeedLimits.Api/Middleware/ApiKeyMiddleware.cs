namespace SpeedLimits.Api.Middleware;

/// <summary>
/// Rejects requests that do not carry a valid X-Api-Key header.
/// The expected key is read from the SPEEDLIMITS_API_KEY environment variable.
/// Swagger UI paths (/swagger/**) are excluded so the docs remain accessible.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const string EnvVarName   = "SPEEDLIMITS_API_KEY";

    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        // Swagger UI / spec endpoint — allow through without a key
        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var provided)
            || string.IsNullOrWhiteSpace(provided))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = $"Missing '{ApiKeyHeader}' header." });
            return;
        }

        var expected = Environment.GetEnvironmentVariable(EnvVarName);
        if (string.IsNullOrEmpty(expected))
        {
            // Server is misconfigured — don't leak details, just reject
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "API key not configured on server." });
            return;
        }

        if (!string.Equals(provided, expected, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API key." });
            return;
        }

        await _next(context);
    }
}
