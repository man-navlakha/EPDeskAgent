using EPDeskExtractionWorker.Services.Processing;

namespace EPDeskExtractionWorker.Tests;

public sealed class FileTypeInspectorTests
{
    private readonly FileTypeInspector _inspector = new();

    [Fact]
    public async Task InspectAsync_AcceptsTextCsvWithGenericDeclaredType()
    {
        using var files = new TestFiles();
        var path = files.WriteText("orders.csv", "id,name\n1,Acme\n");

        var result = await _inspector.InspectAsync(
            path,
            "orders.csv",
            "application/octet-stream",
            CancellationToken.None);

        Assert.Equal(".csv", result.Extension);
        Assert.Equal("text/plain", result.DetectedContentType);
    }

    [Fact]
    public async Task InspectAsync_DetectsOpenXmlFromPackageParts()
    {
        using var files = new TestFiles();
        var path = files.WriteZip("document.docx", new Dictionary<string, string>
        {
            ["word/document.xml"] = "<document />"
        });

        var result = await _inspector.InspectAsync(
            path,
            "document.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            CancellationToken.None);

        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            result.DetectedContentType);
    }

    [Fact]
    public async Task InspectAsync_RejectsContentThatConflictsWithExtension()
    {
        using var files = new TestFiles();
        var path = files.WriteText("not-office.docx", "%PDF-1.7\n");

        var exception = await Assert.ThrowsAsync<RejectedExtractionException>(() =>
            _inspector.InspectAsync(
                path,
                "not-office.docx",
                "application/octet-stream",
                CancellationToken.None));

        Assert.Equal("mime_mismatch", exception.ErrorCode);
    }

    [Fact]
    public async Task InspectAsync_RejectsUnsupportedExtensionBeforeProcessing()
    {
        using var files = new TestFiles();
        var path = files.WriteBytes("archive.exe", 0x4D, 0x5A);

        var exception = await Assert.ThrowsAsync<RejectedExtractionException>(() =>
            _inspector.InspectAsync(
                path,
                "archive.exe",
                "application/octet-stream",
                CancellationToken.None));

        Assert.Equal("unsupported_format", exception.ErrorCode);
    }
}
