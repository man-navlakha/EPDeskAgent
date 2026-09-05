using System.Xml.Linq;

namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class PptxExtractor : IFileExtractor
{
    private static readonly XNamespace Presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly ExtractionLimits _limits;

    public PptxExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? new ExtractionLimits();
    }

    public bool CanExtract(ExtractionRequest request) =>
        ExtractionUtilities.Extension(request) == ".pptx" ||
        string.Equals(
            request.DeclaredContentType,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ExtractionUtilities.ValidateInputFile(request, _limits);
        using var package = new SafeZipPackage(request.FilePath, _limits);
        var presentation = package.LoadRequiredXml("ppt/presentation.xml");
        var relationships = SafeZipPackage.ReadInternalRelationships(package, "ppt/presentation.xml");

        var slideParts = presentation
            .Descendants(Presentation + "sldId")
            .Select(element => (string?)element.Attribute(Relationships + "id"))
            .Where(id => id is not null && relationships.ContainsKey(id))
            .Select(id => relationships[id!])
            .ToArray();

        var sections = new List<ExtractedSection>(slideParts.Length);
        for (var index = 0; index < slideParts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sections.Add(ReadSlide(package, slideParts[index], index + 1, request.IncludeSpeakerNotes));
        }

        return Task.FromResult(new ExtractedDocument(
            "pptx",
            sections,
            new Dictionary<string, object?>
            {
                ["slide_count"] = sections.Count,
                ["speaker_notes_included"] = request.IncludeSpeakerNotes
            },
            Array.Empty<string>()));
    }

    private ExtractedSection ReadSlide(
        SafeZipPackage package,
        string slidePart,
        int slideNumber,
        bool includeSpeakerNotes)
    {
        var slide = package.LoadRequiredXml(slidePart);
        var textBoxes = new List<IReadOnlyDictionary<string, object?>>();
        string? title = null;

        foreach (var shape in slide.Descendants(Presentation + "sp"))
        {
            var text = JoinDrawingText(shape);
            if (text.Length == 0)
            {
                continue;
            }

            var placeholderType = (string?)shape
                .Element(Presentation + "nvSpPr")?
                .Element(Presentation + "nvPr")?
                .Element(Presentation + "ph")?
                .Attribute("type");
            var shapeName = (string?)shape
                .Element(Presentation + "nvSpPr")?
                .Element(Presentation + "cNvPr")?
                .Attribute("name");

            if (title is null && placeholderType is "title" or "ctrTitle")
            {
                title = text;
            }

            textBoxes.Add(new Dictionary<string, object?>
            {
                ["name"] = shapeName,
                ["placeholder_type"] = placeholderType,
                ["text"] = text
            });
        }

        var tables = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var table in slide.Descendants(Drawing + "tbl"))
        {
            if (tables.Count >= _limits.MaxTableRows)
            {
                throw new ExtractionException("PPTX contains too many tables on one slide.");
            }

            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in table.Elements(Drawing + "tr"))
            {
                if (rows.Count >= _limits.MaxTableRows)
                {
                    throw new ExtractionException($"PPTX table exceeds {_limits.MaxTableRows} rows.");
                }

                var cells = row.Elements(Drawing + "tc").Select(JoinDrawingText).ToArray();
                if (cells.Length > _limits.MaxTableColumns)
                {
                    throw new ExtractionException($"PPTX table exceeds {_limits.MaxTableColumns} columns.");
                }

                rows.Add(cells);
            }

            tables.Add(new Dictionary<string, object?>
            {
                ["row_count"] = rows.Count,
                ["column_count"] = rows.Count == 0 ? 0 : rows.Max(row => row.Count),
                ["rows"] = rows
            });
        }

        string? notes = null;
        if (includeSpeakerNotes)
        {
            var relationships = SafeZipPackage.ReadInternalRelationships(package, slidePart);
            var notesPart = relationships.Values.FirstOrDefault(path =>
                path.Contains("/notesSlides/", StringComparison.OrdinalIgnoreCase));
            if (notesPart is not null && package.Contains(notesPart))
            {
                var notesDocument = package.LoadRequiredXml(notesPart);
                notes = string.Join(
                    Environment.NewLine,
                    notesDocument.Descendants(Presentation + "sp")
                        .Where(shape => !IsNotesMetadataPlaceholder(shape))
                        .Select(JoinDrawingText)
                        .Where(text => text.Length > 0));
                notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
            }
        }

        var searchableParts = textBoxes
            .Select(box => box["text"] as string)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .ToList();
        searchableParts.AddRange(tables.SelectMany(table =>
            ((IReadOnlyList<IReadOnlyList<string>>)table["rows"]!).Select(row => string.Join(" | ", row))));
        if (notes is not null)
        {
            searchableParts.Add(notes);
        }

        return new ExtractedSection(
            "slide",
            slideNumber,
            title,
            string.Join(Environment.NewLine, searchableParts.Distinct(StringComparer.Ordinal)),
            new Dictionary<string, object?>
            {
                ["slide_number"] = slideNumber,
                ["part_name"] = slidePart,
                ["title"] = title,
                ["text_boxes"] = textBoxes,
                ["tables"] = tables,
                ["speaker_notes"] = notes
            });
    }

    private static bool IsNotesMetadataPlaceholder(XElement shape)
    {
        var type = (string?)shape
            .Element(Presentation + "nvSpPr")?
            .Element(Presentation + "nvPr")?
            .Element(Presentation + "ph")?
            .Attribute("type");
        return type is "sldImg" or "dt" or "ftr" or "sldNum";
    }

    private static string JoinDrawingText(XContainer container) =>
        string.Join(
            Environment.NewLine,
            container.Descendants(Drawing + "p")
                .Select(paragraph => string.Concat(paragraph.Descendants(Drawing + "t").Select(text => text.Value)))
                .Where(text => !string.IsNullOrWhiteSpace(text)))
        .Trim();
}
