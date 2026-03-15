using Atlas.ControlPlane.Application.ValueTracking;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Azure;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.ValueTracking;

public class ValueRealizationServiceTests : IDisposable
{
    private readonly AtlasDbContext _db;
    private readonly ValueRealizationService _sut;
    private readonly FakeCostManagementClient _costClient;
    private readonly Guid _serviceGroupId = Guid.NewGuid();
    private readonly Guid _recommendationId = Guid.NewGuid();

    public ValueRealizationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseInMemoryDatabase(databaseName: $"ValueRealizationTests_{Guid.NewGuid()}")
            .Options;

        _db = new AtlasDbContext(options);

        var serviceGroup = new ServiceGroup
        {
            Id = _serviceGroupId,
            ExternalKey = "test-sg",
            Name = "Test Service Group"
        };

        var recommendation = new Recommendation
        {
            Id = _recommendationId,
            ServiceGroupId = _serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = Guid.NewGuid(),
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
            TargetEnvironment = "production",
            Title = "Optimize VM SKU",
            Category = "FinOps",
            Description = "Downsize oversized VM",
            Summary = "VM is significantly underutilized",
            RecommendationType = "resize",
            ActionType = "modify",
            Priority = "high",
            Status = "approved",
            Confidence = 0.9m,
            Rationale = "VM CPU utilization < 20%",
            Impact = "Reduce costs by $500/month",
            ProposedChanges = "Change to D2s_v3",
            ApprovalMode = "single",
            RequiredApprovals = 1,
            ReceivedApprovals = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ServiceGroups.Add(serviceGroup);
        _db.Recommendations.Add(recommendation);
        _db.SaveChanges();

        _costClient = new FakeCostManagementClient();
        _sut = new ValueRealizationService(_db, null, _costClient, NullLogger<ValueRealizationService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task InitializeTrackingAsync_CreatesTracking_WithPaybackEstimate()
    {
        // Arrange
        var estimatedMonthlySavings = 500m;
        var estimatedCost = 1000m;

        // Act
        var tracking = await _sut.InitializeTrackingAsync(
            _recommendationId,
            null,
            estimatedMonthlySavings,
            estimatedCost);

        // Assert
        Assert.NotNull(tracking);
        Assert.Equal(_recommendationId, tracking.RecommendationId);
        Assert.Equal(estimatedMonthlySavings, tracking.EstimatedMonthlySavings);
        Assert.Equal(estimatedCost, tracking.EstimatedImplementationCost);
        Assert.Equal("pending", tracking.Status);
    }

    [Fact]
    public async Task RecordActualValueAsync_UpdatesTracking()
    {
        // Arrange
        await _sut.InitializeTrackingAsync(_recommendationId, null, 500m, 1000m);

        // Act
        var updated = await _sut.RecordActualValueAsync(_recommendationId, 450m, 1100m, "Slightly under estimate");

        // Assert
        Assert.NotNull(updated);
        Assert.Equal(450m, updated.ActualMonthlySavings);
        Assert.Equal(1100m, updated.ActualImplementationCost);
        Assert.Equal("measuring", updated.Status);
    }

    [Fact]
    public async Task GetDashboardDataAsync_ReturnsAggregatedMetrics()
    {
        // Arrange
        await _sut.InitializeTrackingAsync(_recommendationId, null, 500m, 1000m);
        await _sut.RecordActualValueAsync(_recommendationId, 480m, 1050m, "Close to estimate");

        // Act
        var dashboard = await _sut.GetDashboardDataAsync(_serviceGroupId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.True(dashboard.TotalEstimatedAnnualSavings > 0);
        Assert.True(dashboard.TotalActualAnnualSavings > 0);
    }

    [Fact]
    public async Task GetDashboardDataAsync_UsesRecommendationEstimates_WhenTrackingNotInitialized()
    {
        // Arrange
        var recommendation = await _db.Recommendations.FirstAsync(r => r.Id == _recommendationId);
        recommendation.EstimatedImpact = "{\"monthlySavings\":420,\"costDelta\":-420}";
        await _db.SaveChangesAsync();

        // Act
        var dashboard = await _sut.GetDashboardDataAsync(_serviceGroupId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.Equal(420m * 12m, dashboard.TotalEstimatedAnnualSavings);
        Assert.Equal(0m, dashboard.TotalActualAnnualSavings);
        Assert.Equal(1, dashboard.TotalRecommendations);
        Assert.Single(dashboard.TopSavers);
        Assert.Equal(420m, dashboard.TopSavers[0].MonthlySavings);
    }

    [Fact]
    public async Task GetDashboardDataAsync_ParsesNumericStringMonthlySavings_WhenTrackingNotInitialized()
    {
        // Arrange
        var recommendation = await _db.Recommendations.FirstAsync(r => r.Id == _recommendationId);
        recommendation.EstimatedImpact = "{\"monthlySavings\":\"420.5\"}";
        await _db.SaveChangesAsync();

        // Act
        var dashboard = await _sut.GetDashboardDataAsync(_serviceGroupId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.Equal(420.5m * 12m, dashboard.TotalEstimatedAnnualSavings);
        Assert.Single(dashboard.TopSavers);
        Assert.Equal(420.5m, dashboard.TopSavers[0].MonthlySavings);
    }

    [Fact]
    public async Task GetDashboardDataAsync_AllServiceGroups_AggregatesAcrossGroups()
    {
        // Arrange
        var secondServiceGroupId = Guid.NewGuid();
        var secondRecommendationId = Guid.NewGuid();

        _db.ServiceGroups.Add(new ServiceGroup
        {
            Id = secondServiceGroupId,
            ExternalKey = "test-sg-2",
            Name = "Test Service Group 2"
        });

        _db.Recommendations.Add(new Recommendation
        {
            Id = secondRecommendationId,
            ServiceGroupId = secondServiceGroupId,
            CorrelationId = Guid.NewGuid(),
            AnalysisRunId = Guid.NewGuid(),
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1",
            TargetEnvironment = "production",
            Title = "Rightsize storage",
            Category = "FinOps",
            Description = "Downgrade redundant tier",
            Summary = "Storage overprovisioned",
            RecommendationType = "resize",
            ActionType = "modify",
            Priority = "medium",
            Status = "approved",
            Confidence = 0.8m,
            Rationale = "Low utilization",
            Impact = "Reduce costs",
            ProposedChanges = "Switch to standard tier",
            ApprovalMode = "single",
            RequiredApprovals = 1,
            ReceivedApprovals = 0,
            EstimatedImpact = "{\"monthlySavings\":\"100\"}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var firstRecommendation = await _db.Recommendations.FirstAsync(r => r.Id == _recommendationId);
        firstRecommendation.EstimatedImpact = "{\"monthlySavings\":\"50\"}";

        await _db.SaveChangesAsync();

        // Act
        var dashboard = await _sut.GetDashboardDataAsync(serviceGroupId: null);

        // Assert
        Assert.NotNull(dashboard);
        Assert.Equal(2, dashboard.TotalRecommendations);
        Assert.Equal((50m + 100m) * 12m, dashboard.TotalEstimatedAnnualSavings);
    }

    [Fact]
    public async Task GetDashboardDataAsync_ServiceGroupScope_LoadsOnlySelectedGroupEvidence()
    {
        var secondServiceGroupId = Guid.NewGuid();
        _db.ServiceGroups.Add(new ServiceGroup
        {
            Id = secondServiceGroupId,
            ExternalKey = "test-sg-2",
            Name = "Test Service Group 2"
        });

        _db.ServiceGroupScopes.AddRange(
            new ServiceGroupScope
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = _serviceGroupId,
                SubscriptionId = "sub-a",
                ResourceGroup = "rg-a",
                CreatedAt = DateTime.UtcNow
            },
            new ServiceGroupScope
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = secondServiceGroupId,
                SubscriptionId = "sub-b",
                ResourceGroup = "rg-b",
                CreatedAt = DateTime.UtcNow
            });

        await _db.SaveChangesAsync();

        _costClient.SetMonthToDate("sub-a", "rg-a", 100m);
        _costClient.SetPreviousMonth("sub-a", "rg-a", 1200m);
        _costClient.SetMonthToDate("sub-b", "rg-b", 9999m);
        _costClient.SetPreviousMonth("sub-b", "rg-b", 9999m);

        var dashboard = await _sut.GetDashboardDataAsync(_serviceGroupId);

        Assert.Single(dashboard.CostEvidence);
        Assert.Equal("sub-a", dashboard.CostEvidence[0].SubscriptionId);
        Assert.Equal("rg-a", dashboard.CostEvidence[0].ResourceGroup);
        Assert.NotEqual(0m, dashboard.TotalActualAnnualSavings);
    }

    [Fact]
    public async Task GetDashboardDataAsync_ReportsFallbackWhenAzureMcpEvidenceIsUnavailable()
    {
        _db.ServiceGroupScopes.Add(new ServiceGroupScope
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            SubscriptionId = "sub-a",
            ResourceGroup = "rg-a",
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        _costClient.SetMonthToDate("sub-a", "rg-a", 100m);
        _costClient.SetPreviousMonth("sub-a", "rg-a", 1200m);

        var dashboard = await _sut.GetDashboardDataAsync(_serviceGroupId);

        Assert.Contains("Azure MCP evidence was unavailable", dashboard.BillingEvidenceStatus);
        Assert.Contains("Azure Cost Management API", dashboard.BillingEvidenceStatus);
        Assert.Contains("Azure MCP tool-call evidence was not resolved", dashboard.AzureMcpToolCallStatus);
    }

    [Fact]
    public async Task GetDashboardDataAsync_ReportsAzureMcpToolCallCoverage_WhenEvidenceIsReturned()
    {
        _db.ServiceGroupScopes.Add(new ServiceGroupScope
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            SubscriptionId = "sub-a",
            ResourceGroup = "rg-a",
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        var mcpClient = new FakeAzureMcpValueEvidenceClient();
        mcpClient.SetEvidence(
            subscriptionId: "sub-a",
            resourceGroup: "rg-a",
            evidence: new McpCostEvidence(
                MonthToDateCostUsd: 100m,
                BaselineMonthToDateCostUsd: 140m,
                EstimatedMonthlySavingsUsd: 40m,
                AnomalyCount: 2,
                AdvisorRecommendationLinks: 3,
                ActivityLogCorrelationEvents: 1,
                LastQueriedAtUtc: DateTime.UtcNow,
                Source: "Azure MCP"));

        var sut = new ValueRealizationService(
            _db,
            mcpClient,
            _costClient,
            NullLogger<ValueRealizationService>.Instance);

        var dashboard = await sut.GetDashboardDataAsync(_serviceGroupId);

        Assert.Contains("Azure MCP evidence loaded", dashboard.BillingEvidenceStatus);
        Assert.Contains("Azure MCP tool-call evidence resolved for 1/1", dashboard.AzureMcpToolCallStatus);
        Assert.Single(dashboard.CostEvidence);
        Assert.Equal("Azure MCP", dashboard.CostEvidence[0].EvidenceSource);
    }

    [Fact]
    public async Task GetDashboardDataAsync_ComputesCurrentAnnualRunRate_WhenBillingEvidenceAvailable()
    {
        _db.ServiceGroupScopes.Add(new ServiceGroupScope
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            SubscriptionId = "sub-tco",
            ResourceGroup = "rg-tco",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _costClient.SetMonthToDate("sub-tco", "rg-tco", 1000m);
        _costClient.SetPreviousMonth("sub-tco", "rg-tco", 1200m);

        var dashboard = await _sut.GetDashboardDataAsync(_serviceGroupId);

        // $1000 MTD extrapolated to annual must be positive
        Assert.True(dashboard.CurrentAnnualRunRate > 0m);
        // Optimised target must not exceed current run rate
        Assert.True(dashboard.OptimisedAnnualRunRate <= dashboard.CurrentAnnualRunRate);
    }

    [Fact]
    public async Task GetDashboardDataAsync_OptimisedAnnualRunRate_NeverGoesNegative()
    {
        // Estimated savings far exceed the tiny run rate — floor must hold at zero
        var recommendation = await _db.Recommendations.FirstAsync(r => r.Id == _recommendationId);
        recommendation.EstimatedImpact = "{\"monthlySavings\":99999}";

        _db.ServiceGroupScopes.Add(new ServiceGroupScope
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = _serviceGroupId,
            SubscriptionId = "sub-tco2",
            ResourceGroup = "rg-tco2",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _costClient.SetMonthToDate("sub-tco2", "rg-tco2", 0.01m);
        _costClient.SetPreviousMonth("sub-tco2", "rg-tco2", 0.01m);

        var dashboard = await _sut.GetDashboardDataAsync(_serviceGroupId);

        Assert.True(dashboard.OptimisedAnnualRunRate >= 0m);
    }

    [Fact]
    public async Task GetDashboardDataAsync_CurrentAnnualRunRate_IsZero_WhenNoServiceGroupSelected()
    {
        var dashboard = await _sut.GetDashboardDataAsync(serviceGroupId: null);

        Assert.Equal(0m, dashboard.CurrentAnnualRunRate);
        Assert.Equal(0m, dashboard.OptimisedAnnualRunRate);
    }

    private sealed class FakeCostManagementClient : IAzureCostManagementClient
    {
        private readonly Dictionary<(string SubscriptionId, string ResourceGroup), decimal> _monthToDate = new();
        private readonly Dictionary<(string SubscriptionId, string ResourceGroup), decimal> _previousMonth = new();

        public void SetMonthToDate(string subscriptionId, string? resourceGroup, decimal value)
            => _monthToDate[(subscriptionId, resourceGroup ?? string.Empty)] = value;

        public void SetPreviousMonth(string subscriptionId, string? resourceGroup, decimal value)
            => _previousMonth[(subscriptionId, resourceGroup ?? string.Empty)] = value;

        public Task<decimal> QueryCostsAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            _monthToDate.TryGetValue((subscriptionId, string.Empty), out var value);
            return Task.FromResult(value);
        }

        public Task<decimal> QueryCostsAsync(
            string subscriptionId,
            DateTime fromUtc,
            DateTime toUtc,
            string? resourceGroup = null,
            CancellationToken cancellationToken = default)
        {
            var key = (subscriptionId, resourceGroup ?? string.Empty);
            if (fromUtc.Day == 1 && fromUtc.Month == toUtc.Month)
            {
                _monthToDate.TryGetValue(key, out var mtdValue);
                return Task.FromResult(mtdValue);
            }

            _previousMonth.TryGetValue(key, out var previousValue);
            return Task.FromResult(previousValue);
        }

        public Task<IReadOnlyList<CostAlert>> GetAnomaliesAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CostAlert>>([]);
    }

    private sealed class FakeAzureMcpValueEvidenceClient : IAzureMcpValueEvidenceClient
    {
        private readonly Dictionary<(string SubscriptionId, string ResourceGroup), McpCostEvidence> _evidence = new();

        public void SetEvidence(string subscriptionId, string? resourceGroup, McpCostEvidence evidence)
            => _evidence[(subscriptionId, resourceGroup ?? string.Empty)] = evidence;

        public Task<McpCostEvidence?> TryGetCostEvidenceAsync(
            string subscriptionId,
            string? resourceGroup,
            DateTime monthStartUtc,
            DateTime utcNow,
            int elapsedDaysInCurrentMonth,
            int previousMonthDays,
            CancellationToken cancellationToken = default)
        {
            _evidence.TryGetValue((subscriptionId, resourceGroup ?? string.Empty), out var evidence);
            return Task.FromResult(evidence);
        }
    }
}
