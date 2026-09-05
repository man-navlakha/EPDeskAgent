using System.Text.Json;
using EPDeskOldDataUploader.Models;

namespace EPDeskOldDataUploader.Services;

/// <summary>
/// Remembers how each person's folder finished, so working through an archive
/// over several days does not need a list kept on paper.
///
/// This is a local note of what this machine did, not a statement about what the
/// server holds. The run itself is the authority: a folder marked done will
/// still be re-checked file by file if it is uploaded again, and anything the
/// server already has costs nothing to skip.
/// </summary>
public sealed class FolderHistory
{
    private readonly Dictionary<string, FolderHistoryEntry> _entries = new(
        StringComparer.OrdinalIgnoreCase
    );

    public static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EPDeskOldDataUploader",
        "folder-history.json"
    );

    public static FolderHistory Load()
    {
        var history = new FolderHistory();

        try
        {
            if (File.Exists(HistoryPath))
            {
                var entries = JsonSerializer.Deserialize<List<FolderHistoryEntry>>(
                    File.ReadAllText(HistoryPath)
                ) ?? [];

                foreach (var entry in entries)
                {
                    history._entries[CreateKey(entry.RootPath, entry.UserFolder)] = entry;
                }
            }
        }
        catch (Exception)
        {
            // A damaged history file costs nothing to discard; it is only a note.
        }

        return history;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(
                HistoryPath,
                JsonSerializer.Serialize(
                    _entries.Values.OrderBy(x => x.RootPath).ThenBy(x => x.UserFolder),
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
        }
        catch (Exception)
        {
            // Failing to write the note must never fail the upload it followed.
        }
    }

    public void Record(string rootPath, ScannedUser user)
    {
        var entry = new FolderHistoryEntry
        {
            RootPath = rootPath,
            UserFolder = user.UserFolder,
            LastRunUtc = DateTime.UtcNow,
            FilesSeen = user.Files.Count,
            Uploaded = user.UploadedCount,
            AlreadyThere = user.AlreadyPresentCount,
            Skipped = user.SkippedCount,
            Failed = user.FailedCount,
            Pending = user.Files.Count(x => x.State == FileState.Pending)
        };

        _entries[CreateKey(rootPath, user.UserFolder)] = entry;
    }

    /// <summary>
    /// Describes an earlier run in the terms that matter when deciding whether to
    /// tick the folder again. Comparing today's file count against the count at
    /// the time catches files added to the archive since.
    /// </summary>
    public string Describe(string rootPath, ScannedUser user)
    {
        if (!_entries.TryGetValue(CreateKey(rootPath, user.UserFolder), out var entry))
        {
            return "";
        }

        var when = entry.LastRunUtc.ToLocalTime().ToString("dd MMM");

        if (entry.Failed > 0)
        {
            return $"{entry.Failed:N0} failed - {when}";
        }

        if (entry.Pending > 0)
        {
            return $"Stopped part way - {when}";
        }

        var added = user.Files.Count - entry.FilesSeen;

        if (added > 0)
        {
            return $"{added:N0} new since {when}";
        }

        if (entry.Skipped > 0)
        {
            return $"Done, {entry.Skipped:N0} skipped - {when}";
        }

        return $"Done - {when}";
    }

    /// <summary>
    /// True only when nothing is outstanding, which is what lets the grid grey a
    /// folder out rather than merely annotating it.
    /// </summary>
    public bool IsFullyDone(string rootPath, ScannedUser user)
    {
        if (!_entries.TryGetValue(CreateKey(rootPath, user.UserFolder), out var entry))
        {
            return false;
        }

        return entry.Failed == 0 &&
               entry.Pending == 0 &&
               user.Files.Count <= entry.FilesSeen;
    }

    private static string CreateKey(string rootPath, string userFolder) =>
        rootPath.TrimEnd('\\', '/') + "|" + userFolder;
}

public sealed class FolderHistoryEntry
{
    public string RootPath { get; set; } = "";
    public string UserFolder { get; set; } = "";
    public DateTime LastRunUtc { get; set; }
    public int FilesSeen { get; set; }
    public int Uploaded { get; set; }
    public int AlreadyThere { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
}
