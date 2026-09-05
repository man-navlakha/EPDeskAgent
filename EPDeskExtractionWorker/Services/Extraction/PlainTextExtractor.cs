namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class PlainTextExtractor : IFileExtractor
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".text", ".md", ".markdown" };

    private readonly ExtractionLimits _limits;

    public PlainTextExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? new ExtractionLimits();
    }

    public bool CanExtract(ExtractionRequest request)
    {
        var contentType = request.DeclaredContentType ?? string.Empty;
        return SupportedExtensions.Contains(ExtractionUtilities.Extension(request)) ||
               contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ExtractionUtilities.ValidateInputFile(request, _limits);
        var text = await ExtractionUtilities.ReadBoundedTextAsync(
            request.FilePath,
            _limits.MaxTextCharacters,
            cancellationToken);

        var isMarkdown = ExtractionUtilities.Extension(request) is ".md" or ".markdown" ||
                         request.DeclaredContentType?.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase) == true;
        var sections = isMarkdown ? SplitMarkdown(text) : SingleTextSection(text);

        return new ExtractedDocument(
            isMarkdown ? "markdown" : "text",
            sections,
            new Dictionary<string, object?>
            {
                ["character_count"] = text.Length,
                ["line_count"] = CountLines(text),
                ["encoding"] = "UTF-8 (BOM-aware)"
            },
            Array.Empty<string>());
    }

    private static IReadOnlyList<ExtractedSection> SingleTextSection(string text) =>
        new[]
        {
            new ExtractedSection(
                "text",
                1,
                null,
                text,
                new Dictionary<string, object?>())
        };

    private static IReadOnlyList<ExtractedSection> SplitMarkdown(string text)
    {
        var sections = new List<ExtractedSection>();
        using var reader = new StringReader(text);
        var content = new List<string>();
        string? heading = null;
        var headingLevel = 0;

        void Flush()
        {
            var sectionContent = string.Join(Environment.NewLine, content).Trim();
            if (heading is null && sectionContent.Length == 0)
            {
                content.Clear();
                return;
            }

            sections.Add(new ExtractedSection(
                "markdown_section",
                sections.Count + 1,
                heading,
                sectionContent,
                new Dictionary<string, object?> { ["heading_level"] = headingLevel }));
            content.Clear();
        }

        while (reader.ReadLine() is { } line)
        {
            var level = MarkdownHeadingLevel(line);
            if (level > 0)
            {
                Flush();
                heading = line[(level + 1)..].Trim();
                headingLevel = level;
            }
            else
            {
                content.Add(line);
            }
        }

        Flush();
        return sections.Count > 0 ? sections : SingleTextSection(text);
    }

    private static int MarkdownHeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && level < 6 && line[level] == '#')
        {
            level++;
        }

        return level > 0 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        return text.Count(character => character == '\n') + 1;
    }
}

