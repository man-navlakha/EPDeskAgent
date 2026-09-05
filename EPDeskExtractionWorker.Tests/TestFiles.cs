using System.IO.Compression;
using System.Text;

namespace EPDeskExtractionWorker.Tests;

internal sealed class TestFiles : IDisposable
{
    public TestFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "epdesk-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string WriteText(string fileName, string content)
    {
        var path = Path.Combine(Root, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public string WriteBytes(string fileName, params byte[] content)
    {
        var path = Path.Combine(Root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string WriteZip(string fileName, IReadOnlyDictionary<string, string> entries)
    {
        var path = Path.Combine(Root, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
