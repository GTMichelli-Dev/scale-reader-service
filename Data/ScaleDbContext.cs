using Microsoft.EntityFrameworkCore;
using ScaleReaderService.Models;

namespace ScaleReaderService.Data;

public class ScaleDbContext : DbContext
{
    public ScaleDbContext(DbContextOptions<ScaleDbContext> options) : base(options) { }

    public DbSet<ScaleConfigEntity> Scales => Set<ScaleConfigEntity>();
    public DbSet<ServiceSettings> Settings => Set<ServiceSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScaleConfigEntity>(e =>
        {
            e.ToTable("Scales");
            e.HasIndex(s => s.ScaleId).IsUnique();
        });

        modelBuilder.Entity<ServiceSettings>(e =>
        {
            e.ToTable("Settings");
            e.HasData(new ServiceSettings
            {
                Id = 1,
                ServerUrl = "http://localhost:5110",
                SignalRHub = "/scaleHub",
                // Seed with the public device-definitions URL so a fresh install
                // can fetch brand metadata without operator intervention. Anyone
                // running a fork can PUT /api/settings to change it at runtime.
                BrandsUrl = "https://raw.githubusercontent.com/GTMichelli-Dev/device-definitions/main/scales/scale-models.json",
                BrandsToken = ""
            });
        });
    }
}
