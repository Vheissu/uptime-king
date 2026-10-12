using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UptimeKing.Api.Data;

public sealed class UptimeDbFactory : IDesignTimeDbContextFactory<UptimeDb>
{
    public UptimeDb CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<UptimeDb>()
                .UseNpgsql(
                    Environment.GetEnvironmentVariable("ConnectionStrings__uptime")
                        ?? "Host=localhost;Database=uptime;Username=uptime"
                )
                .Options
        );
}
