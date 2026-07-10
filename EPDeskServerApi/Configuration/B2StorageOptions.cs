namespace EPDeskServerApi.Configuration;

/// <summary>
/// Configuration required for Backblaze B2 object storage.
/// Secret values will come from environment variables and will
/// not be committed to GitHub.
/// </summary>
public sealed class B2StorageOptions
{
    public const string SectionName = "B2";

    /// <summary>
    /// Backblaze S3-compatible API endpoint.
    /// Example: https://s3.us-west-004.backblazeb2.com
    /// </summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Backblaze bucket region.
    /// Example: us-west-004
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Name of the private Backblaze bucket.
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// Restricted Backblaze application key ID.
    /// Never commit the real value to GitHub.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// Restricted Backblaze application key secret.
    /// Never commit the real value to GitHub.
    /// </summary>
    public string ApplicationKey { get; set; } = string.Empty;

    /// <summary>
    /// Default multipart upload part size.
    /// 104857600 bytes equals 100 MiB.
    /// </summary>
    public long PartSizeBytes { get; set; } = 104_857_600;

    /// <summary>
    /// Number of minutes that a part-upload URL remains usable.
    /// </summary>
    public int PresignedUrlMinutes { get; set; } = 30;

    /// <summary>
    /// Number of minutes that a temporary download URL remains usable.
    /// </summary>
    public int DownloadUrlMinutes { get; set; } = 10;
}