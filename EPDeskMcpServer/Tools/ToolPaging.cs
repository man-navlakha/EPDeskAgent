using EPDeskMcpServer.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskMcpServer.Tools;

/// <summary>
/// Clamps paging arguments to the configured ceilings. Tools take this from DI
/// so a model that asks for ten thousand rows gets a capped page instead of an
/// error, and every tool caps the same way.
/// </summary>
public sealed class ToolPaging
{
    private readonly EpDeskMcpOptions _options;

    public ToolPaging(IOptions<EpDeskMcpOptions> options)
    {
        _options = options.Value;
    }

    public int MaxTextCharacters => _options.MaxTextCharacters;

    public int NormalizeOffset(int offset)
    {
        return Math.Max(0, offset);
    }

    public int NormalizeLimit(int? limit)
    {
        return Math.Clamp(
            limit ?? _options.DefaultPageSize,
            1,
            _options.MaxPageSize
        );
    }

    public int NormalizeCharacters(int? maxCharacters)
    {
        return Math.Clamp(
            maxCharacters ?? _options.MaxTextCharacters,
            500,
            _options.MaxTextCharacters
        );
    }
}
