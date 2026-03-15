using Atlas.AgentOrchestrator.Integrations.Azure;
using Xunit;

namespace Atlas.AgentOrchestrator.Tests.Unit.Integrations.Azure;

public class AzureCarbonClientTests
{
  [Fact]
  public void TryBuildFallbackDateRange_WithValidApiError_ParsesAndBuildsWindow()
  {
    // Arrange
    const string response = "Bad Request({\"error\":{\"code\":\"InvalidRequestPropertyValue\",\"message\":\"The end date 02/05/2026 00:00:00 and start date 03/07/2026 00:00:00 should be in available range StartDate: 2025-01-01, EndDate: 2026-01-01. with clientRequestId=abc\"}})";

    // Act
    var success = AzureCarbonClient.TryBuildFallbackDateRange(response, out var start, out var end);

    // Assert
    Assert.True(success);
    Assert.Equal(new DateTime(2026, 1, 1), end.Date);
    Assert.Equal(new DateTime(2025, 12, 2), start.Date);
  }

  [Fact]
  public void TryBuildFallbackDateRange_WithShortAvailableWindow_ClampsToAvailableStart()
  {
    // Arrange
    const string response = "StartDate: 2025-12-20, EndDate: 2026-01-01.";

    // Act
    var success = AzureCarbonClient.TryBuildFallbackDateRange(response, out var start, out var end);

    // Assert
    Assert.True(success);
    Assert.Equal(new DateTime(2025, 12, 20), start.Date);
    Assert.Equal(new DateTime(2026, 1, 1), end.Date);
  }

  [Fact]
  public void TryBuildFallbackDateRange_WithUnrecognizedMessage_ReturnsFalse()
  {
    // Arrange
    const string response = "Some unrelated bad request message";

    // Act
    var success = AzureCarbonClient.TryBuildFallbackDateRange(response, out _, out _);

    // Assert
    Assert.False(success);
  }
}
