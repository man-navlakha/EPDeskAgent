using EPDeskOldDataUploader.Models;
using EPDeskOldDataUploader.Services;

namespace EPDeskOldDataUploader;

public sealed class MainForm : Form
{
    private const int MaxLogLines = 400;
    private const int MaxProblemRows = 3000;

    // Column positions in the users grid, named so adding a column cannot
    // silently write counts into the wrong cell.
    private const int ColumnSelected = 0;
    private const int ColumnDeviceCode = 2;
    private const int ColumnUploaded = 5;
    private const int ColumnAlreadyThere = 6;
    private const int ColumnSkipped = 7;
    private const int ColumnFailed = 8;
    private const int ColumnProgress = 9;
    private const int ColumnHistory = 10;

    private readonly UploaderSettings _settings = UploaderSettings.Load();

    private readonly TextBox _rootPathBox = new();
    private readonly TextBox _apiUrlBox = new();
    private readonly TextBox _apiKeyBox = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _showKeyBox = new() { Text = "Show" };
    private readonly TextBox _prefixBox = new();
    private readonly NumericUpDown _parallelBox = new() { Minimum = 1, Maximum = 32 };
    private readonly NumericUpDown _retryBox = new() { Minimum = 1, Maximum = 10 };
    private readonly CheckBox _shaBox = new() { Text = "Checksum every file" };
    private readonly CheckBox _readableFoldersBox = new()
    {
        Text = "Readable folders in storage"
    };
    private readonly CheckBox _singlePersonBox = new()
    {
        Text = "This folder is one person"
    };

    private readonly Button _browseButton = new() { Text = "Browse..." };
    private readonly Button _scanButton = new() { Text = "Scan folder" };
    private readonly Button _startButton = new() { Text = "Upload selected", Enabled = false };
    private readonly Button _pauseButton = new() { Text = "Pause", Enabled = false };
    private readonly Button _stopButton = new() { Text = "Stop", Enabled = false };
    private readonly Button _selectAllButton = new() { Text = "Select all" };
    private readonly Button _selectNoneButton = new() { Text = "Select none" };

    private readonly DataGridView _usersGrid = new();
    private readonly DataGridView _problemsGrid = new();
    private readonly TextBox _logBox = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly Label _policyLabel = new();

    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 500 };

    private readonly FolderHistory _history = FolderHistory.Load();

    private ScanResult? _scan;
    private string _scannedRootPath = "";
    private UploadPolicy? _policy;
    private UploadRunner? _runner;
    private CancellationTokenSource? _cancellation;
    private Dictionary<string, int> _userRowIndexes = new(StringComparer.OrdinalIgnoreCase);

    private int _totalWorkFiles;
    private long _totalWorkBytes;
    private long _lastTransferredBytes;
    private long _lastCompletedFiles;
    private DateTime _lastSampleAtUtc = DateTime.UtcNow;
    private double _filesPerSecond;
    private double _bytesPerSecond;

    public MainForm()
    {
        Text = "EPDesk - Old User Data Uploader";
        MinimumSize = new Size(1000, 640);
        Size = new Size(1320, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(BuildHeader());

        WireEvents();
        LoadSettingsIntoFields();
    }

    // ---------------------------------------------------------------- layout

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 8,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 12, 12, 6)
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _rootPathBox.Dock = DockStyle.Fill;
        _apiUrlBox.Dock = DockStyle.Fill;
        _apiKeyBox.Dock = DockStyle.Fill;
        _prefixBox.Width = 90;
        _parallelBox.Width = 60;
        _retryBox.Width = 60;
        _browseButton.AutoSize = true;
        _browseButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _browseButton.Padding = new Padding(8, 2, 8, 2);

        panel.Controls.Add(NewLabel("Users Data folder"), 0, 0);
        panel.Controls.Add(_rootPathBox, 1, 0);
        panel.SetColumnSpan(_rootPathBox, 6);
        panel.Controls.Add(_browseButton, 7, 0);

        panel.Controls.Add(NewLabel("API address"), 0, 1);
        panel.Controls.Add(_apiUrlBox, 1, 1);
        panel.SetColumnSpan(_apiUrlBox, 3);
        panel.Controls.Add(NewLabel("API key"), 4, 1);
        panel.Controls.Add(_apiKeyBox, 5, 1);
        panel.SetColumnSpan(_apiKeyBox, 2);
        panel.Controls.Add(_showKeyBox, 7, 1);

        var optionsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0)
        };

        optionsPanel.Controls.Add(NewLabel("Device code prefix"));
        optionsPanel.Controls.Add(_prefixBox);
        optionsPanel.Controls.Add(NewLabel("Files at once"));
        optionsPanel.Controls.Add(_parallelBox);
        optionsPanel.Controls.Add(NewLabel("Retries"));
        optionsPanel.Controls.Add(_retryBox);
        _shaBox.Margin = new Padding(16, 6, 0, 0);
        _shaBox.AutoSize = true;
        optionsPanel.Controls.Add(_shaBox);

        _singlePersonBox.Margin = new Padding(16, 6, 0, 0);
        _singlePersonBox.AutoSize = true;
        optionsPanel.Controls.Add(_singlePersonBox);

        _readableFoldersBox.Margin = new Padding(16, 6, 0, 0);
        _readableFoldersBox.AutoSize = true;
        optionsPanel.Controls.Add(_readableFoldersBox);

        panel.Controls.Add(optionsPanel, 0, 2);
        panel.SetColumnSpan(optionsPanel, 8);

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };

        foreach (var button in new[] { _scanButton, _startButton, _pauseButton, _stopButton })
        {
            // Sized to the caption rather than a fixed width, so nothing is
            // clipped on a display running at 125% or 150%.
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(104, 30);
            button.Padding = new Padding(10, 4, 10, 4);
            buttonPanel.Controls.Add(button);
        }

        foreach (var button in new[] { _selectAllButton, _selectNoneButton })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(88, 30);
            button.Padding = new Padding(8, 4, 8, 4);
            button.Margin = new Padding(12, 3, 0, 3);
            buttonPanel.Controls.Add(button);
        }

        _policyLabel.AutoSize = true;
        _policyLabel.Margin = new Padding(16, 12, 0, 0);
        _policyLabel.ForeColor = SystemColors.GrayText;
        buttonPanel.Controls.Add(_policyLabel);

        panel.Controls.Add(buttonPanel, 0, 3);
        panel.SetColumnSpan(buttonPanel, 8);

        return panel;
    }

    private Control BuildBody()
    {
        ConfigureGrid(_usersGrid);

        // Only the tick box is editable; everything else is a read-only report.
        _usersGrid.ReadOnly = false;
        _usersGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Upload",
            FillWeight = 8,
            MinimumWidth = 62,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        var reportColumns = new[]
        {
            TextColumn("User folder", 22),
            TextColumn("Device code", 17),
            TextColumn("Files", 9),
            TextColumn("Size", 11),
            TextColumn("Uploaded", 11),
            TextColumn("Already there", 13),
            TextColumn("Skipped", 10),
            TextColumn("Failed", 9),
            TextColumn("Progress", 11),
            TextColumn("Last run", 24)
        };

        foreach (var column in reportColumns)
        {
            column.ReadOnly = true;
        }

        _usersGrid.Columns.AddRange(reportColumns);

        ConfigureGrid(_problemsGrid);
        _problemsGrid.Columns.AddRange(
            TextColumn("User", 14),
            TextColumn("State", 8),
            TextColumn("File", 22),
            TextColumn("Reason", 26),
            TextColumn("Full path", 40)
        );

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.BackColor = Color.White;
        _logBox.Font = new Font("Consolas", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };

        var usersTab = new TabPage("Users") { Padding = new Padding(6) };
        usersTab.Controls.Add(_usersGrid);

        var problemsTab = new TabPage("Skipped and failed") { Padding = new Padding(6) };
        problemsTab.Controls.Add(_problemsGrid);

        var logTab = new TabPage("Log") { Padding = new Padding(6) };
        logTab.Controls.Add(_logBox);

        tabs.TabPages.AddRange([usersTab, problemsTab, logTab]);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        host.Controls.Add(tabs);

        return host;
    }

    private Control BuildFooter()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 66,
            Padding = new Padding(12, 8, 12, 12)
        };

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 22;
        _progressBar.Maximum = 1000;

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "Pick the Users Data folder and press Scan folder.";

        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(_progressBar);

        return panel;
    }

    private static Label NewLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(3, 7, 8, 0)
    };

    /// <summary>
    /// Columns share the grid width by weight rather than by pixels, so a header
    /// never ends up half drawn on a scaled display.
    /// </summary>
    private static DataGridViewTextBoxColumn TextColumn(string header, int fillWeight) => new()
    {
        HeaderText = header,
        FillWeight = fillWeight,
        MinimumWidth = 54,
        SortMode = DataGridViewColumnSortMode.NotSortable
    };

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 249, 249);
    }

    // ---------------------------------------------------------------- wiring

    private void WireEvents()
    {
        _browseButton.Click += (_, _) => BrowseForRoot();
        _showKeyBox.CheckedChanged += (_, _) =>
            _apiKeyBox.UseSystemPasswordChar = !_showKeyBox.Checked;
        _scanButton.Click += async (_, _) => await ScanAsync();
        _startButton.Click += async (_, _) => await StartAsync();
        _pauseButton.Click += (_, _) => TogglePause();
        _stopButton.Click += (_, _) => Stop();
        _refreshTimer.Tick += (_, _) => RefreshProgress();
        FormClosing += OnFormClosing;

        _selectAllButton.Click += (_, _) => SetAllSelected(true);
        _selectNoneButton.Click += (_, _) => SetAllSelected(false);

        // A checkbox cell does not commit until focus leaves it, which would make
        // the tick look ignored until you clicked elsewhere.
        _usersGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_usersGrid.IsCurrentCellDirty)
            {
                _usersGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        _usersGrid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == ColumnSelected)
            {
                OnUserSelectionChanged(e.RowIndex);
            }
        };

        // Editing the prefix only re-labels what was found, so a scan that took
        // minutes over a large archive is not thrown away.
        _prefixBox.TextChanged += (_, _) => ReapplyDeviceCodePrefix();

        // Changing the grouping does invalidate the scan: which folder counts as
        // a person decides how every file was filed.
        _singlePersonBox.CheckedChanged += (_, _) => InvalidateScan(
            "Grouping changed. Scan the folder again."
        );

        _rootPathBox.TextChanged += (_, _) => InvalidateScan(
            "Folder changed. Scan the folder again."
        );
    }

    /// <summary>
    /// Re-derives every device code from the prefix box without rescanning.
    /// </summary>
    private void ReapplyDeviceCodePrefix()
    {
        if (_scan == null || _runner != null)
        {
            return;
        }

        var prefix = _prefixBox.Text;

        foreach (var user in _scan.Users)
        {
            user.DeviceCode = FolderScanner.CreateDeviceCode(prefix, user.UserFolder);

            foreach (var file in user.Files)
            {
                file.DeviceCode = user.DeviceCode;
            }

            if (_userRowIndexes.TryGetValue(user.UserFolder, out var index) &&
                index < _usersGrid.Rows.Count)
            {
                _usersGrid.Rows[index].Cells[ColumnDeviceCode].Value = user.DeviceCode;
            }
        }
    }

    private void InvalidateScan(string message)
    {
        if (_scan == null || _runner != null)
        {
            return;
        }

        _scan = null;
        _usersGrid.Rows.Clear();
        _problemsGrid.Rows.Clear();
        _userRowIndexes.Clear();
        _startButton.Enabled = false;
        _progressBar.Value = 0;
        _statusLabel.Text = message;
    }

    private void LoadSettingsIntoFields()
    {
        _rootPathBox.Text = _settings.RootPath;
        _apiUrlBox.Text = _settings.ApiBaseUrl;
        _apiKeyBox.Text = _settings.ApiKey;
        _prefixBox.Text = _settings.DeviceCodePrefix;
        _parallelBox.Value = Math.Clamp(_settings.ParallelUploads, 1, 32);
        _retryBox.Value = Math.Clamp(_settings.RetryCount, 1, 10);
        _shaBox.Checked = _settings.ComputeSha256;
        _singlePersonBox.Checked = _settings.SinglePersonFolder;
        _readableFoldersBox.Checked = _settings.UseReadableFolders;
    }

    private void SaveSettingsFromFields()
    {
        _settings.RootPath = _rootPathBox.Text.Trim();
        _settings.ApiBaseUrl = _apiUrlBox.Text.Trim();
        _settings.ApiKey = _apiKeyBox.Text.Trim();
        _settings.DeviceCodePrefix = _prefixBox.Text.Trim();
        _settings.ParallelUploads = (int)_parallelBox.Value;
        _settings.RetryCount = (int)_retryBox.Value;
        _settings.ComputeSha256 = _shaBox.Checked;
        _settings.SinglePersonFolder = _singlePersonBox.Checked;
        _settings.UseReadableFolders = _readableFoldersBox.Checked;
        _settings.Save();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_runner != null && _cancellation is { IsCancellationRequested: false })
        {
            var answer = MessageBox.Show(
                this,
                "An upload is still running. Stop it and close?\n\n" +
                "Finished files stay on the server, and restarting the tool picks " +
                "up where this left off.",
                "Upload in progress",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cancellation.Cancel();
        }

        SaveSettingsFromFields();
    }

    private void BrowseForRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Users Data folder that holds one folder per person.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (Directory.Exists(_rootPathBox.Text.Trim()))
        {
            dialog.SelectedPath = _rootPathBox.Text.Trim();
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _rootPathBox.Text = dialog.SelectedPath;
        }
    }

    // ----------------------------------------------------------------- scan

    private async Task ScanAsync()
    {
        var rootPath = _rootPathBox.Text.Trim();

        if (!Directory.Exists(rootPath))
        {
            ShowWarning("Pick a folder that exists before scanning.");
            return;
        }

        if (_apiKeyBox.Text.Trim().Length == 0)
        {
            ShowWarning("The agent file-upload API key is required.");
            return;
        }

        SetBusy(true);
        AppendLog($"Reading the upload policy from {_apiUrlBox.Text.Trim()}...");

        try
        {
            using (var client = CreateClient())
            {
                _policy = await client.GetPolicyAsync(CancellationToken.None);
            }

            if (!_policy.IsEnabled || _policy.Extensions.Count == 0)
            {
                ShowWarning(
                    "The server has automatic file upload switched off, or no " +
                    "extensions are enabled. Nothing can be uploaded until that " +
                    "policy is turned back on."
                );
                SetBusy(false);
                return;
            }

            _policyLabel.Text =
                $"Policy: {_policy.Extensions.Count} extensions, up to " +
                $"{FolderScanner.FormatSize(_policy.MaxFileSizeBytes)} per file";

            AppendLog(
                $"Policy allows {string.Join(" ", _policy.Extensions.OrderBy(x => x))}."
            );
            AppendLog($"Scanning {rootPath}...");

            var prefix = _prefixBox.Text.Trim();
            var singlePerson = _singlePersonBox.Checked;
            var extensions = _policy.Extensions.ToList();
            var maxSize = _policy.MaxFileSizeBytes;
            var progress = new Progress<string>(message => _statusLabel.Text = message);

            var scan = await Task.Run(() =>
                new FolderScanner(rootPath, prefix, singlePerson, extensions, maxSize)
                    .Scan(progress, CancellationToken.None));

            _scan = scan;
            _scannedRootPath = rootPath;

            foreach (var user in scan.Users)
            {
                user.HistoryLabel = _history.Describe(rootPath, user);
            }

            PopulateUsersGrid(scan);
            PopulateScanProblems(scan);

            var totalFiles = scan.Users.Sum(x => x.Files.Count);
            var totalBytes = scan.Users.Sum(x => x.TotalBytes);

            AppendLog(
                $"Found {totalFiles:N0} matching files " +
                $"({FolderScanner.FormatSize(totalBytes)}) across " +
                $"{scan.Users.Count} user folders. " +
                $"{scan.IgnoredByExtensionCount:N0} files ignored by extension." +
                (scan.UnreadableFolderCount > 0
                    ? $" {scan.UnreadableFolderCount:N0} folders could not be read."
                    : "")
            );

            AppendLog(
                $"Found {totalFiles:N0} files across {scan.Users.Count} folders. " +
                "Tick the people to upload."
            );

            // UpdateSelectionSummary owns the status line and the button from
            // here on, because both depend on what is ticked.
            UpdateSelectionSummary();
        }
        catch (Exception exception)
        {
            AppendLog($"Scan failed: {exception.Message}");
            ShowError("Scan failed.\n\n" + exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateUsersGrid(ScanResult scan)
    {
        _usersGrid.Rows.Clear();
        _userRowIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in scan.Users)
        {
            var index = _usersGrid.Rows.Add(
                user.IsSelected,
                user.UserFolder,
                user.DeviceCode,
                user.Files.Count.ToString("N0"),
                FolderScanner.FormatSize(user.TotalBytes),
                "0",
                "0",
                user.SkippedCount.ToString("N0"),
                "0",
                "0%",
                user.HistoryLabel
            );

            _userRowIndexes[user.UserFolder] = index;

            // A folder with nothing outstanding is greyed so the ones still
            // needing attention stand out in a list of forty.
            if (user.HistoryLabel.Length > 0 &&
                _history.IsFullyDone(_scannedRootPath, user))
            {
                _usersGrid.Rows[index].DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            else if (user.HistoryLabel.Contains("failed"))
            {
                _usersGrid.Rows[index].Cells[ColumnHistory].Style.ForeColor = Color.Firebrick;
            }
        }

        UpdateSelectionSummary();
    }

    /// <summary>
    /// Applies a tick box edit back to the model and restates what a run would
    /// now cover, so the size of the job is visible before pressing Start.
    /// </summary>
    private void OnUserSelectionChanged(int rowIndex)
    {
        if (_scan == null || _runner != null || rowIndex < 0)
        {
            return;
        }

        var folder = _usersGrid.Rows[rowIndex].Cells[ColumnSelected + 1].Value?.ToString() ?? "";
        var user = _scan.Users.FirstOrDefault(x =>
            string.Equals(x.UserFolder, folder, StringComparison.OrdinalIgnoreCase));

        if (user == null)
        {
            return;
        }

        user.IsSelected =
            _usersGrid.Rows[rowIndex].Cells[ColumnSelected].Value as bool? ?? false;

        UpdateSelectionSummary();
    }

    private void SetAllSelected(bool selected)
    {
        if (_scan == null || _runner != null)
        {
            return;
        }

        foreach (var user in _scan.Users)
        {
            user.IsSelected = selected;
        }

        foreach (DataGridViewRow row in _usersGrid.Rows)
        {
            row.Cells[ColumnSelected].Value = selected;
        }

        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        if (_scan == null || _runner != null)
        {
            return;
        }

        var selected = _scan.Users.Where(x => x.IsSelected).ToList();
        var files = selected.Sum(x => x.Files.Count(y => y.State == FileState.Pending));
        var bytes = selected
            .SelectMany(x => x.Files)
            .Where(x => x.State == FileState.Pending)
            .Sum(x => x.SizeBytes);

        _startButton.Enabled = files > 0;

        _statusLabel.Text = selected.Count == 0
            ? "Nothing selected. Tick the folders to upload."
            : $"Selected {selected.Count} of {_scan.Users.Count} folders: " +
              $"{files:N0} files, {FolderScanner.FormatSize(bytes)} to upload.";
    }

    private void PopulateScanProblems(ScanResult scan)
    {
        _problemsGrid.Rows.Clear();

        foreach (var file in scan.AllFiles.Where(x => x.State == FileState.Skipped))
        {
            if (_problemsGrid.Rows.Count >= MaxProblemRows)
            {
                break;
            }

            AddProblemRow(file);
        }
    }

    // --------------------------------------------------------------- upload

    private async Task StartAsync()
    {
        if (_scan == null)
        {
            return;
        }

        // Failed files from an earlier pass are picked up again, which makes the
        // button double as "retry what did not make it".
        var work = new List<ScannedFile>();
        var selectedUsers = _scan.Users.Where(x => x.IsSelected).ToList();

        if (selectedUsers.Count == 0)
        {
            ShowWarning(
                "No folders are ticked. Tick the people whose data you want to " +
                "upload, then press Upload selected."
            );
            return;
        }

        foreach (var user in selectedUsers)
        {
            foreach (var file in user.Files)
            {
                if (file.State is FileState.Failed)
                {
                    user.FailedCount = Math.Max(0, user.FailedCount - 1);
                    file.State = FileState.Pending;
                    file.Message = "";
                }

                if (file.State == FileState.Pending)
                {
                    work.Add(file);
                }
            }
        }

        if (work.Count == 0)
        {
            ShowWarning(
                "Everything in the ticked folders is already uploaded or skipped."
            );
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Upload {work.Count:N0} files " +
            $"({FolderScanner.FormatSize(work.Sum(x => x.SizeBytes))}) from " +
            $"{selectedUsers.Count} folder(s)?\n\n" +
            string.Join("\n", selectedUsers.Take(12).Select(x =>
                $"  {x.UserFolder}  ->  {x.DeviceCode}")) +
            (selectedUsers.Count > 12 ? $"\n  ... and {selectedUsers.Count - 12} more" : ""),
            "Confirm upload",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question
        );

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SaveSettingsFromFields();

        _totalWorkFiles = work.Count;
        _totalWorkBytes = work.Sum(x => x.SizeBytes);
        _lastTransferredBytes = 0;
        _lastCompletedFiles = 0;
        _lastSampleAtUtc = DateTime.UtcNow;
        _filesPerSecond = 0;
        _bytesPerSecond = 0;

        _cancellation = new CancellationTokenSource();

        using var client = CreateClient();

        if (_readableFoldersBox.Checked)
        {
            try
            {
                var job = await client.UseReadableFoldersAsync(
                    _scannedRootPath,
                    "Old User Data",
                    CancellationToken.None
                );

                AppendLog(
                    $"Readable folders on. Objects go to " +
                    $"uploads/old-user-data/<person>/<path>. Job {job.JobId}."
                );
            }
            catch (Exception exception)
            {
                ShowError(
                    "The server did not accept the readable-folder request, so " +
                    "nothing was uploaded.\n\n" +
                    "This needs a server build that has the old-user-data push " +
                    "endpoints. Until it is deployed, untick \"Readable folders " +
                    "in storage\" to upload the existing way.\n\n" +
                    exception.Message
                );
                return;
            }
        }

        _runner = new UploadRunner(
            client,
            (int)_parallelBox.Value,
            (int)_retryBox.Value,
            _shaBox.Checked
        );

        SetRunning(true);
        AppendLog(
            $"Uploading {work.Count:N0} files " +
            $"({FolderScanner.FormatSize(_totalWorkBytes)}) with " +
            $"{_parallelBox.Value} at a time."
        );
        _refreshTimer.Start();

        try
        {
            await _runner.RunAsync(work, _cancellation.Token);
            RefreshProgress();
            AppendLog("Upload finished.");
            ReportOutcome(stopped: false);
        }
        catch (OperationCanceledException)
        {
            RefreshProgress();
            AppendLog("Upload stopped.");
            ReportOutcome(stopped: true);
        }
        catch (Exception exception)
        {
            RefreshProgress();
            AppendLog($"Upload failed: {exception.Message}");
            ShowError("The upload stopped with an error.\n\n" + exception.Message);
        }
        finally
        {
            _refreshTimer.Stop();
            RefreshProgress();

            // Written whether the run finished or was stopped, so a folder left
            // half done is described as exactly that next time.
            foreach (var user in selectedUsers)
            {
                _history.Record(_scannedRootPath, user);
                user.HistoryLabel = _history.Describe(_scannedRootPath, user);

                if (_userRowIndexes.TryGetValue(user.UserFolder, out var row) &&
                    row < _usersGrid.Rows.Count)
                {
                    _usersGrid.Rows[row].Cells[ColumnHistory].Value = user.HistoryLabel;
                }
            }

            _history.Save();

            // Cleared before SetRunning, which restates the status line and the
            // button from the current selection and ignores a run in progress.
            _runner = null;
            _cancellation?.Dispose();
            _cancellation = null;

            SetRunning(false);
        }
    }

    private void ReportOutcome(bool stopped)
    {
        if (_runner == null)
        {
            return;
        }

        var summary =
            $"Uploaded: {_runner.CompletedFiles:N0}\n" +
            $"Already on the server: {_runner.AlreadyPresentFiles:N0}\n" +
            $"Skipped: {_runner.SkippedFiles:N0}\n" +
            $"Failed: {_runner.FailedFiles:N0}";

        if (_runner.FailedFiles > 0)
        {
            summary +=
                "\n\nPress Start upload again to retry the failed files. " +
                "Anything already stored is not sent twice.";
        }

        MessageBox.Show(
            this,
            summary,
            stopped ? "Upload stopped" : "Upload finished",
            MessageBoxButtons.OK,
            _runner.FailedFiles > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information
        );
    }

    private void TogglePause()
    {
        if (_runner == null)
        {
            return;
        }

        if (_runner.IsPaused)
        {
            _runner.Resume();
            _pauseButton.Text = "Pause";
            AppendLog("Resumed.");
        }
        else
        {
            _runner.Pause();
            _pauseButton.Text = "Resume";
            AppendLog("Paused. Files already in flight will finish.");
        }
    }

    private void Stop()
    {
        _cancellation?.Cancel();
        _stopButton.Enabled = false;
        _statusLabel.Text = "Stopping after the files in flight finish...";
    }

    // ------------------------------------------------------------- progress

    private void RefreshProgress()
    {
        if (_runner == null || _scan == null)
        {
            return;
        }

        DrainFinishedFiles();
        DrainLog();

        var done = _runner.CompletedFiles +
                   _runner.AlreadyPresentFiles +
                   _runner.SkippedFiles +
                   _runner.FailedFiles;

        var fraction = _totalWorkFiles == 0 ? 0 : done / (double)_totalWorkFiles;
        _progressBar.Value = (int)Math.Clamp(fraction * 1000, 0, 1000);

        SampleRate(done);

        var remaining = Math.Max(0, _totalWorkFiles - done);
        var eta = _filesPerSecond > 0.01
            ? TimeSpan.FromSeconds(remaining / _filesPerSecond).ToString(@"d\d\ hh\:mm\:ss")
            : "unknown";

        _statusLabel.Text =
            $"{done:N0} / {_totalWorkFiles:N0} files  ({fraction:P1})   " +
            $"sent {FolderScanner.FormatSize(_runner.TransferredBytes)} of " +
            $"{FolderScanner.FormatSize(_totalWorkBytes)}   " +
            $"{_filesPerSecond:N1} files/s, " +
            $"{FolderScanner.FormatSize((long)_bytesPerSecond)}/s   " +
            $"ETA {eta}   " +
            $"failed {_runner.FailedFiles:N0}" +
            (_runner.IsPaused ? "   [paused]" : "");
    }

    private void SampleRate(long done)
    {
        var now = DateTime.UtcNow;
        var seconds = (now - _lastSampleAtUtc).TotalSeconds;

        if (seconds < 2)
        {
            return;
        }

        var transferred = _runner!.TransferredBytes;

        // Smoothed so the readout does not swing wildly between a big file and a
        // burst of small ones.
        var instantFiles = (done - _lastCompletedFiles) / seconds;
        var instantBytes = (transferred - _lastTransferredBytes) / seconds;

        _filesPerSecond = _filesPerSecond == 0
            ? instantFiles
            : (_filesPerSecond * 0.6) + (instantFiles * 0.4);
        _bytesPerSecond = _bytesPerSecond == 0
            ? instantBytes
            : (_bytesPerSecond * 0.6) + (instantBytes * 0.4);

        _lastCompletedFiles = done;
        _lastTransferredBytes = transferred;
        _lastSampleAtUtc = now;
    }

    private void DrainFinishedFiles()
    {
        if (_runner == null || _scan == null)
        {
            return;
        }

        var touchedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drained = 0;

        while (drained < 2000 && _runner.FinishedFiles.TryDequeue(out var file))
        {
            drained++;

            var user = _scan.Users.FirstOrDefault(x =>
                string.Equals(x.UserFolder, file.UserFolder, StringComparison.OrdinalIgnoreCase));

            if (user != null)
            {
                switch (file.State)
                {
                    case FileState.Completed:
                        user.UploadedCount++;
                        break;
                    case FileState.AlreadyOnServer:
                        user.AlreadyPresentCount++;
                        break;
                    case FileState.Skipped:
                        user.SkippedCount++;
                        break;
                    case FileState.Failed:
                        user.FailedCount++;
                        break;
                }

                touchedUsers.Add(user.UserFolder);
            }

            if (file.State is FileState.Failed or FileState.Skipped &&
                _problemsGrid.Rows.Count < MaxProblemRows)
            {
                AddProblemRow(file);
            }
        }

        foreach (var userFolder in touchedUsers)
        {
            UpdateUserRow(userFolder);
        }
    }

    private void UpdateUserRow(string userFolder)
    {
        if (_scan == null ||
            !_userRowIndexes.TryGetValue(userFolder, out var index) ||
            index >= _usersGrid.Rows.Count)
        {
            return;
        }

        var user = _scan.Users.FirstOrDefault(x =>
            string.Equals(x.UserFolder, userFolder, StringComparison.OrdinalIgnoreCase));

        if (user == null)
        {
            return;
        }

        var row = _usersGrid.Rows[index];
        var percent = user.Files.Count == 0
            ? 0
            : user.DoneCount * 100.0 / user.Files.Count;

        row.Cells[ColumnUploaded].Value = user.UploadedCount.ToString("N0");
        row.Cells[ColumnAlreadyThere].Value = user.AlreadyPresentCount.ToString("N0");
        row.Cells[ColumnSkipped].Value = user.SkippedCount.ToString("N0");
        row.Cells[ColumnFailed].Value = user.FailedCount.ToString("N0");
        row.Cells[ColumnProgress].Value = $"{percent:N0}%";

        if (user.FailedCount > 0)
        {
            row.DefaultCellStyle.ForeColor = Color.Firebrick;
        }
    }

    private void AddProblemRow(ScannedFile file)
    {
        var index = _problemsGrid.Rows.Add(
            file.UserFolder,
            file.State == FileState.Failed ? "Failed" : "Skipped",
            file.FileName,
            file.Message,
            file.FullPath
        );

        if (file.State == FileState.Failed)
        {
            _problemsGrid.Rows[index].DefaultCellStyle.ForeColor = Color.Firebrick;
        }
    }

    private void DrainLog()
    {
        if (_runner == null)
        {
            return;
        }

        var drained = 0;

        while (drained < 200 && _runner.Log.TryDequeue(out var line))
        {
            drained++;
            AppendLog(line);
        }
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";

        var lines = _logBox.Lines.ToList();
        lines.Add(line);

        if (lines.Count > MaxLogLines)
        {
            lines.RemoveRange(0, lines.Count - MaxLogLines);
        }

        _logBox.Lines = lines.ToArray();
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    // ---------------------------------------------------------------- state

    private UploadApiClient CreateClient() =>
        new(_apiUrlBox.Text.Trim(), _apiKeyBox.Text.Trim());

    private void SetBusy(bool busy)
    {
        _scanButton.Enabled = !busy;
        _browseButton.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void SetRunning(bool running)
    {
        _scanButton.Enabled = !running;
        _browseButton.Enabled = !running;
        _startButton.Enabled = !running;
        _pauseButton.Enabled = running;
        _stopButton.Enabled = running;
        _rootPathBox.ReadOnly = running;
        _apiUrlBox.ReadOnly = running;
        _apiKeyBox.ReadOnly = running;
        _prefixBox.ReadOnly = running;
        _parallelBox.Enabled = !running;
        _retryBox.Enabled = !running;
        _shaBox.Enabled = !running;
        _singlePersonBox.Enabled = !running;
        _readableFoldersBox.Enabled = !running;

        _usersGrid.ReadOnly = running;
        _selectAllButton.Enabled = !running;
        _selectNoneButton.Enabled = !running;

        if (!running)
        {
            _pauseButton.Text = "Pause";
            UpdateSelectionSummary();
        }
    }

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void ShowError(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
