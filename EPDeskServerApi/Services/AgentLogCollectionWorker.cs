using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace EPDeskServerApi.Services;

/// <summary>
/// Queues REQUEST_LOGS remote commands for devices that are online but whose logs
/// have gone stale, so agent-side failures show up in AgentLogs without anyone
/// having to ask for them.
/// </summary>
public sealed class AgentLogCollectionWorker : BackgroundService
{
    private const string CommandType = "REQUEST_LOGS";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentLogCollectionOptions _options;
    private readonly ILogger<AgentLogCollectionWorker> _logger;

    public AgentLogCollectionWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<AgentLogCollectionOptions> options,
        ILogger<AgentLogCollectionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Automatic agent log collection is disabled. " +
                "Set AgentLogCollection:Enabled to true to turn it on."
            );
            return;
        }

        var pollInterval = TimeSpan.FromMinutes(Math.Max(1, _options.PollIntervalMinutes));

        _logger.LogInformation(
            "Automatic agent log collection is on. Every {PollMinutes} minutes, " +
            "devices seen in the last {ActiveMinutes} minutes are asked for logs " +
            "at most once every {CollectHours} hours.",
            pollInterval.TotalMinutes,
            _options.DeviceActiveWindowMinutes,
            _options.CollectEveryHours
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A database blip must not kill the worker for the rest of the
                // process lifetime.
                _logger.LogError(exception, "Agent log collection pass failed.");
            }

            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var activeSince = now.AddMinutes(-Math.Max(1, _options.DeviceActiveWindowMinutes));
        var staleBefore = now.AddHours(-Math.Max(1, _options.CollectEveryHours));
        var maxDevices = Math.Clamp(_options.MaxDevicesPerPass, 1, 200);

        var activeDeviceCodes = await db.Devices
            .AsNoTracking()
            .Where(x => x.LastSeenAtUtc != null && x.LastSeenAtUtc > activeSince)
            .Select(x => x.DeviceCode)
            .ToListAsync(cancellationToken);

        if (activeDeviceCodes.Count == 0)
        {
            return;
        }

        // A device is skipped when it already has a log request waiting to be picked
        // up, or when one was issued recently. Both checks matter: the deployed API
        // and a developer machine can be pointed at the same database.
        var busyDeviceCodes = await db.RemoteCommands
            .AsNoTracking()
            .Where(x => x.CommandType == CommandType &&
                        activeDeviceCodes.Contains(x.DeviceCode) &&
                        (x.Status == "pending" ||
                         x.Status == "sent_to_agent" ||
                         x.RequestedAtUtc > staleBefore))
            .Select(x => x.DeviceCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        var dueDeviceCodes = activeDeviceCodes
            .Except(busyDeviceCodes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(maxDevices)
            .ToList();

        if (dueDeviceCodes.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            logType = "all",
            takeLines = Math.Clamp(_options.TakeLines, 1, 5000)
        });

        foreach (var deviceCode in dueDeviceCodes)
        {
            db.RemoteCommands.Add(new RemoteCommand
            {
                Id = Guid.NewGuid(),
                DeviceCode = deviceCode.Trim().ToUpperInvariant(),
                CommandType = CommandType,
                PayloadJson = payload,
                Status = "pending",
                RequestedBy = _options.RequestedBy,
                RequestedAtUtc = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Queued {CommandCount} log request(s) for: {DeviceCodes}",
            dueDeviceCodes.Count,
            string.Join(", ", dueDeviceCodes)
        );
    }
}
