using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ServiceMantle.ReferenceService.Data.Migrations;

[DbContext(typeof(ReferenceDbContext))]
public sealed class ReferenceDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => InitialReferenceModel.Build(modelBuilder);
}
