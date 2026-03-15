using Atlas.ControlPlane.Application.Recommendations;

namespace Atlas.ControlPlane.Tests.Unit.Application.Recommendations;

public class RecommendationWorkflowStatusTests
{
  [Fact]
  public void GetQueueStatusDatabaseValues_ContainsExpectedQueueStatuses()
  {
    var values = RecommendationWorkflowStatus.GetQueueStatusDatabaseValues();

    Assert.Contains(RecommendationWorkflowStatus.Pending, values);
    Assert.Contains(RecommendationWorkflowStatus.PendingApproval, values);
    Assert.Contains(RecommendationWorkflowStatus.ManualReview, values);
  }

  [Fact]
  public void GetQueueStatusDatabaseValues_ContainsLegacyPendingSecondApprovalValue()
  {
    var values = RecommendationWorkflowStatus.GetQueueStatusDatabaseValues();

    Assert.Contains("pending_second_approval", values);
  }
}
