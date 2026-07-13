namespace EPDeskServerApi.Models;

public class DeviceScanExclusion
{
    public Guid Id { get; set; }

    public string DeviceCode { get; set; } = "";

    public string ExclusionType { get; set; } = "";
    // folder_path, folder_name, file_extension

    public string Value { get; set; } = "";

    public string CreatedBy { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;
}
