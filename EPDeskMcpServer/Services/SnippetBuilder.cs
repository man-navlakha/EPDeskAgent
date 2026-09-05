using System.Globalization;
using System.Text;

namespace EPDeskMcpServer.Services;

/// <summary>
/// Builds the short quoted excerpt shown with each search hit. PostgreSQL can
/// do this with ts_headline, but doing it here keeps the ranking query cheap
/// and lets the excerpt stay readable when the match is a partial word.
/// </summary>
public static class SnippetBuilder
{
    private const int DefaultWindow = 320;

    public static string Build(
        string content,
        string query,
        int window = DefaultWindow)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        var collapsed = CollapseWhitespace(content);

        if (collapsed.Length <= window)
        {
            return collapsed;
        }

        var matchIndex = FindFirstTermIndex(collapsed, query);

        if (matchIndex < 0)
        {
            return collapsed[..window].TrimEnd() + "…";
        }

        // Centre the window on the match so the model sees the words either
        // side of it, which is usually what makes the hit judgeable.
        var start = Math.Max(0, matchIndex - (window / 2));
        var length = Math.Min(window, collapsed.Length - start);

        var snippet = collapsed.Substring(start, length).Trim();

        if (start > 0)
        {
            snippet = "…" + snippet;
        }

        if (start + length < collapsed.Length)
        {
            snippet += "…";
        }

        return snippet;
    }

    private static int FindFirstTermIndex(string content, string query)
    {
        var best = -1;

        foreach (var term in ExtractTerms(query))
        {
            var index = content.IndexOf(
                term,
                StringComparison.OrdinalIgnoreCase
            );

            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    private static IEnumerable<string> ExtractTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        var buffer = new StringBuilder();

        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer.Append(character);
                continue;
            }

            if (buffer.Length >= 3)
            {
                yield return buffer.ToString();
            }

            buffer.Clear();
        }

        if (buffer.Length >= 3)
        {
            yield return buffer.ToString();
        }
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    public static string DescribeBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            unitIndex == 0 ? "{0:0} {1}" : "{0:0.#} {1}",
            value,
            units[unitIndex]
        );
    }
}
