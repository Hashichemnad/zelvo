using Microsoft.EntityFrameworkCore.Design;

namespace Zelvo.Data;

/// <summary>Used by <c>dotnet ef</c> to create migrations. It never connects.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ZelvoDbContext>
{
    public ZelvoDbContext CreateDbContext(string[] args) =>
        new(DataServiceCollectionExtensions.CreateOptions("Host=localhost;Database=zelvo_design_time"));
}
