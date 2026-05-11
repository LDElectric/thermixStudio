using Microsoft.EntityFrameworkCore;
using ThermixStudio.Core;

namespace ThermixStudio.Infrastructure;

public sealed class ThermixDbContext(DbContextOptions<ThermixDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<Thermogram> Thermograms => Set<Thermogram>();
    public DbSet<ThermalMeasurement> ThermalMeasurements => Set<ThermalMeasurement>();
    public DbSet<ReportRecord> Reports => Set<ReportRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Inspection>().HasIndex(x => x.OsNumber).IsUnique();

        modelBuilder.Entity<Inspection>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<Thermogram>().Property(x => x.Criticality).HasConversion<string>();
        modelBuilder.Entity<ThermalMeasurement>().Property(x => x.Type).HasConversion<string>();
    }
}