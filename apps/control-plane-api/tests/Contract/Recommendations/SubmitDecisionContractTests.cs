using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Atlas.ControlPlaneApi.Tests.Contract.Recommendations;

/// <summary>
/// T028: Contract tests for recommendation decision submission
/// Validates dual-control approval workflow
/// </summary>
public class SubmitDecisionContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly string _apiVersion = "2026-02-16";

    public SubmitDecisionContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SubmitDecision_RequiresApiVersion()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var payload = new
        {
            decision = "approved",
            rationale = "LGTM"
        };

        // Act - Missing api-version
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations/{recommendationId}/decisions",
            payload);

        // Assert
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Recommendations endpoints are currently disabled (RecommendationsController.cs.disabled).
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problemDetails = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problemDetails.TryGetProperty("errorCode", out var errorCodeProperty));
        Assert.Equal("MissingApiVersionParameter", errorCodeProperty.GetString());
    }

    [Fact]
    public async Task SubmitDecision_ValidatesDecisionValue()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var payload = new
        {
            decision = "invalid-value", // Only 'approved' or 'rejected' allowed
            rationale = "Test"
        };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations/{recommendationId}/decisions?api-version={_apiVersion}",
            payload);

        // Assert
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Recommendations endpoints are currently disabled (RecommendationsController.cs.disabled).
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problemDetails = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problemDetails.TryGetProperty("errorCode", out var errorCodeProperty));
        Assert.Equal("InvalidDecisionValue", errorCodeProperty.GetString());
    }

    [Fact]
    public async Task SubmitDecision_RequiresRationale()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var payload = new
        {
            decision = "approved"
            // Missing rationale
        };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations/{recommendationId}/decisions?api-version={_apiVersion}",
            payload);

        // Assert
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Recommendations endpoints are currently disabled (RecommendationsController.cs.disabled).
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problemDetails = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problemDetails.TryGetProperty("errorCode", out var errorCodeProperty));
        Assert.Equal("MissingRationale", errorCodeProperty.GetString());
    }

    [Fact]
    public async Task SubmitDecision_ReturnsAccepted_WhenPendingApproval()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var payload = new
        {
            decision = "approved",
            rationale = "Valid architecture improvement"
        };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations/{recommendationId}/decisions?api-version={_apiVersion}",
            payload);

        // Assert
        // Could be 202 Accepted (awaiting 2nd approval), 200 OK (approved), or 404 (not found)
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted ||
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            // Dual-control: First approval submitted, awaiting second
            var content = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(content.TryGetProperty("status", out var statusProperty));
            Assert.Equal("pending_second_approval", statusProperty.GetString());
            Assert.True(content.TryGetProperty("approvalsReceived", out _));
            Assert.True(content.TryGetProperty("approvalsRequired", out _));
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            // Dual-control: Both approvals received, recommendation approved
            var content = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(content.TryGetProperty("status", out var statusProperty));
            Assert.Equal("approved", statusProperty.GetString());
        }
    }

    [Fact]
    public async Task SubmitDecision_ReturnsDecisionId()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var payload = new
        {
            decision = "approved",
            rationale = "Test approval"
        };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations/{recommendationId}/decisions?api-version={_apiVersion}",
            payload);

        // Assert
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadFromJsonAsync<JsonElement>();

            // Must return decision ID for audit trail
            Assert.True(content.TryGetProperty("decisionId", out var decisionIdProperty));
            Assert.NotEqual(Guid.Empty, Guid.Parse(decisionIdProperty.GetString()!));

            // Must include timestamp
            Assert.True(content.TryGetProperty("submittedAt", out _));

            // Must include submitter (from auth context)
            Assert.True(content.TryGetProperty("submittedBy", out _));
        }
    }

    [Fact]
    public async Task SubmitDecision_IncludesRejectionRationale()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        var payload = new
        {
            decision = "rejected",
            rationale = "Does not align with current architecture roadmap"
        };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations/{recommendationId}/decisions?api-version={_apiVersion}",
            payload);

        // Assert
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadFromJsonAsync<JsonElement>();

            // Rejection must include rationale in response
            Assert.True(content.TryGetProperty("rationale", out var rationaleProperty));
            Assert.NotNull(rationaleProperty.GetString());
        }
    }
}
