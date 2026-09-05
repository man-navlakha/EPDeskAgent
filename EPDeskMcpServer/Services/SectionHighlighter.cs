using System.Data.Common;
using EPDeskServerApi.Data;
using Microsoft.EntityFrameworkCore;

namespace EPDeskMcpServer.Services;

/// <summary>
/// Builds search snippets with PostgreSQL's ts_headline, which knows the same
/// lexemes the index matched on. Trimming text in C# cannot do that: a hit can
/// be on a stemmed form, on OCR text, or thousands of characters into a page,
/// and a leading excerpt would show none of them.
/// </summary>
public static class SectionHighlighter
{
    /// <summary>
    /// Text-search configuration of the generated tsvector column. Highlighting
    /// with a different configuration would mark the wrong words.
    /// </summary>
    private const string SearchConfiguration = "simple";

    /// <summary>
    /// ts_headline scans the whole document it is given, so very long sections
    /// are clipped first to keep the call cheap.
    /// </summary>
    private const int MaxCharactersScanned = 200_000;

    private const string HeadlineOptions =
        "StartSel=**, StopSel=**, MaxWords=45, MinWords=20, ShortWord=3, " +
        "MaxFragments=2";

    public static async Task<IReadOnlyDictionary<Guid, string>> BuildAsync(
        AppDbContext db,
        IReadOnlyList<Guid> sectionIds,
        string query,
        CancellationToken cancellationToken)
    {
        var snippets = new Dictionary<Guid, string>();

        if (sectionIds.Count == 0)
        {
            return snippets;
        }

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();

            var idPlaceholders = new List<string>(sectionIds.Count);

            for (var index = 0; index < sectionIds.Count; index++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@id{index}";
                parameter.Value = sectionIds[index];
                command.Parameters.Add(parameter);
                idPlaceholders.Add(parameter.ParameterName);
            }

            AddParameter(command, "@query", query);
            AddParameter(command, "@options", HeadlineOptions);
            AddParameter(command, "@maxChars", MaxCharactersScanned);

            command.CommandText = $"""
                SELECT s."Id",
                       ts_headline(
                           '{SearchConfiguration}',
                           left(
                               coalesce(s."Heading", '') || ' ' ||
                               coalesce(s."Content", '') || ' ' ||
                               coalesce(s."OcrContent", ''),
                               @maxChars
                           ),
                           websearch_to_tsquery('{SearchConfiguration}', @query),
                           @options
                       )
                FROM "DocumentSections" s
                WHERE s."Id" IN ({string.Join(", ", idPlaceholders)})
                """;

            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken
            );

            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var sectionId = reader.GetGuid(0);
                var snippet = Normalize(reader.GetString(1));

                if (snippet.Length > 0)
                {
                    snippets[sectionId] = snippet;
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return snippets;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Extracted text keeps the original line breaks and column padding, which
    /// read as noise once a fragment is pulled out of context.
    /// </summary>
    private static string Normalize(string headline)
    {
        return string.Join(
            ' ',
            headline.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries
            )
        );
    }
}
