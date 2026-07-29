using System.Text.Json;

namespace EPDeskAgent.Services;

/// <summary>
/// Holds the persistent, remotely controlled state of long-running Agent work.
/// Stopping an activity cancels only that activity; heartbeat and command
/// polling continue to run.
/// </summary>
public sealed class AgentActivityControl
{
    private readonly object _sync = new();
    private readonly ILogger<AgentActivityControl> _logger;
    private readonly string _statePath;
    private readonly SemaphoreSlim _scanSignal = new(0, 1);
    private readonly SemaphoreSlim _uploadSignal = new(0, 1);

    private bool _scanEnabled = true;
    private bool _fileUploadEnabled = true;
    private CancellationTokenSource? _activeScan;
    private CancellationTokenSource? _activeFileUpload;

    public AgentActivityControl(
        IConfiguration configuration,
        ILogger<AgentActivityControl> logger)
    {
        _logger = logger;
        _statePath = configuration["Agent:ActivityControlStatePath"] ?? "";

        if (string.IsNullOrWhiteSpace(_statePath))
        {
            _statePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EPDeskAgent",
                "activity-control.json"
            );
        }

        LoadState();
    }

    public bool ScanEnabled
    {
        get
        {
            lock (_sync)
            {
                return _scanEnabled;
            }
        }
    }

    public bool FileUploadEnabled
    {
        get
        {
            lock (_sync)
            {
                return _fileUploadEnabled;
            }
        }
    }

    public void StartScan()
    {
        lock (_sync)
        {
            _scanEnabled = true;
            SaveStateLocked();
        }

        Signal(_scanSignal);
    }

    public void StopScan()
    {
        lock (_sync)
        {
            _scanEnabled = false;
            _activeScan?.Cancel();
            SaveStateLocked();
        }

        Signal(_scanSignal);
    }

    public void StartFileUpload()
    {
        lock (_sync)
        {
            _fileUploadEnabled = true;
            SaveStateLocked();
        }

        Signal(_uploadSignal);
    }

    public void StopFileUpload()
    {
        lock (_sync)
        {
            _fileUploadEnabled = false;
            _activeFileUpload?.Cancel();
            SaveStateLocked();
        }

        Signal(_uploadSignal);
    }

    public void TriggerFileUpload()
    {
        if (FileUploadEnabled)
        {
            Signal(_uploadSignal);
        }
    }

    public Task WaitForScanTurnAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        return WaitForTurnAsync(
            () => ScanEnabled,
            _scanSignal,
            delay,
            cancellationToken
        );
    }

    public Task WaitForFileUploadTurnAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        return WaitForTurnAsync(
            () => FileUploadEnabled,
            _uploadSignal,
            delay,
            cancellationToken
        );
    }

    public CancellationTokenSource? BeginScan(CancellationToken stoppingToken)
    {
        lock (_sync)
        {
            if (!_scanEnabled)
            {
                return null;
            }

            _activeScan = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken
            );

            return _activeScan;
        }
    }

    public CancellationTokenSource? BeginFileUpload(CancellationToken stoppingToken)
    {
        lock (_sync)
        {
            if (!_fileUploadEnabled)
            {
                return null;
            }

            _activeFileUpload = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken
            );

            return _activeFileUpload;
        }
    }

    public void EndScan(CancellationTokenSource operation)
    {
        EndOperation(operation, isScan: true);
    }

    public void EndFileUpload(CancellationTokenSource operation)
    {
        EndOperation(operation, isScan: false);
    }

    private void EndOperation(
        CancellationTokenSource operation,
        bool isScan)
    {
        lock (_sync)
        {
            if (isScan && ReferenceEquals(_activeScan, operation))
            {
                _activeScan = null;
            }
            else if (!isScan &&
                     ReferenceEquals(_activeFileUpload, operation))
            {
                _activeFileUpload = null;
            }
        }

        operation.Dispose();
    }

    private static async Task WaitForTurnAsync(
        Func<bool> isEnabled,
        SemaphoreSlim signal,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!isEnabled())
            {
                await signal.WaitAsync(cancellationToken);
                delay = TimeSpan.Zero;
                continue;
            }

            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            using var turnCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var delayTask = Task.Delay(delay, turnCancellation.Token);
            var signalTask = signal.WaitAsync(turnCancellation.Token);
            var completedTask = await Task.WhenAny(delayTask, signalTask);

            if (completedTask == signalTask)
            {
                await signalTask;
                turnCancellation.Cancel();
                delay = TimeSpan.Zero;
                continue;
            }

            await delayTask;
            turnCancellation.Cancel();
            return;
        }
    }

    private static void Signal(SemaphoreSlim signal)
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<ActivityControlState>(
                File.ReadAllText(_statePath)
            );

            if (state != null)
            {
                _scanEnabled = state.ScanEnabled;
                _fileUploadEnabled = state.FileUploadEnabled;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not load Agent activity control state from {StatePath}.",
                _statePath
            );
        }
    }

    private void SaveStateLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new ActivityControlState
            {
                ScanEnabled = _scanEnabled,
                FileUploadEnabled = _fileUploadEnabled
            };

            File.WriteAllText(
                _statePath,
                JsonSerializer.Serialize(
                    state,
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not persist Agent activity control state to {StatePath}.",
                _statePath
            );
        }
    }

    private sealed class ActivityControlState
    {
        public bool ScanEnabled { get; set; } = true;

        public bool FileUploadEnabled { get; set; } = true;
    }
}
