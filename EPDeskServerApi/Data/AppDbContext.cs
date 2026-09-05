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
    public DbSet<AgentVersion> AgentVersions => Set<AgentVersion>();
    public DbSet<AgentLog> AgentLogs => Set<AgentLog>();
    public DbSet<RemoteCommand> RemoteCommands => Set<RemoteCommand>();
    public DbSet<DeviceDiagnosticReport> DeviceDiagnosticReports => Set<DeviceDiagnosticReport>();
    public DbSet<DeviceScanExclusion> DeviceScanExclusions => Set<DeviceScanExclusion>();
    public DbSet<AutomaticFileUpload> AutomaticFileUploads => Set<AutomaticFileUpload>();
    public DbSet<FileUploadPolicy> FileUploadPolicies => Set<FileUploadPolicy>();
    public DbSet<OldUserDataImportJob> OldUserDataImportJobs => Set<OldUserDataImportJob>();
    public DbSet<OldUserDataFile> OldUserDataFiles => Set<OldUserDataFile>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<ExtractionJob> ExtractionJobs => Set<ExtractionJob>();
    public DbSet<DocumentSection> DocumentSections => Set<DocumentSection>();
    public DbSet<DocumentDerivative> DocumentDerivatives => Set<DocumentDerivative>();

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

        modelBuilder.Entity<AgentVersion>()
            .HasIndex(x => x.Version)
            .IsUnique();

        modelBuilder.Entity<AgentVersion>()
            .HasIndex(x => x.IsActive);

        modelBuilder.Entity<AgentLog>()
    .HasIndex(x => x.DeviceCode);

        modelBuilder.Entity<AgentLog>()
            .HasIndex(x => x.RequestId);

        modelBuilder.Entity<AgentLog>()
            .HasIndex(x => x.CreatedAtUtc);

        modelBuilder.Entity<RemoteCommand>()
            .HasIndex(x => new { x.DeviceCode, x.Status });

        modelBuilder.Entity<DeviceDiagnosticReport>()
            .HasIndex(x => x.DeviceCode);

        modelBuilder.Entity<DeviceDiagnosticReport>()
            .HasIndex(x => x.CreatedAtUtc);

        modelBuilder.Entity<DeviceScanExclusion>()
            .HasIndex(x => new { x.DeviceCode, x.IsActive });

        modelBuilder.Entity<DeviceScanExclusion>()
            .HasIndex(x => new { x.DeviceCode, x.ExclusionType, x.Value });

        modelBuilder.Entity<AutomaticFileUpload>()
            .HasIndex(x => new { x.DeviceCode, x.PathIdentity })
            .IsUnique();

        modelBuilder.Entity<AutomaticFileUpload>()
            .HasIndex(x => x.Status);

        modelBuilder.Entity<AutomaticFileUpload>()
            .HasIndex(x => x.UpdatedAtUtc);

        modelBuilder.Entity<AutomaticFileUpload>()
            .Property(x => x.B2VersionId)
            .HasMaxLength(256);

        modelBuilder.Entity<AutomaticFileUpload>()
            .Property(x => x.ObjectETag)
            .HasMaxLength(256);

        modelBuilder.Entity<AutomaticFileUpload>()
            .Property(x => x.Sha256)
            .HasMaxLength(64);

        modelBuilder.Entity<OldUserDataImportJob>()
            .HasIndex(x => x.RootPathIdentity)
            .IsUnique();

        modelBuilder.Entity<OldUserDataImportJob>()
            .HasIndex(x => x.Status);

        modelBuilder.Entity<OldUserDataFile>()
            .HasIndex(x => new { x.ImportJobId, x.FullPath })
            .IsUnique();

        modelBuilder.Entity<OldUserDataFile>()
            .HasIndex(x => new { x.ImportJobId, x.Status });

        modelBuilder.Entity<OldUserDataFile>()
            .HasIndex(x => new { x.ImportJobId, x.DeviceCode });

        modelBuilder.Entity<OldUserDataFile>()
            .Property(x => x.B2VersionId)
            .HasMaxLength(256);

        modelBuilder.Entity<OldUserDataFile>()
            .Property(x => x.ObjectETag)
            .HasMaxLength(256);

        modelBuilder.Entity<OldUserDataFile>()
            .Property(x => x.Sha256)
            .HasMaxLength(64);

        modelBuilder.Entity<OldUserDataFile>()
            .HasOne(x => x.ImportJob)
            .WithMany(x => x.Files)
            .HasForeignKey(x => x.ImportJobId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.ConfigureDocumentExtraction();
    }
}
