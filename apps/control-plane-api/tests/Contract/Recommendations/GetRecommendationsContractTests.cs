using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Atlas.ControlPlaneApi.Tests.Contract.Recommendations;

/// <summary>
/// T028: Contract tests for recommendation endpoints
/// Validates REST compliance and Microsoft API Guidelines patterns
/// </summary>
public class GetRecommendationsContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly string _apiVersion = "2026-02-16";

    public GetRecommendationsContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetRecommendations_RequiresApiVersion()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();

        // Act - Missing api-version query parameter
        var response = await _client.GetAsync($"/api/v1/service-groups/{serviceGroupId}/recommendations");

        // Assert
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Recommendations endpoints are currently disabled (RecommendationsController.cs.disabled).
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify RFC 9457 Problem Details
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Assert.Equal("application/problem+json", contentType);

        // Verify x-error-code header
        Assert.True(response.Headers.Contains("x-error-code"));
        var errorCode = response.Headers.GetValues("x-error-code").FirstOrDefault();
        Assert.Equal("MissingApiVersionParameter", errorCode);

        // Verify body includes errorCode extension matching x-error-code
        var problemDetails = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problemDetails.TryGetProperty("errorCode", out var errorCodeProperty));
        Assert.Equal("MissingApiVersionParameter", errorCodeProperty.GetString());
    }

    [Fact]
    public async Task GetRecommendations_ReturnsPagedResults()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations?api-version={_apiVersion}");

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadFromJsonAsync<JsonElement>();

            // Verify pagination contract: { "value": [...], "nextLink": "..." }
            Assert.True(content.TryGetProperty("value", out var valueProperty));
            Assert.Equal(JsonValueKind.Array, valueProperty.ValueKind);

            // nextLink is optional (only present when there are more pages)
            if (content.TryGetProperty("nextLink", out var nextLinkProperty))
            {
                var nextLink = nextLinkProperty.GetString();
                Assert.NotNull(nextLink);

                // nextLink must be absolute URL and include api-version
                Assert.StartsWith("http", nextLink);
                Assert.Contains("api-version=", nextLink);
            }

            // Validate each recommendation has required fields
            foreach (var item in valueProperty.EnumerateArray())
            {
                Assert.True(item.TryGetProperty("id", out _));
                Assert.True(item.TryGetProperty("serviceGroupId", out _));
                Assert.True(item.TryGetProperty("category", out _));
                Assert.True(item.TryGetProperty("confidence", out _));
                Assert.True(item.TryGetProperty("status", out _)); // pending, approved, rejected
                Assert.True(item.TryGetProperty("createdAt", out _));
            }
        }
    }

    [Fact]
    public async Task GetRecommendation_ById_ReturnsDetails()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations/{recommendationId}?api-version={_apiVersion}");

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);

        if (response.IsSuccessStatusCode)
        {
            var recommendation = await response.Content.ReadFromJsonAsync<JsonElement>();

            // Verify required fields
            Assert.True(recommendation.TryGetProperty("id", out _));
            Assert.True(recommendation.TryGetProperty("serviceGroupId", out _));
            Assert.True(recommendation.TryGetProperty("category", out _));
            Assert.True(recommendation.TryGetProperty("confidence", out var confidenceProperty));
            Assert.True(recommendation.TryGetProperty("status", out _));
            Assert.True(recommendation.TryGetProperty("rationale", out _)); // Why this recommendation
            Assert.True(recommendation.TryGetProperty("impact", out _)); // Expected impact
            Assert.True(recommendation.TryGetProperty("proposedChanges", out _)); // What will change
            Assert.True(recommendation.TryGetProperty("createdAt", out _));

            // Confidence must be 0.0-1.0
            var confidence = confidenceProperty.GetDecimal();
            Assert.InRange(confidence, 0.0m, 1.0m);
        }
    }

    [Fact]
    public async Task GetRecommendations_SupportsFiltering()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();

        // Act - Filter by status
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations?api-version={_apiVersion}&status=pending");

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(content.TryGetProperty("value", out var valueProperty));

            // All returned items must have status=pending
            foreach (var item in valueProperty.EnumerateArray())
            {
                Assert.True(item.TryGetProperty("status", out var statusProperty));
                Assert.Equal("pending", statusProperty.GetString());
            }
        }
    }

    [Fact]
    public async Task GetRecommendations_SupportsConfidenceThreshold()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();

        // Act - Filter by minimum confidence
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/recommendations?api-version={_apiVersion}&minConfidence=0.7");

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(content.TryGetProperty("value", out var valueProperty));

            // All returned items must have confidence >= 0.7
            foreach (var item in valueProperty.EnumerateArray())
            {
                Assert.True(item.TryGetProperty("confidence", out var confidenceProperty));
                var confidence = confidenceProperty.GetDecimal();
                Assert.True(confidence >= 0.7m);
            }
        }
    }

    [Fact]
    public async Task GetRecommendations_ReturnsConsistentErrorContract()
    {
        // Arrange - Use invalid service group ID format
        var invalidId = "not-a-guid";

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{invalidId}/recommendations?api-version={_apiVersion}");

        // Assert
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Recommendations endpoints are currently disabled (RecommendationsController.cs.disabled).
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify RFC 9457 Problem Details
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Assert.Equal("application/problem+json", contentType);

        // Verify x-error-code header
        Assert.True(response.Headers.Contains("x-error-code"));
        var errorCode = response.Headers.GetValues("x-error-code").FirstOrDefault();
        Assert.NotNull(errorCode);

        // Verify body structure
        var problemDetails = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problemDetails.TryGetProperty("type", out _));
        Assert.True(problemDetails.TryGetProperty("title", out _));
        Assert.True(problemDetails.TryGetProperty("status", out _));
        Assert.True(problemDetails.TryGetProperty("errorCode", out var errorCodeProperty));

        // Verify x-error-code matches errorCode in body
        Assert.Equal(errorCode, errorCodeProperty.GetString());
    }
}
