using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Atlas.ControlPlane.Tests.Unit.Infrastructure.Data;

public class AtlasDbContextModelTests
{
  [Fact]
  public void IacChangeSet_ArtifactUri_IsNotLengthConstrained()
  {
    var options = new DbContextOptionsBuilder<AtlasDbContext>()
        .UseInMemoryDatabase($"atlas-model-{Guid.NewGuid()}")
        .Options;

    using var db = new AtlasDbContext(options);

    var entityType = db.Model.FindEntityType(typeof(IacChangeSet));
    Assert.NotNull(entityType);

    var artifactUriProperty = entityType!.FindProperty(nameof(IacChangeSet.ArtifactUri));
    Assert.NotNull(artifactUriProperty);

    Assert.Null(artifactUriProperty!.GetMaxLength());
  }
}
