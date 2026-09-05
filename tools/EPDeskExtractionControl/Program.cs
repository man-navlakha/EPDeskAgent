using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

const int DefaultBatchSize = 250;
const int MaximumBatchSize = 250;

if (args.Length == 0)
{
    WriteUsage();
    return 2;
}

var configuredConnectionString =
    Environment.GetEnvironmentVariable("EPDESK_CONTROL_DATABASE") ?? "";
var bucketName =
    Environment.GetEnvironmentVariable("EPDESK_CONTROL_B2_BUCKET") ?? "";
if (string.IsNullOrWhiteSpace(configuredConnectionString) ||
    string.IsNullOrWhiteSpace(bucketName))
{
    Console.Error.WriteLine(
        "The control database and B2 bucket settings are required."
    );
    return 2;
}

var connectionString = NormalizeDatabaseConnectionString(
    configuredConnectionString
);

var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(connectionString, options => options.CommandTimeout(60))
    .Options;
var storageOptions = Options.Create(new B2StorageOptions
{
    BucketName = bucketName
});

if (string.Equals(args[0], "enqueue-automatic", StringComparison.Ordinal))
{
    if (args.Length != 2 ||
        !Guid.TryParse(args[1], out var sourceRecordId) ||
        sourceRecordId == Guid.Empty)
    {
        WriteUsage();
        return 2;
    }

    return await EnqueueAutomaticCanaryAsync(
        dbOptions,
        storageOptions,
        sourceRecordId
    );
}

if (string.Equals(args[0], "enqueue-bulk", StringComparison.Ordinal))
{
    if (!TryReadBatchSize(args, out var batchSize))
    {
        WriteUsage();
        return 2;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    return await BulkEnqueueCommand.RunAsync(
        dbOptions,
        storageOptions,
        batchSize,
        cancellation.Token
    );
}

WriteUsage();
return 2;

static async Task<int> EnqueueAutomaticCanaryAsync(
    DbContextOptions<AppDbContext> dbOptions,
    IOptions<B2StorageOptions> storageOptions,
    Guid sourceRecordId
)
{
    await using var db = new AppDbContext(dbOptions);
    var upload = await db.AutomaticFileUploads.SingleOrDefaultAsync(
        item => item.Id == sourceRecordId
    );
    if (upload is null ||
        !string.Equals(upload.Status, "completed", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "The selected source is not a completed automatic upload."
        );
        return 3;
    }

    var queue = new DocumentExtractionQueueService(db, storageOptions);

    await using var transaction = await db.Database.BeginTransactionAsync();
    var result = await queue.EnsureAutomaticUploadQueuedAsync(upload);
    await db.SaveChangesAsync();
    await transaction.CommitAsync();

    Console.WriteLine($"document_id={result.DocumentId}");
    Console.WriteLine($"version_id={result.DocumentVersionId}");
    Console.WriteLine($"job_id={result.ExtractionJobId}");
    Console.WriteLine(
        $"document_created={result.DocumentCreated.ToString().ToLowerInvariant()}"
    );
    Console.WriteLine(
        $"version_created={result.VersionCreated.ToString().ToLowerInvariant()}"
    );
    Console.WriteLine(
        $"job_created={result.JobCreated.ToString().ToLowerInvariant()}"
    );
    return 0;
}

static bool TryReadBatchSize(string[] commandArguments, out int batchSize)
{
    batchSize = DefaultBatchSize;
    if (commandArguments.Length == 1)
    {
        return true;
    }

    return commandArguments.Length == 3 &&
           string.Equals(
               commandArguments[1],
               "--batch-size",
               StringComparison.Ordinal
           ) &&
           int.TryParse(commandArguments[2], out batchSize) &&
           batchSize is >= 1 and <= MaximumBatchSize;
}

static void WriteUsage()
{
    Console.Error.WriteLine(
        "Usage: EPDeskExtractionControl enqueue-automatic <source-record-id>"
    );
    Console.Error.WriteLine(
        "       EPDeskExtractionControl enqueue-bulk [--batch-size 1..250]"
    );
}

static string NormalizeDatabaseConnectionString(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
    {
        return value;
    }

    var separator = uri.UserInfo.IndexOf(':');
    var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
    if (separator <= 0 || string.IsNullOrWhiteSpace(uri.Host) ||
        string.IsNullOrWhiteSpace(database))
    {
        throw new ArgumentException("The PostgreSQL URL is incomplete.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = database,
        Username = Uri.UnescapeDataString(uri.UserInfo[..separator]),
        Password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]),
        SslMode = SslMode.Require,
        Timeout = 15,
        CommandTimeout = 60
    };
    return builder.ConnectionString;
}
