using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EPDeskServerApi.Data;

/// <summary>
/// Keeps EF migration scaffolding independent from deployment secrets and from
/// the API startup migration path. The connection is not opened when generating
/// migrations or SQL scripts.
/// </summary>
public sealed class AppDbContextDesignTimeFactory :
    IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=epdesk_design;" +
                "Username=epdesk_design;Password=not-used"
            )
            .Options;

        return new AppDbContext(options);
    }
}
