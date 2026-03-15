using Atlas.AgentOrchestrator.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.AgentOrchestrator.Tests.Unit.Contracts;

public class A2AMessageValidatorTests
{
    private readonly A2AMessageValidator _validator;

    public A2AMessageValidatorTests()
    {
        var logger = NullLogger<A2AMessageValidator>.Instance;
        _validator = new A2AMessageValidator(logger);
    }

    [Fact]
    public void Validate_WithValidMessage_ReturnsValidationResult()
    {
        var message = new A2AMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            SenderAgent = "FinOps",
            RecipientAgent = "GovernanceMediator",
            MessageType = A2AMessageTypes.ConcurrentResult,
            Payload = new ConcurrentEvaluationPayload
            {
                AgentName = "FinOps",
                Pillar = "CostOptimization",
                Score = 72,
                Position = "Reduce monthly spend by rightsizing workloads",
                SuggestedActions = ["Right-size compute"],
                EstimatedCostDelta = -500m,
                EstimatedRiskDelta = 0.05,
                SlaImpact = -0.001
            },
            Lineage = new LineageMetadata
            {
                OriginAgent = "FinOps",
                ContributingAgents = new List<string> { "FinOps" },
                EvidenceReferences = new List<string> { "analysis_run:123" },
                DecisionPath = new List<string> { "concurrent_mediator", "concurrent_result" },
                ConfidenceScore = 0.82m
            }
        };

        var result = _validator.Validate(message);

        Assert.NotNull(result);
        Assert.NotNull(result.Errors);
    }

    [Fact]
    public void Validate_WithInvalidPayloadType_ReturnsInvalid()
    {
        var message = new A2AMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            SenderAgent = "test-agent",
            RecipientAgent = "target-agent",
            MessageType = A2AMessageTypes.Analysis,
            Payload = "not-an-object",
            Lineage = new LineageMetadata
            {
                OriginAgent = "test-agent",
                ContributingAgents = new List<string> { "test-agent" },
                EvidenceReferences = new List<string>(),
                DecisionPath = new List<string>()
            }
        };

        var result = _validator.Validate(message);

        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateJson_WithValidJsonString_DoesNotThrow()
    {
        var jsonMessage = """
        {
            "message_id": "123e4567-e89b-12d3-a456-426614174000",
            "correlation_id": "123e4567-e89b-12d3-a456-426614174001",
            "timestamp": "2024-01-01T00:00:00Z",
            "sender_agent": "test-agent",
            "recipient_agent": "target-agent",
            "message_type": "discovery",
            "payload": {},
            "lineage": {
                "origin_agent": "test-agent",
                "contributing_agents": ["test-agent"],
                "evidence_references": [],
                "decision_path": ["step1"]
            }
        }
        """;

        var result = _validator.ValidateJson(jsonMessage);

        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateJson_WithInvalidJson_ReturnsInvalid()
    {
        // Arrange
        var invalidJson = "{ invalid json }";

        // Act
        var result = _validator.ValidateJson(invalidJson);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Constructor_InitializesWithoutThrowingException()
    {
        // Arrange & Act
        var logger = NullLogger<A2AMessageValidator>.Instance;
        var validator = new A2AMessageValidator(logger);

        // Assert
        Assert.NotNull(validator);
    }
}
