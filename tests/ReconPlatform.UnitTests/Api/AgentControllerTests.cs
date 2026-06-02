using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ReconPlatform.Api.Controllers;
using Xunit;

namespace ReconPlatform.UnitTests.Api;

/// <summary>
/// Unit tests for <see cref="AgentController"/>.
/// Controller is instantiated directly — no TestServer required.
/// </summary>
public class AgentControllerTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (AgentController controller, Mock<HttpMessageHandler> handlerMock)
        BuildController(string? teamClaim = "team-a")
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Loose);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"answer\":\"ok\"}"),
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Url"] = "http://localhost:8000",
            })
            .Build();

        var controller = new AgentController(
            factoryMock.Object,
            config,
            NullLogger<AgentController>.Instance);

        // Build an HttpContext with optional team claim
        var httpContext = new DefaultHttpContext();

        var identity = new ClaimsIdentity("Bearer");
        if (teamClaim is not null)
            identity.AddClaim(new Claim("team", teamClaim));

        httpContext.User = new ClaimsPrincipal(identity);

        if (teamClaim is not null)
            httpContext.Items["team_claim"] = teamClaim;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return (controller, handlerMock);
    }

    // ── Validation tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_MissingTeam_Returns400()
    {
        var (controller, _) = BuildController(teamClaim: null);
        // No team_claim in HttpContext.Items so even if we put team in request we'd get 403.
        // Use empty team to exercise the validation path.
        var request = new AgentQueryRequest(
            Team: "",
            EngagementId: "eng-1",
            Question: "list assets",
            Model: null);

        var result = await controller.QueryAsync(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task QueryAsync_EmptyQuestion_Returns400()
    {
        var (controller, _) = BuildController("team-a");
        var request = new AgentQueryRequest(
            Team: "team-a",
            EngagementId: "eng-1",
            Question: "",
            Model: null);

        var result = await controller.QueryAsync(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task QueryAsync_QuestionExceeds2000Chars_Returns400()
    {
        var (controller, _) = BuildController("team-a");
        var request = new AgentQueryRequest(
            Team: "team-a",
            EngagementId: "eng-1",
            Question: new string('x', 2001),
            Model: null);

        var result = await controller.QueryAsync(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Team-claim enforcement ─────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_TeamClaimMismatch_Returns403()
    {
        // Controller has team_claim="team-a" but request asks for "team-b"
        var (controller, _) = BuildController(teamClaim: "team-a");
        var request = new AgentQueryRequest(
            Team: "team-b",
            EngagementId: "eng-1",
            Question: "list assets",
            Model: null);

        var result = await controller.QueryAsync(request, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_ValidRequest_Returns200WithDownstreamBody()
    {
        var (controller, _) = BuildController("team-a");
        var request = new AgentQueryRequest(
            Team: "team-a",
            EngagementId: "eng-1",
            Question: "list assets",
            Model: null);

        var result = await controller.QueryAsync(request, CancellationToken.None);

        result.Should().BeOfType<ContentResult>()
            .Which.StatusCode.Should().BeNull(); // ContentResult with no explicit code → 200
    }
}
