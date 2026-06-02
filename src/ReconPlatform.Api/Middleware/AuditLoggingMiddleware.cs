using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Api.Middleware;

/// <summary>
/// Logs every HTTP request with structured fields for SOC2 compliance.
/// Actor, method, path, team claim, and client IP are recorded.
/// Request body is never logged.
/// </summary>
public sealed class AuditLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuditLoggingMiddleware> _logger;

    public AuditLoggingMiddleware(RequestDelegate next, ILogger<AuditLoggingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actor = context.User.Identity?.Name ?? "anonymous";
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? string.Empty;
        var teamClaim = context.Items.TryGetValue("team_claim", out var tc) ? tc as string ?? string.Empty : string.Empty;
        var ip = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                 ?? context.Connection.RemoteIpAddress?.ToString()
                 ?? "unknown";

        _logger.LogInformation(
            "Request: actor={Actor} method={Method} path={Path} team={TeamClaim} ip={Ip} timestamp={Timestamp}",
            actor, method, path, teamClaim, ip, DateTimeOffset.UtcNow);

        await _next(context).ConfigureAwait(false);
    }
}
