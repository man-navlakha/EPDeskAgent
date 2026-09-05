using System.IO.Compression;
using System.Xml.Linq;

namespace EPDeskExtractionWorker.Services.Extraction;

internal sealed class SafeZipPackage : IDisposable
{
    private readonly FileStream _file;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly ExtractionLimits _limits;

    public SafeZipPackage(string path, ExtractionLimits limits)
    {
        _limits = limits;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        try
        {
            _archive = new ZipArchive(_file, ZipArchiveMode.Read, leaveOpen: true);
            _entries = ValidateAndIndexEntries(_archive, limits);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public IEnumerable<string> EntryNames => _entries.Keys;

    public bool Contains(string partName) => _entries.ContainsKey(NormalizePartName(partName));

    public Stream OpenRequired(string partName)
    {
        var normalized = NormalizePartName(partName);
        if (!_entries.TryGetValue(normalized, out var entry))
        {
            throw new ExtractionException($"Required package part '{normalized}' was not found.");
        }

        return new BoundedReadStream(entry.Open(), _limits.MaxArchiveEntryBytes, normalized);
    }

    public Stream? OpenOptional(string partName)
    {
        var normalized = NormalizePartName(partName);
        return _entries.TryGetValue(normalized, out var entry)
            ? new BoundedReadStream(entry.Open(), _limits.MaxArchiveEntryBytes, normalized)
            : null;
    }

    public XDocument LoadRequiredXml(string partName)
    {
        using var stream = OpenRequired(partName);
        return ExtractionUtilities.LoadXml(stream, _limits.MaxXmlCharacters);
    }

    public XDocument? LoadOptionalXml(string partName)
    {
        using var stream = OpenOptional(partName);
        return stream is null ? null : ExtractionUtilities.LoadXml(stream, _limits.MaxXmlCharacters);
    }

    public void Dispose()
    {
        _archive.Dispose();
        _file.Dispose();
    }

    public static string ResolveTarget(string sourcePart, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ExtractionException("An Open XML relationship has an empty target.");
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            throw new ExtractionException("External package relationships are not read during extraction.");
        }

        var decoded = Uri.UnescapeDataString(target.Replace('\\', '/'));
        var combined = decoded.StartsWith("/", StringComparison.Ordinal)
            ? decoded.TrimStart('/')
            : $"{Path.GetDirectoryName(sourcePart)?.Replace('\\', '/')}/{decoded}";

        var segments = new Stack<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ExtractionException("A package relationship escapes the package root.");
                }

                segments.Pop();
                continue;
            }

            segments.Push(segment);
        }

        return string.Join('/', segments.Reverse());
    }

    public static string RelationshipPartName(string sourcePart)
    {
        var directory = Path.GetDirectoryName(sourcePart)?.Replace('\\', '/');
        var file = Path.GetFileName(sourcePart);
        return string.IsNullOrEmpty(directory)
            ? $"_rels/{file}.rels"
            : $"{directory}/_rels/{file}.rels";
    }

    public static IReadOnlyDictionary<string, string> ReadInternalRelationships(
        SafeZipPackage package,
        string sourcePart)
    {
        XNamespace relationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
        var relationshipDocument = package.LoadOptionalXml(RelationshipPartName(sourcePart));
        if (relationshipDocument?.Root is null)
        {
            return new Dictionary<string, string>();
        }

        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relationship in relationshipDocument.Root.Elements(relationshipsNamespace + "Relationship"))
        {
            if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            relationships[id] = ResolveTarget(sourcePart, target);
        }

        return relationships;
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateAndIndexEntries(
        ZipArchive archive,
        ExtractionLimits limits)
    {
        if (archive.Entries.Count > limits.MaxArchiveEntries)
        {
            throw new ExtractionException($"Archive has more than {limits.MaxArchiveEntries} entries.");
        }

        long expandedTotal = 0;
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = NormalizePartName(entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (entry.Length > limits.MaxArchiveEntryBytes)
            {
                throw new ExtractionException(
                    $"Archive entry '{name}' exceeds the {limits.MaxArchiveEntryBytes} byte limit.");
            }

            expandedTotal = checked(expandedTotal + entry.Length);
            if (expandedTotal > limits.MaxArchiveExpandedBytes)
            {
                throw new ExtractionException(
                    $"Archive expands beyond the {limits.MaxArchiveExpandedBytes} byte limit.");
            }

            if (entry.Length > 0)
            {
                var denominator = Math.Max(1, entry.CompressedLength);
                var ratio = (double)entry.Length / denominator;
                if (ratio > limits.MaxCompressionRatio)
                {
                    throw new ExtractionException(
                        $"Archive entry '{name}' has a suspicious {ratio:F1}:1 compression ratio.");
                }
            }

            if (!entries.TryAdd(name, entry))
            {
                throw new ExtractionException($"Archive contains duplicate entry '{name}'.");
            }
        }

        return entries;
    }

    private static string NormalizePartName(string partName)
    {
        var normalized = partName.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new ExtractionException("Archive entry path traversal was rejected.");
        }

        return normalized;
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private readonly string _name;
        private long _read;

        public BoundedReadStream(Stream inner, long limit, string name)
        {
            _inner = inner;
            _limit = limit;
            _name = name;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Count(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            Count(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Count(int count)
        {
            _read = checked(_read + count);
            if (_read > _limit)
            {
                throw new ExtractionException($"Archive entry '{_name}' exceeded its read limit.");
            }
        }
    }
}
