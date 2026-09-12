using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ServiceMantle.ReferenceService.Database.PostgreSql.Migrations;

[DbContext(typeof(ReferencePostgreSqlDbContext))]
public sealed class ReferencePostgreSqlDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) =>
        CurrentReferencePostgreSqlModel.Build(modelBuilder);
}
