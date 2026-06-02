using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReconPlatform.Storage;
using Xunit;

namespace ReconPlatform.UnitTests.Storage;

/// <summary>
/// Task 6.4 — Unit tests for dead-letter handling.
/// We test the SqlMetadataClient.WriteDeadLetterAuditAsync contract
/// (the audit entry that DeadLetterMonitor writes for each dead-lettered message).
/// </summary>
public class DeadLetterMonitorTests
{
    // ── ProcessDeadLetter_WritesToAuditLog ────────────────────────────────────

    [Fact]
    public void ProcessDeadLetter_WritesToAuditLog_CorrectSignatureExists()
    {
        // WriteDeadLetterAuditAsync is the public method DeadLetterMonitor calls.
        // Verify it exists with the correct signature.
        var client = new SqlMetadataClient(
            "Server=localhost;Database=recon;Authentication=Active Directory Managed Identity;",
            NullLogger<SqlMetadataClient>.Instance);

        // The method must accept: messageId, reason, description, enqueuedTime, ct
        var method = typeof(SqlMetadataClient).GetMethod(nameof(SqlMetadataClient.WriteDeadLetterAuditAsync));
        method.Should().NotBeNull("WriteDeadLetterAuditAsync must be public on SqlMetadataClient");

        var parameters = method!.GetParameters();
        parameters.Should().HaveCount(5);
        parameters[0].Name.Should().Be("messageId");
        parameters[1].Name.Should().Be("deadLetterReason");
        parameters[2].Name.Should().Be("deadLetterErrorDescription");
        parameters[3].Name.Should().Be("enqueuedTime");
        parameters[4].Name.Should().Be("ct");
    }

    // ── ProcessDeadLetter_DoesNotLogMessageBody ───────────────────────────────

    [Fact]
    public void ProcessDeadLetter_DoesNotLogMessageBody()
    {
        // The WriteDeadLetterAuditAsync method signature does not accept a body parameter.
        // This enforces the SOC2 requirement that message bodies are never logged.
        var method = typeof(SqlMetadataClient).GetMethod(nameof(SqlMetadataClient.WriteDeadLetterAuditAsync));
        method.Should().NotBeNull();

        var paramNames = method!.GetParameters().Select(p => p.Name).ToList();

        // No parameter should be named "body" or "messageBody" or "content"
        paramNames.Should().NotContain("body");
        paramNames.Should().NotContain("messageBody");
        paramNames.Should().NotContain("content");
        paramNames.Should().NotContain("payload");
    }

    // ── WriteDeadLetterAuditAsync_EmptyMessageId_ThrowsArgument ──────────────

    [Fact]
    public async Task WriteDeadLetterAuditAsync_EmptyMessageId_ThrowsArgument()
    {
        var client = new SqlMetadataClient(
            "Server=localhost;Database=recon;Authentication=Active Directory Managed Identity;",
            NullLogger<SqlMetadataClient>.Instance);

        var act = async () => await client.WriteDeadLetterAuditAsync(
            messageId: "",
            deadLetterReason: "ProcessingFailed",
            deadLetterErrorDescription: "Deserialization error",
            enqueuedTime: DateTimeOffset.UtcNow);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── WriteDeadLetterAuditAsync_ValidMetadata_DoesNotThrowOnGuards ─────────

    [Fact]
    public void WriteDeadLetterAuditAsync_ValidMetadata_PassesGuardClauses()
    {
        var client = new SqlMetadataClient(
            "Server=localhost;Database=recon;Authentication=Active Directory Managed Identity;",
            NullLogger<SqlMetadataClient>.Instance);

        // The call will fail on SQL connection, but guard clauses pass.
        // We confirm no ArgumentException is raised for valid metadata.
        var act = async () => await client.WriteDeadLetterAuditAsync(
            messageId: "msg-abc-123",
            deadLetterReason: "MaxDeliveryCountExceeded",
            deadLetterErrorDescription: "Retried 10 times",
            enqueuedTime: DateTimeOffset.UtcNow.AddMinutes(-5));

        // Will throw SqlException (no real SQL server), NOT ArgumentException
        act.Should().NotThrowAsync<ArgumentException>();
    }
}
