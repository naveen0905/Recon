using FluentAssertions;
using ReconPlatform.Api.Logging;
using Xunit;

namespace ReconPlatform.UnitTests.Security;

/// <summary>
/// Task 6.7 — Tests for <see cref="SecretScrubbingPolicy.IsSensitive"/>.
/// Verifies that the scrub pattern matching correctly identifies secret-like field names.
/// </summary>
public class SecretScrubbingTests
{
    // ── Scrub_SecretField_IsRedacted ──────────────────────────────────────────

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("user_password")]
    [InlineData("admin_Password_Hash")]
    public void Scrub_PasswordField_IsRedacted(string name)
    {
        SecretScrubbingPolicy.IsSensitive(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("api_token")]
    [InlineData("token")]
    [InlineData("TOKEN")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    public void Scrub_TokenField_IsRedacted(string name)
    {
        SecretScrubbingPolicy.IsSensitive(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("client_secret")]
    [InlineData("clientSecret")]
    [InlineData("SECRET_KEY")]
    public void Scrub_SecretField_IsRedacted(string name)
    {
        SecretScrubbingPolicy.IsSensitive(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("connection_string")]
    [InlineData("connectionString")]
    [InlineData("conn_str")]
    [InlineData("dbconn")]
    [InlineData("ConnString")]
    public void Scrub_ConnectionStringField_IsRedacted(string name)
    {
        SecretScrubbingPolicy.IsSensitive(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("KEY_NAME")]
    [InlineData("private_key")]
    public void Scrub_KeyField_IsRedacted(string name)
    {
        SecretScrubbingPolicy.IsSensitive(name).Should().BeTrue();
    }

    // ── Scrub_NormalField_IsNotRedacted ───────────────────────────────────────

    [Theory]
    [InlineData("hostname")]
    [InlineData("team")]
    [InlineData("assetId")]
    [InlineData("status")]
    [InlineData("created_at")]
    [InlineData("source_id")]
    [InlineData("ip_address")]
    [InlineData("port")]
    [InlineData("actor")]
    [InlineData("method")]
    public void Scrub_NormalField_IsNotRedacted(string name)
    {
        SecretScrubbingPolicy.IsSensitive(name).Should().BeFalse();
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void IsSensitive_EmptyString_ReturnsFalse()
    {
        SecretScrubbingPolicy.IsSensitive("").Should().BeFalse();
    }

    [Fact]
    public void IsSensitive_NullString_ReturnsFalse()
    {
        SecretScrubbingPolicy.IsSensitive(null!).Should().BeFalse();
    }

    // ── TryDestructure with real objects ─────────────────────────────────────

    [Fact]
    public void TryDestructure_ObjectWithSecretProperty_RedactsIt()
    {
        // Arrange
        var policy = new SecretScrubbingPolicy();
        var testObj = new { hostname = "api.example.com", password = "s3cr3t!", port = 443 };

        var mockFactory = new MockLogEventPropertyValueFactory();

        // Act
        var handled = policy.TryDestructure(testObj, mockFactory, out var result);

        // Assert: the policy should handle this object (has a sensitive property)
        handled.Should().BeTrue();

        var structureValue = result as Serilog.Events.StructureValue;
        structureValue.Should().NotBeNull();

        var passwordProp = structureValue!.Properties.FirstOrDefault(p => p.Name == "password");
        passwordProp.Should().NotBeNull();

        var scalar = passwordProp!.Value as Serilog.Events.ScalarValue;
        scalar.Should().NotBeNull();
        scalar!.Value.Should().Be("[redacted]");
    }

    [Fact]
    public void TryDestructure_ObjectWithNoSecretProperties_NotHandled()
    {
        var policy = new SecretScrubbingPolicy();
        var testObj = new { hostname = "api.example.com", port = 443, team = "net-sec" };
        var mockFactory = new MockLogEventPropertyValueFactory();

        var handled = policy.TryDestructure(testObj, mockFactory, out var result);

        // No sensitive properties → policy returns false (hands off to Serilog default)
        handled.Should().BeFalse();
    }

    [Fact]
    public void TryDestructure_NullValue_NotHandled()
    {
        var policy = new SecretScrubbingPolicy();
        var mockFactory = new MockLogEventPropertyValueFactory();

        var handled = policy.TryDestructure(null!, mockFactory, out _);

        handled.Should().BeFalse();
    }

    [Fact]
    public void TryDestructure_PrimitiveValue_NotHandled()
    {
        var policy = new SecretScrubbingPolicy();
        var mockFactory = new MockLogEventPropertyValueFactory();

        var handled = policy.TryDestructure(42, mockFactory, out _);

        handled.Should().BeFalse();
    }
}

/// <summary>
/// Minimal implementation of <see cref="Serilog.Core.ILogEventPropertyValueFactory"/>
/// for unit test use — wraps scalar values.
/// </summary>
internal sealed class MockLogEventPropertyValueFactory : Serilog.Core.ILogEventPropertyValueFactory
{
    public Serilog.Events.LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects = false) =>
        new Serilog.Events.ScalarValue(value);
}
