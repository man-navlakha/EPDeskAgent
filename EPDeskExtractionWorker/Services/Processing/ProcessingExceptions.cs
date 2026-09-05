namespace EPDeskExtractionWorker.Services.Processing;

public abstract class ExtractionProcessingException : Exception
{
    protected ExtractionProcessingException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class RetryableExtractionException : ExtractionProcessingException
{
    public RetryableExtractionException(string errorCode, string message, Exception? inner = null)
        : base(errorCode, message, inner)
    {
    }
}

public sealed class RejectedExtractionException : ExtractionProcessingException
{
    public RejectedExtractionException(string errorCode, string message, Exception? inner = null)
        : base(errorCode, message, inner)
    {
    }
}
