using EPDeskServerApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<FileIndex> FileIndexes => Set<FileIndex>();
    public DbSet<FileRequest> FileRequests => Set<FileRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>()
            .HasIndex(x => x.DeviceCode)
            .IsUnique();

        modelBuilder.Entity<FileIndex>()
            .HasIndex(x => new { x.DeviceCode, x.FullPath })
            .IsUnique();

        modelBuilder.Entity<FileIndex>()
            .HasIndex(x => x.FileName);

        modelBuilder.Entity<FileIndex>()
            .HasIndex(x => x.Extension);

        modelBuilder.Entity<FileRequest>()
            .HasIndex(x => x.DeviceCode);

        modelBuilder.Entity<FileRequest>()
            .HasIndex(x => x.Status);
    }
}