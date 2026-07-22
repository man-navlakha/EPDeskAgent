using System.Text.Json;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Services;

public sealed class FileUploadPolicyService
{
    private static readonly Guid DefaultPolicyId =
        Guid.Parse("8d0417b5-92b9-4a7b-891a-cbf18cb943ea");

    public static readonly string[] DefaultExtensions =
    [
        ".pdf",
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".rtf", ".txt", ".odt",
        ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm", ".xla", ".xlam", ".xlw",
        ".csv", ".xml", ".dif", ".slk", ".ods",
        ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".ppsm", ".pot", ".potx", ".potm", ".ppa", ".ppam", ".odp",
        ".vsd", ".vsdx", ".vsdm", ".vss", ".vssx", ".vst", ".vstx", ".vstm",
        ".one", ".onepkg", ".pub", ".mpp", ".mpt",
        ".mdb", ".accdb", ".accde", ".accdt", ".accdr",
        ".pst", ".ost", ".msg", ".eml", ".xps", ".oxps"
    ];

    private readonly AppDbContext _db;

    public FileUploadPolicyService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<FileUploadPolicy> GetOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        var policy = await _db.FileUploadPolicies
            .FirstOrDefaultAsync(x => x.Id == DefaultPolicyId, cancellationToken);

        if (policy != null)
        {
            return policy;
        }

        policy = new FileUploadPolicy
        {
            Id = DefaultPolicyId,
            IsEnabled = true,
            ExtensionsJson = JsonSerializer.Serialize(DefaultExtensions),
            MaxFileSizeBytes = 1024L * 1024 * 1024,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.FileUploadPolicies.Add(policy);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another agent may have created the singleton policy concurrently.
            _db.Entry(policy).State = EntityState.Detached;
            policy = await _db.FileUploadPolicies.FirstAsync(
                x => x.Id == DefaultPolicyId,
                cancellationToken
            );
        }

        return policy;
    }

    public static List<string> ReadExtensions(FileUploadPolicy policy)
    {
        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(
                policy.ExtensionsJson
            ) ?? [];

            return NormalizeExtensions(values);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static List<string> NormalizeExtensions(IEnumerable<string> extensions)
    {
        return extensions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().TrimStart('.'))
            .Where(x => x.Length > 0)
            .Select(x => "." + x.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    public Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }
}
