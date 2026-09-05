<#
.SYNOPSIS
    Read-only status view for the Old User Data import. Safe to run any time.

.DESCRIPTION
    Queries the local API that tools/run-import.ps1 is hosting. Run it from a second
    terminal - it never changes job state, so it cannot disturb a running import.

.EXAMPLE
    .\tools\import-status.ps1
    Job summary plus overall progress.

.EXAMPLE
    .\tools\import-status.ps1 -Users
    Per-user-folder breakdown.

.EXAMPLE
    .\tools\import-status.ps1 -Failed
    The files that failed, with their error messages.

.EXAMPLE
    .\tools\import-status.ps1 -Watch
    Refreshes every 10 seconds with a throughput estimate.
#>
[CmdletBinding()]
param(
    [int]$ApiPort = 8081,
    [string]$JobId = "",
    [switch]$Users,
    [switch]$Failed,
    [switch]$Watch,
    [int]$RefreshSeconds = 10,
    [int]$Top = 30
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-ImportKey {
    foreach ($scope in @('Process', 'User', 'Machine')) {
        $value = [Environment]::GetEnvironmentVariable('EPDESK_IMPORT_KEY', $scope)
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }

    $envFile = Join-Path $repoRoot '.env'
    if (Test-Path -LiteralPath $envFile) {
        foreach ($line in [IO.File]::ReadAllLines($envFile)) {
            $trimmed = $line.Trim()
            if ($trimmed.StartsWith('#')) { continue }
            if ($trimmed -match '^EPDESK_IMPORT_KEY\s*=\s*(.+)$') {
                return $Matches[1].Trim()
            }
        }
    }

    throw 'EPDESK_IMPORT_KEY not found in the environment or .env.'
}

function Format-Size([long]$bytes) {
    if ($bytes -ge 1TB) { return "{0:N2} TB" -f ($bytes / 1TB) }
    if ($bytes -ge 1GB) { return "{0:N2} GB" -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return "{0:N1} MB" -f ($bytes / 1MB) }
    if ($bytes -ge 1KB) { return "{0:N0} KB" -f ($bytes / 1KB) }
    return "$bytes B"
}

$headers = @{ 'X-Import-Key' = (Get-ImportKey) }
$baseUri = "http://127.0.0.1:$ApiPort/api/admin/old-user-data"

function Get-ActiveJob {
    $jobs = @(Invoke-RestMethod -Uri $baseUri -Headers $headers -TimeoutSec 30)
    if ($jobs.Count -eq 0) { throw 'No import jobs exist yet.' }
    if ($JobId) {
        $match = $jobs | Where-Object { $_.id -eq $JobId }
        if (-not $match) { throw "Job $JobId was not found." }
        return $match
    }
    # The endpoint already sorts newest-first.
    return $jobs[0]
}

function Show-Job($job, $previous, $elapsedSeconds) {
    $indexed = [long]$job.indexedFileCount
    $done = [long]$job.uploadedFileCount
    $failed = [long]$job.failedFileCount
    $percent = 0
    if ($indexed -gt 0) { $percent = [Math]::Round(($done * 100.0) / $indexed, 2) }

    $colour = 'Green'
    if ($job.status -in @('paused', 'failed', 'completed_with_errors')) { $colour = 'Yellow' }

    Write-Host ''
    Write-Host "Job      : $($job.id)"
    Write-Host "Status   : $($job.status)" -ForegroundColor $colour
    Write-Host "Scanned  : $indexed files / $(Format-Size ([long]$job.indexedSizeBytes))"
    Write-Host "Uploaded : $done files / $(Format-Size ([long]$job.uploadedSizeBytes))  ($percent%)"
    Write-Host "Failed   : $failed"
    Write-Host "Updated  : $($job.updatedAtUtc) UTC"

    if ($job.errorMessage) {
        Write-Host "Error    : $($job.errorMessage)" -ForegroundColor Red
    }

    $barWidth = 40
    $filled = 0
    if ($indexed -gt 0) { $filled = [int][Math]::Floor(($done / [double]$indexed) * $barWidth) }
    $bar = ('#' * $filled) + ('.' * ($barWidth - $filled))
    Write-Host "[$bar] $percent%"

    if ($null -ne $previous -and $elapsedSeconds -gt 0) {
        $deltaFiles = $done - [long]$previous.uploadedFileCount
        $deltaBytes = [long]$job.uploadedSizeBytes - [long]$previous.uploadedSizeBytes

        if ($deltaFiles -gt 0) {
            $filesPerSecond = $deltaFiles / $elapsedSeconds
            $remaining = $indexed - $done
            $etaText = 'unknown'
            if ($filesPerSecond -gt 0 -and $remaining -gt 0) {
                $eta = [TimeSpan]::FromSeconds($remaining / $filesPerSecond)
                $etaText = "{0:d\d\ hh\:mm\:ss}" -f $eta
            }
            Write-Host ("Rate     : {0:N1} files/s, {1}/s   ETA {2}" -f `
                    $filesPerSecond, (Format-Size ([long]($deltaBytes / $elapsedSeconds))), $etaText) -ForegroundColor Cyan
        }
        else {
            Write-Host 'Rate     : no files completed since the last refresh' -ForegroundColor DarkGray
        }
    }
}

function Show-Users($job) {
    $users = @(Invoke-RestMethod -Uri "$baseUri/$($job.id)/users" -Headers $headers -TimeoutSec 60)
    Write-Host ''
    Write-Host "Per-user breakdown ($($users.Count) folders):"
    $users |
        Sort-Object { [long]$_.indexedFileCount } -Descending |
        Select-Object -First $Top |
        Format-Table -AutoSize @(
            @{ Label = 'User'; Expression = { $_.userFolder } }
            @{ Label = 'Files'; Expression = { $_.indexedFileCount } }
            @{ Label = 'Done'; Expression = { $_.uploadedFileCount } }
            @{ Label = 'Failed'; Expression = { $_.failedFileCount } }
            @{ Label = 'Skipped'; Expression = { $_.skippedFileCount } }
            @{ Label = 'Size'; Expression = { Format-Size ([long]$_.indexedSizeBytes) } }
        ) | Out-Host
}

function Show-Failed($job) {
    foreach ($state in @('failed', 'missing')) {
        $page = Invoke-RestMethod -Uri "$baseUri/$($job.id)/files?status=$state&take=$Top" `
            -Headers $headers -TimeoutSec 60
        Write-Host ''
        Write-Host "Status '$state': $($page.total) file(s)"
        if ($page.total -eq 0) { continue }
        $page.files | Format-Table -AutoSize @(
            @{ Label = 'User'; Expression = { $_.userFolder } }
            @{ Label = 'File'; Expression = { $_.fileName } }
            @{ Label = 'Tries'; Expression = { $_.attemptCount } }
            @{ Label = 'Error'; Expression = {
                    if ($_.errorMessage.Length -gt 70) { $_.errorMessage.Substring(0, 70) + '...' }
                    else { $_.errorMessage }
                }
            }
        ) | Out-Host
    }
}

try {
    if (-not $Watch) {
        $job = Get-ActiveJob
        Show-Job $job $null 0
        if ($Users) { Show-Users $job }
        if ($Failed) { Show-Failed $job }
        Write-Host ''
        return
    }

    $previous = $null
    $previousAtUtc = [DateTime]::UtcNow

    while ($true) {
        $job = Get-ActiveJob
        $now = [DateTime]::UtcNow
        $elapsed = ($now - $previousAtUtc).TotalSeconds

        Clear-Host
        Write-Host "Watching import - Ctrl+C to stop (refresh ${RefreshSeconds}s)" -ForegroundColor DarkGray
        Show-Job $job $previous $elapsed
        if ($Users) { Show-Users $job }
        if ($Failed) { Show-Failed $job }

        if ($job.status -in @('completed', 'completed_with_errors')) {
            Write-Host ''
            Write-Host "Job finished with status '$($job.status)'." -ForegroundColor Green
            return
        }

        $previous = $job
        $previousAtUtc = $now
        Start-Sleep -Seconds $RefreshSeconds
    }
}
catch {
    Write-Host "Could not reach the API on port $ApiPort. Is tools\run-import.ps1 running?" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor DarkGray
    exit 1
}
