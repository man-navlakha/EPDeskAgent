namespace EPDeskExtractionSandbox.Services;

public sealed class SandboxRequestException : Exception
{
    public SandboxRequestException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}
