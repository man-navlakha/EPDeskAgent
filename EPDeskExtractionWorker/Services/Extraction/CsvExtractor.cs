using System.Text;

namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class CsvExtractor : IFileExtractor
{
    private readonly ExtractionLimits _limits;

    public CsvExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? new ExtractionLimits();
    }

    public bool CanExtract(ExtractionRequest request)
    {
        var extension = ExtractionUtilities.Extension(request);
        var contentType = request.DeclaredContentType ?? string.Empty;
        return extension is ".csv" or ".tsv" ||
               contentType.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("text/tab-separated-values", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ExtractionUtilities.ValidateInputFile(request, _limits);
        var delimiter = ExtractionUtilities.Extension(request) == ".tsv" ? '\t' : ',';

        await using var stream = new FileStream(
            request.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
        var records = new DelimitedRecordReader(reader, delimiter, _limits.MaxCellCharacters, _limits.MaxTableColumns);

        var header = await records.ReadRecordAsync(cancellationToken);
        if (header is null)
        {
            return Empty(delimiter);
        }

        var columnNames = MakeUniqueColumnNames(header);
        var inferredTypes = Enumerable.Repeat("null", columnNames.Count).ToArray();
        var samples = new List<IReadOnlyDictionary<string, string?>>();
        var dataRowCount = 0;

        while (await records.ReadRecordAsync(cancellationToken) is { } row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dataRowCount++;
            if (dataRowCount > _limits.MaxTableRows)
            {
                throw new ExtractionException($"Delimited file has more than {_limits.MaxTableRows} data rows.");
            }

            for (var column = 0; column < columnNames.Count; column++)
            {
                var value = column < row.Count ? row[column] : null;
                inferredTypes[column] = ExtractionUtilities.MergeScalarTypes(
                    inferredTypes[column],
                    ExtractionUtilities.InferScalarType(value));
            }

            if (samples.Count < _limits.SampleRowCount)
            {
                samples.Add(columnNames
                    .Select((name, index) => new KeyValuePair<string, string?>(name, index < row.Count ? row[index] : null))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            }
        }

        var columns = columnNames
            .Select((name, index) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["name"] = name,
                ["inferred_type"] = inferredTypes[index]
            })
            .ToArray();
        var boundary = columnNames.Count == 0
            ? null
            : $"A1:{ExtractionUtilities.ColumnName(columnNames.Count - 1)}{dataRowCount + 1}";

        var sectionMetadata = new Dictionary<string, object?>
        {
            ["columns"] = columns,
            ["row_count"] = dataRowCount,
            ["sample_rows"] = samples,
            ["table_boundary"] = boundary,
            ["delimiter"] = delimiter.ToString()
        };

        return new ExtractedDocument(
            delimiter == '\t' ? "tsv" : "csv",
            new[]
            {
                new ExtractedSection(
                    "table",
                    1,
                    Path.GetFileNameWithoutExtension(request.FileName),
                    $"Columns: {string.Join(", ", columnNames)}",
                    sectionMetadata)
            },
            new Dictionary<string, object?>
            {
                ["row_count"] = dataRowCount,
                ["column_count"] = columnNames.Count,
                ["table_boundary"] = boundary
            },
            Array.Empty<string>());
    }

    private static IReadOnlyList<string> MakeUniqueColumnNames(IReadOnlyList<string> header)
    {
        var names = new List<string>(header.Count);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < header.Count; index++)
        {
            var baseName = string.IsNullOrWhiteSpace(header[index]) ? $"Column{index + 1}" : header[index].Trim();
            counts.TryGetValue(baseName, out var count);
            count++;
            counts[baseName] = count;
            names.Add(count == 1 ? baseName : $"{baseName}_{count}");
        }

        return names;
    }

    private static ExtractedDocument Empty(char delimiter) => new(
        delimiter == '\t' ? "tsv" : "csv",
        Array.Empty<ExtractedSection>(),
        new Dictionary<string, object?> { ["row_count"] = 0, ["column_count"] = 0 },
        new[] { "The delimited file was empty." });

    private sealed class DelimitedRecordReader
    {
        private readonly TextReader _reader;
        private readonly char _delimiter;
        private readonly int _maxCellCharacters;
        private readonly int _maxColumns;
        private readonly char[] _buffer = new char[16 * 1024];
        private int _bufferIndex;
        private int _bufferLength;
        private bool _skipLineFeed;

        public DelimitedRecordReader(
            TextReader reader,
            char delimiter,
            int maxCellCharacters,
            int maxColumns)
        {
            _reader = reader;
            _delimiter = delimiter;
            _maxCellCharacters = maxCellCharacters;
            _maxColumns = maxColumns;
        }

        public async Task<IReadOnlyList<string>?> ReadRecordAsync(CancellationToken cancellationToken)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var afterClosingQuote = false;
            var sawAnyCharacter = false;

            while (true)
            {
                var next = await ReadCharacterAsync(cancellationToken);
                if (next < 0)
                {
                    if (inQuotes)
                    {
                        throw new ExtractionException("Delimited file ended inside a quoted field.");
                    }

                    if (!sawAnyCharacter && fields.Count == 0 && field.Length == 0)
                    {
                        return null;
                    }

                    AddField(fields, field);
                    return fields;
                }

                var character = (char)next;
                if (_skipLineFeed)
                {
                    _skipLineFeed = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }

                sawAnyCharacter = true;
                if (inQuotes)
                {
                    if (character == '"')
                    {
                        inQuotes = false;
                        afterClosingQuote = true;
                    }
                    else
                    {
                        Append(field, character);
                    }

                    continue;
                }

                if (afterClosingQuote)
                {
                    if (character == '"')
                    {
                        Append(field, '"');
                        inQuotes = true;
                        afterClosingQuote = false;
                        continue;
                    }

                    if (character is ' ' or '\t')
                    {
                        continue;
                    }

                    if (character != _delimiter && character is not '\r' and not '\n')
                    {
                        throw new ExtractionException("Unexpected character after a closing CSV quote.");
                    }

                    afterClosingQuote = false;
                }

                if (character == _delimiter)
                {
                    AddField(fields, field);
                    continue;
                }

                if (character is '\r' or '\n')
                {
                    _skipLineFeed = character == '\r';
                    AddField(fields, field);
                    return fields;
                }

                if (character == '"' && field.Length == 0)
                {
                    inQuotes = true;
                    continue;
                }

                Append(field, character);
            }
        }

        private async ValueTask<int> ReadCharacterAsync(CancellationToken cancellationToken)
        {
            if (_bufferIndex >= _bufferLength)
            {
                _bufferLength = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                _bufferIndex = 0;
                if (_bufferLength == 0)
                {
                    return -1;
                }
            }

            return _buffer[_bufferIndex++];
        }

        private void AddField(List<string> fields, StringBuilder field)
        {
            if (fields.Count >= _maxColumns)
            {
                throw new ExtractionException($"Delimited row has more than {_maxColumns} columns.");
            }

            fields.Add(field.ToString());
            field.Clear();
        }

        private void Append(StringBuilder field, char character)
        {
            if (field.Length >= _maxCellCharacters)
            {
                throw new ExtractionException($"Delimited field exceeds {_maxCellCharacters} characters.");
            }

            field.Append(character);
        }
    }
}
