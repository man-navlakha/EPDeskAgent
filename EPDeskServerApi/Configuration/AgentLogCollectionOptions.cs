namespace EPDeskServerApi.Configuration;

/// <summary>
/// Controls the background worker that pulls agent logs on a schedule.
///
/// Agents only ship their logs when they receive a REQUEST_LOGS remote command,
/// so without this nothing reaches AgentLogs unless an admin asks for it by hand.
/// That blind spot is how a stalled upload queue went unnoticed for 17 days.
/// </summary>
public sealed class AgentLogCollectionOptions
{
    public const string SectionName = "AgentLogCollection";

    /// <summary>
    /// Off by default on purpose. A developer machine pointed at the production
    /// database would otherwise start issuing remote commands to real devices.
    /// Turn it on only where the deployed API runs.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How often the worker looks for devices that are due a log pull.</summary>
    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>How much time must pass before the same device is asked again.</summary>
    public int CollectEveryHours { get; set; } = 6;

    /// <summary>
    /// Only ask devices that have heartbeated inside this window. Queueing commands
    /// for machines that are switched off just builds a backlog they receive all at
    /// once when they next come online.
    /// </summary>
    public int DeviceActiveWindowMinutes { get; set; } = 30;

    /// <summary>Caps how many devices are asked in a single pass.</summary>
    public int MaxDevicesPerPass { get; set; } = 10;

    /// <summary>Log lines requested per device.</summary>
    public int TakeLines { get; set; } = 500;

    /// <summary>Value recorded in RemoteCommand.RequestedBy for these commands.</summary>
    public string RequestedBy { get; set; } = "auto-log-collection";
}
