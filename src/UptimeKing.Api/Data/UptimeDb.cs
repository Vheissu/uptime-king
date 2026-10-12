using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Domain;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Api.Data;

public sealed class UptimeDb(DbContextOptions<UptimeDb> options) : DbContext(options)
{
    public DbSet<Monitor> Monitors => Set<Monitor>();
    public DbSet<CheckResult> Checks => Set<CheckResult>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<NotificationSettings> Settings => Set<NotificationSettings>();
    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Monitor>().Property(x => x.Name).HasMaxLength(100);
        model.Entity<Monitor>().Property(x => x.Url).HasMaxLength(2048);
        model.Entity<Monitor>().Property(x => x.Client).HasMaxLength(100);
        model.Entity<Monitor>().HasIndex(x => x.NextCheckAt);
        model.Entity<CheckResult>().HasIndex(x => new { x.MonitorId, x.CheckedAt });
        model.Entity<CheckResult>().HasIndex(x => x.CheckedAt);
        model.Entity<CheckResult>().Property(x => x.Error).HasMaxLength(500);
        model
            .Entity<Incident>()
            .HasIndex(x => x.MonitorId)
            .IsUnique()
            .HasFilter("\"ResolvedAt\" IS NULL");
        model
            .Entity<NotificationDelivery>()
            .HasIndex(x => new
            {
                x.IncidentId,
                x.Kind,
                x.Channel,
            })
            .IsUnique();
        model.Entity<NotificationDelivery>().HasIndex(x => x.NextAttemptAt);
        model
            .Entity<NotificationDelivery>()
            .HasOne<Incident>()
            .WithMany()
            .HasForeignKey(x => x.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
