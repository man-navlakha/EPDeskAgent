using ModelContextProtocol;

namespace EPDeskMcpServer.Services;

/// <summary>
/// Signals a failure whose message is written for the calling model rather than
/// for a log file. The message should always say what to try next, because the
/// model's recovery attempt is only as good as the sentence it is given.
/// Deriving from <see cref="McpException"/> is what lets the message reach the
/// client instead of being replaced by a generic tool failure.
/// </summary>
public sealed class McpToolException : McpException
{
    public McpToolException(string message)
        : base(message)
    {
    }
}
