using EPDeskServerApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Data;

public static class DocumentExtractionModelConfiguration
{
    public static void ConfigureDocumentExtraction(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(entity =>
        {
            entity.Property(x => x.SourceType).HasMaxLength(64);
            entity.Property(x => x.DeviceCode).HasMaxLength(128);
            entity.Property(x => x.DisplayName).HasMaxLength(512);
            entity.Property(x => x.Classification).HasMaxLength(64);
            entity.Property(x => x.Department).HasMaxLength(128);

            entity.HasIndex(x => new { x.SourceType, x.SourceRecordId })
                .IsUnique();

            entity.HasIndex(x => new { x.Department, x.Classification });
            entity.HasIndex(x => x.DeviceCode);
        });

        modelBuilder.Entity<DocumentVersion>(entity =>
        {
            entity.Property(x => x.SourceVersionKey).HasMaxLength(128);
            entity.Property(x => x.FileName).HasMaxLength(512);
            entity.Property(x => x.FileExtension).HasMaxLength(32);
            entity.Property(x => x.BucketName).HasMaxLength(255);
            entity.Property(x => x.B2VersionId).HasMaxLength(256);
            entity.Property(x => x.ObjectETag).HasMaxLength(256);
            entity.Property(x => x.DeclaredContentType).HasMaxLength(255);
            entity.Property(x => x.DetectedContentType).HasMaxLength(255);
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.Property(x => x.ExtractionStatus).HasMaxLength(32);
            entity.Property(x => x.ExtractionPipelineVersion).HasMaxLength(64);
            entity.Property(x => x.ExtractionErrorCode).HasMaxLength(128);
            entity.Property(x => x.ExtractionMetadataJson).HasColumnType("jsonb");

            entity.HasIndex(x => new { x.DocumentId, x.VersionNumber })
                .IsUnique();

            entity.HasIndex(x => new { x.DocumentId, x.SourceVersionKey })
                .IsUnique();

            entity.HasIndex(x => x.ExtractionStatus);
            entity.HasIndex(x => x.Sha256);

            entity.HasOne(x => x.Document)
                .WithMany(x => x.Versions)
                .HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_DocumentVersions_VersionNumber",
                    "\"VersionNumber\" > 0"
                );
                table.HasCheckConstraint(
                    "CK_DocumentVersions_SizeBytes",
                    "\"SizeBytes\" >= 0"
                );
                table.HasCheckConstraint(
                    "CK_DocumentVersions_SectionCount",
                    "\"SectionCount\" >= 0"
                );
            });
        });

        modelBuilder.Entity<ExtractionJob>(entity =>
        {
            entity.Property(x => x.PipelineVersion).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.LeaseOwner).HasMaxLength(128);
            entity.Property(x => x.LeaseToken).HasMaxLength(64);
            entity.Property(x => x.ErrorCode).HasMaxLength(128);

            entity.HasIndex(x => new { x.DocumentVersionId, x.PipelineVersion })
                .IsUnique();

            entity.HasIndex(x => new
            {
                x.Priority,
                x.NextAttemptAtUtc,
                x.CreatedAtUtc
            })
                .IsDescending(true, false, false)
                .HasFilter("\"Status\" IN ('queued', 'retry_wait')")
                .HasDatabaseName("IX_ExtractionJobs_Claim");

            entity.HasIndex(x => x.LeaseUntilUtc)
                .HasFilter("\"Status\" = 'running'")
                .HasDatabaseName("IX_ExtractionJobs_RunningLease");

            entity.HasOne(x => x.DocumentVersion)
                .WithMany(x => x.ExtractionJobs)
                .HasForeignKey(x => x.DocumentVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_ExtractionJobs_AttemptCount",
                    "\"AttemptCount\" >= 0"
                );
                table.HasCheckConstraint(
                    "CK_ExtractionJobs_MaxAttempts",
                    "\"MaxAttempts\" > 0"
                );
            });
        });

        modelBuilder.Entity<DocumentSection>(entity =>
        {
            entity.Property(x => x.PipelineVersion).HasMaxLength(64);
            entity.Property(x => x.SectionType).HasMaxLength(64);
            entity.Property(x => x.Heading).HasMaxLength(1000);
            entity.Property(x => x.ContentHash).HasMaxLength(64);
            entity.Property(x => x.Language).HasMaxLength(32);
            entity.Property(x => x.LocatorJson).HasColumnType("jsonb");
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");

            entity.HasGeneratedTsVectorColumn(
                x => x.SearchVector,
                "simple",
                x => new { x.Heading, x.Content, x.OcrContent }
            );

            entity.HasIndex(x => x.SearchVector).HasMethod("GIN");

            entity.HasIndex(x => new
            {
                x.DocumentVersionId,
                x.PipelineVersion,
                x.Ordinal
            }).IsUnique();

            entity.HasIndex(x => new
            {
                x.DocumentVersionId,
                x.PipelineVersion,
                x.SectionType,
                x.SectionNumber
            });

            entity.HasOne(x => x.DocumentVersion)
                .WithMany(x => x.Sections)
                .HasForeignKey(x => x.DocumentVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_DocumentSections_Ordinal",
                    "\"Ordinal\" >= 0"
                );
                table.HasCheckConstraint(
                    "CK_DocumentSections_CharacterCount",
                    "\"CharacterCount\" >= 0"
                );
            });
        });

        modelBuilder.Entity<DocumentDerivative>(entity =>
        {
            entity.Property(x => x.PipelineVersion).HasMaxLength(64);
            entity.Property(x => x.Kind).HasMaxLength(64);
            entity.Property(x => x.BucketName).HasMaxLength(255);
            entity.Property(x => x.B2VersionId).HasMaxLength(256);
            entity.Property(x => x.ObjectETag).HasMaxLength(256);
            entity.Property(x => x.ContentType).HasMaxLength(255);
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");

            entity.HasIndex(x => new
            {
                x.DocumentVersionId,
                x.PipelineVersion,
                x.Kind,
                x.Ordinal
            }).IsUnique();

            entity.HasOne(x => x.DocumentVersion)
                .WithMany(x => x.Derivatives)
                .HasForeignKey(x => x.DocumentVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_DocumentDerivatives_Ordinal",
                    "\"Ordinal\" >= 0"
                );
                table.HasCheckConstraint(
                    "CK_DocumentDerivatives_SizeBytes",
                    "\"SizeBytes\" >= 0"
                );
            });
        });
    }
}
