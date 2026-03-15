using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.AgentOrchestrator.Contracts;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.ControlPlane.Tests.Contract;

public class A2AContractsTests : IClassFixture<ContractTestFactory>
{
  private readonly ContractTestFactory _factory;
  private readonly HttpClient _client;

  public A2AContractsTests(ContractTestFactory factory)
  {
    _factory = factory;
    _client = factory.CreateClient();
  }

  [Fact]
  public async Task PostA2AMessage_AcceptsValidMessage_AndMessageIsRetrievable()
  {
    var serviceGroupId = Guid.NewGuid();
    var analysisRunId = Guid.NewGuid();
    await SeedAnalysisRunAsync(serviceGroupId, analysisRunId);

    var messageId = Guid.NewGuid().ToString("D");
    var message = new A2AMessage
    {
      MessageId = messageId,
      CorrelationId = analysisRunId.ToString("D"),
      Timestamp = DateTimeOffset.UtcNow,
      SenderAgent = "FinOps",
      RecipientAgent = "GovernanceMediator",
      MessageType = A2AMessageTypes.ConcurrentResult,
      Priority = A2APriority.Normal,
      TtlSeconds = 600,
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
        ContributingAgents = ["FinOps"],
        EvidenceReferences = ["analysis_run:" + analysisRunId.ToString("D")],
        DecisionPath = ["concurrent_mediator", "concurrent_result"],
        ConfidenceScore = 0.82m
      }
    };

    var postResponse = await _client.PostAsJsonAsync($"/api/v1/agents/a2a/{analysisRunId}", message);

    Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

    var acceptedPayload = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(analysisRunId, acceptedPayload.GetProperty("analysisRunId").GetGuid());
    Assert.Equal(messageId, acceptedPayload.GetProperty("messageId").GetString());
    Assert.Equal("FinOps", acceptedPayload.GetProperty("senderAgent").GetString());
    Assert.Equal("GovernanceMediator", acceptedPayload.GetProperty("recipientAgent").GetString());

    var getResponse = await _client.GetAsync($"/api/v1/agents/a2a/{analysisRunId}?recipientAgent=GovernanceMediator");

    Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

    var getPayload = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
    var value = getPayload.GetProperty("value");
    Assert.Equal(JsonValueKind.Array, value.ValueKind);
    Assert.Single(value.EnumerateArray());

    var storedMessage = value.EnumerateArray().Single();
    Assert.Equal(messageId, storedMessage.GetProperty("message_id").GetString());
    Assert.Equal(A2AMessageTypes.ConcurrentResult, storedMessage.GetProperty("message_type").GetString());
    Assert.Equal("FinOps", storedMessage.GetProperty("sender_agent").GetString());
    Assert.Equal("GovernanceMediator", storedMessage.GetProperty("recipient_agent").GetString());
  }

  [Fact]
  public async Task ListA2AMessages_ReturnsConcurrentMediatorOutboxForAnalysisRun()
  {
    var serviceGroupId = Guid.NewGuid();
    var analysisRunId = Guid.NewGuid();
    await SeedAnalysisRunAsync(serviceGroupId, analysisRunId);

    var emittedAt = DateTimeOffset.UtcNow;
    var outboxMessages = new[]
    {
            CreatePersistedA2AMessage(analysisRunId, emittedAt.AddSeconds(1), "FinOps", "GovernanceMediator", A2AMessageTypes.ConcurrentResult),
            CreatePersistedA2AMessage(analysisRunId, emittedAt.AddSeconds(2), "Reliability", "GovernanceMediator", A2AMessageTypes.ConcurrentResult),
            CreatePersistedA2AMessage(analysisRunId, emittedAt.AddSeconds(3), "Security", "GovernanceMediator", A2AMessageTypes.ConcurrentResult),
            CreatePersistedA2AMessage(analysisRunId, emittedAt.AddSeconds(4), "ConcurrentMediatorOrchestrator", "GovernanceMediator", A2AMessageTypes.MediationRequest),
            CreatePersistedA2AMessage(analysisRunId, emittedAt.AddSeconds(5), "GovernanceMediator", "MultiAgentOrchestrator", A2AMessageTypes.MediationOutcome)
        };

    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
      db.AgentMessages.AddRange(outboxMessages.Select(item => item.Entity));
      await db.SaveChangesAsync();
    }

    var response = await _client.GetAsync($"/api/v1/agents/a2a/{analysisRunId}");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
    var value = payload.GetProperty("value");
    Assert.Equal(5, value.GetArrayLength());

    var messageTypes = value.EnumerateArray()
        .Select(item => item.GetProperty("message_type").GetString())
        .ToList();

    Assert.Equal(
        [
            A2AMessageTypes.ConcurrentResult,
                A2AMessageTypes.ConcurrentResult,
                A2AMessageTypes.ConcurrentResult,
                A2AMessageTypes.MediationRequest,
                A2AMessageTypes.MediationOutcome
        ],
        messageTypes);

    var governanceInboxResponse = await _client.GetAsync($"/api/v1/agents/a2a/{analysisRunId}?recipientAgent=GovernanceMediator");
    Assert.Equal(HttpStatusCode.OK, governanceInboxResponse.StatusCode);

    var governanceInbox = await governanceInboxResponse.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(4, governanceInbox.GetProperty("value").GetArrayLength());
  }

  private async Task SeedAnalysisRunAsync(Guid serviceGroupId, Guid analysisRunId)
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

    if (!db.ServiceGroups.Any(sg => sg.Id == serviceGroupId))
    {
      db.ServiceGroups.Add(new ServiceGroup
      {
        Id = serviceGroupId,
        ExternalKey = $"sg-{serviceGroupId:N}",
        Name = "A2A Contract Test Group",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      });
    }

    if (!db.AnalysisRuns.Any(run => run.Id == analysisRunId))
    {
      db.AnalysisRuns.Add(new AnalysisRun
      {
        Id = analysisRunId,
        ServiceGroupId = serviceGroupId,
        CorrelationId = Guid.NewGuid(),
        TriggeredBy = "contract-test-user",
        Status = "running",
        CreatedAt = DateTime.UtcNow
      });
    }

    await db.SaveChangesAsync();
  }

  private static (AgentMessage Entity, A2AMessage Contract) CreatePersistedA2AMessage(
      Guid analysisRunId,
      DateTimeOffset timestamp,
      string sender,
      string recipient,
      string messageType)
  {
    var contract = new A2AMessage
    {
      MessageId = Guid.NewGuid().ToString("D"),
      CorrelationId = analysisRunId.ToString("D"),
      Timestamp = timestamp,
      SenderAgent = sender,
      RecipientAgent = recipient,
      MessageType = messageType,
      Priority = messageType == A2AMessageTypes.MediationOutcome ? A2APriority.High : A2APriority.Normal,
      TtlSeconds = 900,
      Payload = new
      {
        sender,
        recipient,
        messageType
      },
      Lineage = new LineageMetadata
      {
        OriginAgent = sender,
        ContributingAgents = [sender],
        EvidenceReferences = ["analysis_run:" + analysisRunId.ToString("D")],
        DecisionPath = ["concurrent_mediator", messageType],
        ConfidenceScore = messageType == A2AMessageTypes.MediationOutcome ? 0.91m : 0.84m
      }
    };

    var entity = new AgentMessage
    {
      Id = Guid.NewGuid(),
      AnalysisRunId = analysisRunId,
      MessageId = Guid.Parse(contract.MessageId),
      AgentName = sender,
      AgentRole = "agent",
      MessageType = $"a2a.{messageType}",
      Payload = JsonSerializer.Serialize(contract, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
      EvidenceRefs = JsonSerializer.Serialize(contract.Lineage.EvidenceReferences, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
      Confidence = contract.Lineage.ConfidenceScore,
      CreatedAt = contract.Timestamp.UtcDateTime
    };

    return (entity, contract);
  }
}
