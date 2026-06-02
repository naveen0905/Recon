using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using ReconPlatform.Api.Middleware;
using System.Security.Claims;
using Xunit;

namespace ReconPlatform.UnitTests.Api;

/// <summary>
/// Unit tests for <see cref="AuditLoggingMiddleware"/>.
/// Verifies that actor, method, path, team_claim, and IP are logged on every request.
/// </summary>
public class AuditMiddlewareTests
{
    // ── InvokeAsync logs expected fields ─────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_AuthenticatedRequest_LogsActorMethodPathTeamIp()
    {
        // Arrange
        var loggedMessages = new List<string>();
        var mockLogger = CreateCapturingLogger(loggedMessages);

        var middleware = new AuditLoggingMiddleware(
            _ => Task.CompletedTask,
            mockLogger);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice@contoso.com")], "test"));
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/recon/pull";
        httpContext.Items["team_claim"] = "net-sec";
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert
        loggedMessages.Should().ContainSingle();
        var msg = loggedMessages[0];
        msg.Should().Contain("alice@contoso.com");
        msg.Should().Contain("POST");
        msg.Should().Contain("/api/recon/pull");
        msg.Should().Contain("net-sec");
        msg.Should().Contain("10.0.0.1");
    }

    [Fact]
    public async Task InvokeAsync_AnonymousRequest_LogsAnonymousActor()
    {
        // Arrange
        var loggedMessages = new List<string>();
        var mockLogger = CreateCapturingLogger(loggedMessages);

        var middleware = new AuditLoggingMiddleware(
            _ => Task.CompletedTask,
            mockLogger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/recon/assets";

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert
        loggedMessages.Should().ContainSingle();
        loggedMessages[0].Should().Contain("anonymous");
    }

    [Fact]
    public async Task InvokeAsync_XForwardedForPresent_LogsForwardedIp()
    {
        // Arrange
        var loggedMessages = new List<string>();
        var mockLogger = CreateCapturingLogger(loggedMessages);

        var middleware = new AuditLoggingMiddleware(
            _ => Task.CompletedTask,
            mockLogger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/teams/x/sources";
        httpContext.Request.Headers["X-Forwarded-For"] = "203.0.113.5";

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert
        loggedMessages[0].Should().Contain("203.0.113.5");
    }

    [Fact]
    public async Task InvokeAsync_NoTeamClaim_LogsEmptyTeam()
    {
        // Arrange
        var loggedMessages = new List<string>();
        var mockLogger = CreateCapturingLogger(loggedMessages);

        var middleware = new AuditLoggingMiddleware(
            _ => Task.CompletedTask,
            mockLogger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/recon/assets";
        // No team_claim in Items

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert: no exception; log still produced
        loggedMessages.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeAsync_NullContext_ThrowsArgumentNull()
    {
        // Arrange
        var mockLogger = CreateCapturingLogger(new List<string>());
        var middleware = new AuditLoggingMiddleware(_ => Task.CompletedTask, mockLogger);

        // Act
        var act = async () => await middleware.InvokeAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullNext_ThrowsArgumentNull()
    {
        var mockLogger = CreateCapturingLogger(new List<string>());
        var act = () => new AuditLoggingMiddleware(null!, mockLogger);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNull()
    {
        var act = () => new AuditLoggingMiddleware(_ => Task.CompletedTask, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // Health endpoint path is not excluded by middleware (it logs all paths);
    // the middleware design logs every request, including health checks.
    // This test documents that behaviour.
    [Fact]
    public async Task InvokeAsync_HealthEndpoint_IsAlsoLogged()
    {
        // Arrange
        var loggedMessages = new List<string>();
        var mockLogger = CreateCapturingLogger(loggedMessages);

        var middleware = new AuditLoggingMiddleware(
            _ => Task.CompletedTask,
            mockLogger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/health";

        // Act
        await middleware.InvokeAsync(httpContext);

        // Assert: middleware logs it (no skip logic for health)
        loggedMessages.Should().ContainSingle();
        loggedMessages[0].Should().Contain("/api/health");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ILogger<AuditLoggingMiddleware> CreateCapturingLogger(List<string> captured)
    {
        var mock = new Mock<ILogger<AuditLoggingMiddleware>>();
        mock.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>(
                (level, eventId, state, exception, formatter) =>
                {
                    var message = formatter.DynamicInvoke(state, exception) as string ?? string.Empty;
                    captured.Add(message);
                });
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return mock.Object;
    }
}
