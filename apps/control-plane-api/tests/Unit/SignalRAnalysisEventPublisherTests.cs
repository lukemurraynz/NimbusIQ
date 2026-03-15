using Atlas.ControlPlane.Api.Hubs;
using Atlas.ControlPlane.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace Atlas.ControlPlane.Tests.Unit;

/// <summary>
/// Unit tests for SignalRAnalysisEventPublisher — verifies that analysis events are
/// dispatched without throwing in an in-process test server.
/// </summary>
public class SignalRAnalysisEventPublisherTests
{
    private readonly SignalRAnalysisEventPublisher _publisher;

    public SignalRAnalysisEventPublisherTests()
    {
        var hub = new StubHubContext();
        _publisher = new SignalRAnalysisEventPublisher(hub);
    }

    // ──────────────────────────────────────────────
    // AgentStartedAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AgentStartedAsync_DoesNotThrow()
    {
        // In a test environment there are no connected clients so SendAsync is a no-op.
        // We verify no exception is raised.
        await _publisher.AgentStartedAsync("run-1", "BestPracticeAgent", "Evaluating rules");
    }

    // ──────────────────────────────────────────────
    // AgentCompletedAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AgentCompletedAsync_DoesNotThrow()
    {
        await _publisher.AgentCompletedAsync(
            runId: "run-2",
            agentName: "FinOpsAgent",
            success: true,
            elapsedMs: 1234,
            itemsProcessed: 42,
            scoreValue: 0.87,
            summary: "42 orphaned resources detected");
    }

    // ──────────────────────────────────────────────
    // AgentFindingAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AgentFindingAsync_DoesNotThrow()
    {
        await _publisher.AgentFindingAsync(
            runId: "run-3",
            agentName: "SecurityAgent",
            category: "Security",
            severity: "High",
            message: "Storage account allows public blob access");
    }

    // ──────────────────────────────────────────────
    // RunCompletedAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RunCompletedAsync_DoesNotThrow()
    {
        await _publisher.RunCompletedAsync(
            runId: "run-4",
            status: "Completed",
            completedAt: DateTime.UtcNow,
            overallScore: 0.75,
            resourceCount: 120);
    }

    private sealed class StubHubContext : IHubContext<AnalysisHub>
    {
        public IHubClients Clients { get; } = new StubHubClients();
        public IGroupManager Groups { get; } = new StubGroupManager();
    }

    private sealed class StubHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new StubClientProxy();

        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class StubClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
