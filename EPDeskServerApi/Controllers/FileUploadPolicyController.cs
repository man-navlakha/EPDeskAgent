using System.Text.Json;
using EPDeskServerApi.Models;
using EPDeskServerApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace EPDeskServerApi.Controllers;

[ApiController]
public sealed class FileUploadPolicyController : ControllerBase
{
    private readonly FileUploadPolicyService _policyService;

    public FileUploadPolicyController(FileUploadPolicyService policyService)
    {
        _policyService = policyService;
    }

    [HttpGet("api/agent/file-uploads/policy")]
    [HttpGet("api/admin/file-upload-policy")]
    public async Task<IActionResult> GetPolicy(CancellationToken cancellationToken)
    {
        var policy = await _policyService.GetOrCreateAsync(cancellationToken);

        return Ok(ToResponse(policy));
    }

    [HttpPut("api/admin/file-upload-policy")]
    public async Task<IActionResult> UpdatePolicy(
        UpdateFileUploadPolicyDto dto,
        CancellationToken cancellationToken)
    {
        if (dto.Extensions == null || dto.Extensions.Count == 0)
        {
            return BadRequest("At least one file extension is required.");
        }

        var extensions = FileUploadPolicyService.NormalizeExtensions(dto.Extensions);

        if (extensions.Count == 0)
        {
            return BadRequest("At least one valid file extension is required.");
        }

        if (extensions.Count > 500)
        {
            return BadRequest("A maximum of 500 file extensions is allowed.");
        }

        if (extensions.Any(x => x.Length > 32))
        {
            return BadRequest("A file extension cannot be longer than 32 characters.");
        }

        if (dto.MaxFileSizeBytes <= 0)
        {
            return BadRequest("Maximum file size must be greater than zero.");
        }

        var policy = await _policyService.GetOrCreateAsync(cancellationToken);

        policy.IsEnabled = dto.IsEnabled;
        policy.ExtensionsJson = JsonSerializer.Serialize(extensions);
        policy.MaxFileSizeBytes = dto.MaxFileSizeBytes;
        policy.UpdatedAtUtc = DateTime.UtcNow;

        await _policyService.SaveChangesAsync(cancellationToken);

        return Ok(ToResponse(policy));
    }

    private static object ToResponse(FileUploadPolicy policy)
    {
        return new
        {
            policy.IsEnabled,
            Extensions = FileUploadPolicyService.ReadExtensions(policy),
            policy.MaxFileSizeBytes,
            policy.UpdatedAtUtc
        };
    }
}

public sealed class UpdateFileUploadPolicyDto
{
    public bool IsEnabled { get; set; } = true;

    public List<string> Extensions { get; set; } = [];

    public long MaxFileSizeBytes { get; set; } = 1024L * 1024 * 1024;
}
