using System.Globalization;
using System.Xml.Linq;

namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class XlsxExtractor : IFileExtractor
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly ExtractionLimits _limits;

    public XlsxExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? new ExtractionLimits();
    }

    public bool CanExtract(ExtractionRequest request) =>
        ExtractionUtilities.Extension(request) == ".xlsx" ||
        string.Equals(
            request.DeclaredContentType,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ExtractionUtilities.ValidateInputFile(request, _limits);
        using var package = new SafeZipPackage(request.FilePath, _limits);
        var workbook = package.LoadRequiredXml("xl/workbook.xml");
        var workbookRelationships = SafeZipPackage.ReadInternalRelationships(package, "xl/workbook.xml");
        var sharedStrings = ReadSharedStrings(package);
        var dateStyles = ReadDateStyleIndexes(package);
        var uses1904DateSystem = Uses1904DateSystem(workbook);

        var sections = new List<ExtractedSection>();
        foreach (var sheet in workbook.Descendants(Spreadsheet + "sheet"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = (string?)sheet.Attribute("name") ?? $"Sheet{sections.Count + 1}";
            var relationshipId = (string?)sheet.Attribute(Relationships + "id");
            if (relationshipId is null || !workbookRelationships.TryGetValue(relationshipId, out var sheetPart))
            {
                continue;
            }

            sections.Add(ReadSheet(
                package,
                sheetPart,
                name,
                sections.Count + 1,
                sharedStrings,
                dateStyles,
                uses1904DateSystem,
                cancellationToken));
        }

        return Task.FromResult(new ExtractedDocument(
            "xlsx",
            sections,
            new Dictionary<string, object?>
            {
                ["workbook_name"] = Path.GetFileNameWithoutExtension(request.FileName),
                ["sheet_count"] = sections.Count,
                ["sheet_names"] = sections.Select(section => section.Heading).ToArray(),
                ["date_system"] = uses1904DateSystem ? "1904" : "1900"
            },
            Array.Empty<string>()));
    }

    private ExtractedSection ReadSheet(
        SafeZipPackage package,
        string sheetPart,
        string sheetName,
        int sheetNumber,
        IReadOnlyList<string> sharedStrings,
        IReadOnlySet<int> dateStyles,
        bool uses1904DateSystem,
        CancellationToken cancellationToken)
    {
        var worksheet = package.LoadRequiredXml(sheetPart);
        var usedRange = (string?)worksheet.Descendants(Spreadsheet + "dimension").FirstOrDefault()?.Attribute("ref");
        var nonEmptyRows = new List<IReadOnlyList<string?>>();
        var rowCount = 0;
        var maxColumnCount = 0;

        foreach (var rowElement in worksheet.Descendants(Spreadsheet + "sheetData").Elements(Spreadsheet + "row"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cellsByColumn = new SortedDictionary<int, string?>();
            var implicitColumn = 0;
            foreach (var cell in rowElement.Elements(Spreadsheet + "c"))
            {
                var referencedColumn = ExtractionUtilities.ColumnIndexFromReference((string?)cell.Attribute("r"));
                var column = referencedColumn >= 0 ? referencedColumn : implicitColumn;
                implicitColumn = column + 1;
                if (column >= _limits.MaxTableColumns)
                {
                    throw new ExtractionException($"XLSX sheet '{sheetName}' exceeds {_limits.MaxTableColumns} columns.");
                }

                var value = ReadCellValue(
                    cell,
                    sharedStrings,
                    dateStyles,
                    uses1904DateSystem
                );
                if (value?.Length > _limits.MaxCellCharacters)
                {
                    throw new ExtractionException($"XLSX cell exceeds {_limits.MaxCellCharacters} characters.");
                }

                if (value is not null)
                {
                    cellsByColumn[column] = value;
                }
            }

            if (cellsByColumn.Count == 0)
            {
                continue;
            }

            rowCount++;
            if (rowCount > _limits.MaxTableRows)
            {
                throw new ExtractionException($"XLSX sheet '{sheetName}' exceeds {_limits.MaxTableRows} non-empty rows.");
            }

            maxColumnCount = Math.Max(maxColumnCount, cellsByColumn.Keys.Max() + 1);
            if (nonEmptyRows.Count < _limits.SampleRowCount + 1)
            {
                var row = new string?[cellsByColumn.Keys.Max() + 1];
                foreach (var (column, value) in cellsByColumn)
                {
                    row[column] = value;
                }

                nonEmptyRows.Add(row);
            }
        }

        var header = nonEmptyRows.FirstOrDefault() ?? Array.Empty<string?>();
        var columnNames = MakeUniqueColumnNames(header, maxColumnCount);
        var inferredTypes = Enumerable.Repeat("null", columnNames.Count).ToArray();
        var samples = new List<IReadOnlyDictionary<string, string?>>();

        foreach (var row in nonEmptyRows.Skip(1).Take(_limits.SampleRowCount))
        {
            var sample = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var column = 0; column < columnNames.Count; column++)
            {
                var value = column < row.Count ? row[column] : null;
                sample[columnNames[column]] = value;
                inferredTypes[column] = ExtractionUtilities.MergeScalarTypes(
                    inferredTypes[column],
                    ExtractionUtilities.InferScalarType(value));
            }

            samples.Add(sample);
        }

        var columns = columnNames
            .Select((name, index) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["name"] = name,
                ["inferred_type"] = inferredTypes[index]
            })
            .ToArray();
        var tables = ReadTableBoundaries(package, sheetPart);

        return new ExtractedSection(
            "sheet",
            sheetNumber,
            sheetName,
            $"Sheet: {sheetName}{Environment.NewLine}Columns: {string.Join(", ", columnNames)}",
            new Dictionary<string, object?>
            {
                ["sheet_number"] = sheetNumber,
                ["sheet_name"] = sheetName,
                ["part_name"] = sheetPart,
                ["used_range"] = usedRange,
                ["row_count"] = Math.Max(0, rowCount - (header.Count > 0 ? 1 : 0)),
                ["column_count"] = columnNames.Count,
                ["columns"] = columns,
                ["sample_rows"] = samples,
                ["table_boundaries"] = tables
            });
    }

    private IReadOnlyList<string> ReadSharedStrings(SafeZipPackage package)
    {
        var document = package.LoadOptionalXml("xl/sharedStrings.xml");
        if (document?.Root is null)
        {
            return Array.Empty<string>();
        }

        var strings = new List<string>();
        foreach (var item in document.Root.Elements(Spreadsheet + "si"))
        {
            if (strings.Count >= _limits.MaxTableRows * 10L)
            {
                throw new ExtractionException("XLSX shared string table exceeds the configured safety limit.");
            }

            var value = string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value));
            if (value.Length > _limits.MaxCellCharacters)
            {
                throw new ExtractionException($"XLSX shared string exceeds {_limits.MaxCellCharacters} characters.");
            }

            strings.Add(value);
        }

        return strings;
    }

    private static IReadOnlySet<int> ReadDateStyleIndexes(SafeZipPackage package)
    {
        var styles = package.LoadOptionalXml("xl/styles.xml");
        if (styles?.Root is null)
        {
            return new HashSet<int>();
        }

        var customFormats = styles.Descendants(Spreadsheet + "numFmt")
            .Select(element => new
            {
                Id = (int?)element.Attribute("numFmtId"),
                Code = (string?)element.Attribute("formatCode")
            })
            .Where(format => format.Id.HasValue && LooksLikeDateFormat(format.Code))
            .Select(format => format.Id!.Value)
            .ToHashSet();

        var result = new HashSet<int>();
        var indexes = 0;
        var cellFormats = styles.Root.Element(Spreadsheet + "cellXfs");
        if (cellFormats is null)
        {
            return result;
        }

        foreach (var format in cellFormats.Elements(Spreadsheet + "xf"))
        {
            var numberFormat = (int?)format.Attribute("numFmtId") ?? 0;
            if (IsBuiltInDateFormat(numberFormat) || customFormats.Contains(numberFormat))
            {
                result.Add(indexes);
            }

            indexes++;
        }

        return result;
    }

    private static string? ReadCellValue(
        XElement cell,
        IReadOnlyList<string> sharedStrings,
        IReadOnlySet<int> dateStyles,
        bool uses1904DateSystem)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(Spreadsheet + "t").Select(text => text.Value));
        }

        var raw = cell.Element(Spreadsheet + "v")?.Value;
        if (raw is null)
        {
            return null;
        }

        if (type == "s")
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                   index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : null;
        }

        if (type == "b")
        {
            return raw == "1" ? "true" : "false";
        }

        var style = (int?)cell.Attribute("s");
        if (style.HasValue && dateStyles.Contains(style.Value) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serialDate) &&
            serialDate is >= -657435 and <= 2958465)
        {
            try
            {
                var adjustedSerial = uses1904DateSystem
                    ? serialDate + 1462
                    : serialDate;
                return DateTime.FromOADate(adjustedSerial).ToString("O", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                // Preserve the cached numeric value if the producer used a non-standard date serial.
            }
        }

        return raw;
    }

    private static bool Uses1904DateSystem(XDocument workbook)
    {
        var value = (string?)workbook
            .Descendants(Spreadsheet + "workbookPr")
            .FirstOrDefault()?
            .Attribute("date1904");
        return value is "1" or "true" or "TRUE";
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTableBoundaries(
        SafeZipPackage package,
        string sheetPart)
    {
        var relationships = SafeZipPackage.ReadInternalRelationships(package, sheetPart);
        var tables = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var part in relationships.Values.Where(path => path.Contains("/tables/", StringComparison.OrdinalIgnoreCase)))
        {
            if (!package.Contains(part))
            {
                continue;
            }

            var table = package.LoadRequiredXml(part).Root;
            if (table is null)
            {
                continue;
            }

            tables.Add(new Dictionary<string, object?>
            {
                ["name"] = (string?)table.Attribute("displayName") ?? (string?)table.Attribute("name"),
                ["range"] = (string?)table.Attribute("ref"),
                ["part_name"] = part
            });
        }

        return tables;
    }

    private static IReadOnlyList<string> MakeUniqueColumnNames(IReadOnlyList<string?> header, int columnCount)
    {
        var names = new List<string>(columnCount);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columnCount; index++)
        {
            var headerValue = index < header.Count ? header[index] : null;
            var baseName = string.IsNullOrWhiteSpace(headerValue)
                ? $"Column{index + 1}"
                : headerValue.Trim();
            counts.TryGetValue(baseName, out var count);
            count++;
            counts[baseName] = count;
            names.Add(count == 1 ? baseName : $"{baseName}_{count}");
        }

        return names;
    }

    private static bool IsBuiltInDateFormat(int id) =>
        id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58;

    private static bool LooksLikeDateFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        var cleaned = format.ToLowerInvariant();
        return cleaned.Contains('y') || cleaned.Contains('d') || cleaned.Contains("h:") || cleaned.Contains("m/");
    }
}
