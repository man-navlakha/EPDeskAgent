<#
.SYNOPSIS
    One-command supervisor for the Old User Data import: Railway tunnel + EPDesk API.

.DESCRIPTION
    Starts and babysits the API and its database connection from a single terminal:

      * Railway's public Postgres TCP proxy is used first. After repeated end-to-end
        health failures the supervisor opens a tunnel on a fixed local port and
        restarts the API against it. A dropped fallback tunnel is rebuilt in place.
      * EPDeskServerApi, running the built DLL directly (single process, clean kill).

    Health is measured end-to-end by polling the admin jobs endpoint, which reads the
    database. If the API itself has died it is restarted too. After every recovery the
    script calls /resume so files that were marked "failed" during the outage are
    requeued.

    Secrets are read from .env in the repo root, then from User environment variables,
    and only prompted for when neither has them. Nothing is echoed to the console.

.EXAMPLE
    .\tools\run-import.ps1

.EXAMPLE
    .\tools\run-import.ps1 -Build -TunnelPort 45432 -ApiPort 8081
#>
[CmdletBinding()]
param(
    # Must sit below the Windows dynamic range floor (49152). A port inside it can
    # be handed to any process that asks for an ephemeral one - on this machine the
    # Acronis agent had taken 55432 - and the bind then fails with EACCES.
    [int]$TunnelPort = 45432,
    [int]$ApiPort = 8081,
    [string]$RootPath = "E:\Users Data",
    [string]$Database = "railway",
    [string]$DbUser = "postgres",
    [string]$Service = "postgres",
    [int]$DbRemotePort = 5432,
    # Railway's own name for the database service, used to read its TCP proxy
    # address. Case matters here in a way it does not for `railway connect`.
    [string]$DbService = "Postgres",
    [int]$HealthIntervalSeconds = 15,
    [int]$FailuresBeforeRestart = 3,
    [int]$MaxAutoResumes = 5,
    [int]$CrashLoopSeconds = 60,
    [int]$MaxApiCrashes = 3,
    # How many files upload at once, and how many parts of one large file upload
    # at once. Uploads are latency bound, so these are the two speed dials.
    [int]$UploadConcurrency = 8,
    [int]$PartConcurrency = 4,
    [switch]$Build,
    [switch]$NoAutoResume,
    # Route the database through `railway connect --tunnel-only` instead of the
    # service's public TCP proxy. The tunnel adds roughly 250ms to every query and
    # takes the import down with it whenever the SSH session drops, so it is only
    # worth using when the proxy is unavailable.
    [switch]$UseTunnel
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'EPDeskServerApi\EPDeskServerApi.csproj'
$appDll = Join-Path $repoRoot 'EPDeskServerApi\bin\Release\net8.0\EPDeskServerApi.dll'
$appDir = Split-Path -Parent $appDll
$logDir = Join-Path $repoRoot 'artifacts\import-logs'
$tunnelOut = Join-Path $logDir 'tunnel.out.log'
$tunnelErr = Join-Path $logDir 'tunnel.err.log'

# ---------------------------------------------------------------- helpers ----

function Write-Step([string]$message) {
    Write-Host "[run-import] $message" -ForegroundColor Cyan
}

function Write-Warn([string]$message) {
    Write-Host "[run-import] $message" -ForegroundColor Yellow
}

function Import-DotEnv([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $count = 0
    foreach ($line in (Get-Content -LiteralPath $path -Encoding UTF8)) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
        $split = $trimmed.IndexOf('=')
        if ($split -lt 1) { continue }
        $key = $trimmed.Substring(0, $split).Trim()
        $value = $trimmed.Substring($split + 1).Trim()
        if ($value.Length -ge 2) {
            $first = $value[0]
            $last = $value[$value.Length - 1]
            if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }
        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
        $count++
    }
    return $count
}

function Resolve-Secret([string]$name, [string]$prompt) {
    foreach ($scope in @('Process', 'User', 'Machine')) {
        $value = [Environment]::GetEnvironmentVariable($name, $scope)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
            return $value
        }
    }
    $secure = Read-Host -Prompt $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $value = [Runtime.InteropServices.Marshal]::PtrToStringAuto($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$name is required but was not supplied."
    }
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    return $value
}

function Test-PortOpen([int]$port, [int]$timeoutMs = 2000) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($timeoutMs)) { return $false }
        $client.EndConnect($async)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

function Test-PortBindable([int]$port) {
    # Test-PortOpen answers "is anyone serving here", which is not the same question
    # as "can ssh listen here". A port pinned by another process's ESTABLISHED socket
    # accepts no connection yet still refuses the bind, so the only honest check is
    # to attempt it. Returns $null when the port is free, else the winsock message.
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
        $listener.Start()
        return $null
    }
    catch { return $_.Exception.GetBaseException().Message }
    finally { if ($null -ne $listener) { try { $listener.Stop() } catch { } } }
}

function Get-PortOwnerPid([int]$port) {
    # netstat rather than Get-NetTCPConnection: the CIM query behind the cmdlet can
    # stall for minutes on a busy machine, which is exactly when this check runs.
    # Both the IPv4 and IPv6 listen rows are matched, then deduplicated.
    $owners = @()
    foreach ($line in (netstat -ano -p tcp)) {
        if ($line -match '^\s*TCP\s+(\S+):(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$' -and
            [int]$Matches[2] -eq $port) {
            $owners += [int]$Matches[3]
        }
    }
    return @($owners | Select-Object -Unique)
}

function Get-ProcessCommandLine([int]$processId) {
    try {
        return (Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction Stop).CommandLine
    }
    catch { return $null }
}

function Clear-ApiPort([int]$port) {
    # A run that is killed at the console never reaches its finally block, so the
    # previous API keeps the port and every restart here dies with AddressInUse -
    # three times over, burning the crash budget on a stale process. Its job state
    # lives in the database, so stopping it costs nothing but the in-flight uploads,
    # which /resume requeues anyway.
    foreach ($ownerPid in (Get-PortOwnerPid $port)) {
        $owner = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
        if ($null -eq $owner) { continue }

        $commandLine = Get-ProcessCommandLine $ownerPid
        if ($owner.Name -ne 'dotnet' -or $commandLine -notlike '*EPDeskServerApi.dll*') {
            throw "Port $port is held by $($owner.Name) (pid $ownerPid), which is not this API. Stop it, or rerun with -ApiPort <free port>."
        }

        $ownerInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerPid" -ErrorAction SilentlyContinue
        $parent = if ($null -ne $ownerInfo) {
            Get-Process -Id $ownerInfo.ParentProcessId -ErrorAction SilentlyContinue
        }
        else { $null }
        if ($null -ne $parent -and
            $parent.Name -in @('powershell', 'pwsh') -and
            $parent.Id -ne $PID) {
            throw "Another run-import supervisor (pid $($parent.Id)) already owns API port $port. Keep that run open; do not start a second copy."
        }

        Write-Warn "Port $port is held by an earlier EPDeskServerApi (pid $ownerPid) from $($owner.StartTime.ToString('HH:mm:ss')). Stopping it."
        try { Stop-Process -Id $ownerPid -Force -ErrorAction Stop } catch { }
        try { $owner.WaitForExit(10000) | Out-Null } catch { }
    }

    for ($wait = 0; $wait -lt 20; $wait++) {
        if ((Get-PortOwnerPid $port).Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Port $port is still in use after stopping its owner. Rerun with -ApiPort <free port>."
}

function Resolve-RailwayExe {
    # Start-Process needs a real Win32 binary. On this machine `railway` on PATH is
    # an npm shim (railway.ps1 / railway.cmd), and launching the .cmd would wrap the
    # tunnel in a cmd.exe whose child survives Stop-Process and keeps the port bound.
    $candidates = @(Get-Command railway -All -ErrorAction SilentlyContinue)

    foreach ($candidate in $candidates) {
        if ($candidate.CommandType -eq 'Application' -and
            $candidate.Source -like '*.exe') {
            return $candidate.Source
        }
    }

    foreach ($candidate in $candidates) {
        $shimDir = Split-Path -Parent $candidate.Source
        $nested = Join-Path $shimDir 'node_modules\@railway\cli\bin\railway.exe'
        if (Test-Path -LiteralPath $nested) { return (Resolve-Path $nested).Path }
    }

    throw 'Could not find railway.exe. Install the Railway CLI or add its .exe to PATH.'
}

function Resolve-SshExe {
    $ssh = Get-Command ssh.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $ssh) {
        throw 'Could not find ssh.exe. Install the Windows OpenSSH Client optional feature.'
    }
    return $ssh.Source
}

function Resolve-RailwaySshTarget([string]$railwayExe, [string]$service) {
    # `railway connect --tunnel-only` has a hard-coded 10-second readiness
    # deadline. SSH setup from this network can legitimately take longer, so ask
    # Railway for the current deployment target and run the forward ourselves.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $railwayExe ssh config --service $service --dry-run 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }

    $lines = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
    $stderr = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } |
        ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ })

    if ($exitCode -ne 0) {
        $detail = if ($stderr.Count -gt 0) { " CLI said: $($stderr -join ' ')" } else { '' }
        throw "Could not resolve the Railway SSH target for '$service' (exit $exitCode).$detail"
    }

    $sshHost = (($lines | Where-Object { $_ -match '^\s*HostName\s+' } |
        Select-Object -First 1) -replace '^\s*HostName\s+', '').Trim()
    $sshUser = (($lines | Where-Object { $_ -match '^\s*User\s+' } |
        Select-Object -First 1) -replace '^\s*User\s+', '').Trim()

    if ([string]::IsNullOrWhiteSpace($sshHost) -or
        [string]::IsNullOrWhiteSpace($sshUser)) {
        throw "Railway did not return an SSH host and user for '$service'. Run ``railway ssh keys list`` and confirm a local key is registered."
    }

    return "$sshUser@$sshHost"
}

function Resolve-DbEndpoint([string]$railwayExe, [string]$service) {
    # Railway publishes the database over a public TCP proxy whose host and port
    # change when the service is recreated, so read them instead of pinning them.
    #
    # The CLI writes a progress spinner to stderr. In Windows PowerShell 5.1 any
    # redirection of a native command's stderr turns those lines into ErrorRecords,
    # which the script-wide 'Stop' preference then treats as terminating - so the
    # call has to run under 'Continue' and filter the records out of the pipeline.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $railwayExe variables --service $service --kv 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }

    $lines = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
    $stderr = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } |
        ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ })

    if ($exitCode -ne 0 -or $lines.Count -eq 0) {
        $detail = if ($stderr.Count -gt 0) { " CLI said: $($stderr -join ' ')" } else { '' }
        throw "Could not read variables for the '$service' service (exit $exitCode).$detail Check ``railway status``, confirm the service name, or pass -UseTunnel to fall back to the SSH tunnel."
    }

    # Not $host - that name is a read-only PowerShell automatic variable.
    $proxyHost = ($lines | Where-Object { $_ -like 'RAILWAY_TCP_PROXY_DOMAIN=*' } |
        Select-Object -First 1) -replace '^RAILWAY_TCP_PROXY_DOMAIN=', ''
    $proxyPort = ($lines | Where-Object { $_ -like 'RAILWAY_TCP_PROXY_PORT=*' } |
        Select-Object -First 1) -replace '^RAILWAY_TCP_PROXY_PORT=', ''

    if ([string]::IsNullOrWhiteSpace($proxyHost) -or [string]::IsNullOrWhiteSpace($proxyPort)) {
        throw "The '$service' service has no public TCP proxy. Enable one in the Railway dashboard, or pass -UseTunnel."
    }

    return @{ Host = $proxyHost.Trim(); Port = [int]$proxyPort.Trim() }
}

function Stop-PortOwner([int]$port) {
    # Clears an orphaned tunnel left behind by a hard kill, so the fixed port rebinds.
    foreach ($ownerPid in (Get-PortOwnerPid $port)) {
        $process = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        if ($process.Name -notmatch '^(railway|ssh)$') {
            Write-Warn "Port $port is held by $($process.Name) (pid $($process.Id)); not killing it."
            continue
        }

        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction SilentlyContinue
        $parent = if ($null -ne $processInfo) {
            Get-Process -Id $processInfo.ParentProcessId -ErrorAction SilentlyContinue
        }
        else { $null }
        if ($null -ne $parent -and
            $parent.Name -in @('powershell', 'pwsh') -and
            $parent.Id -ne $PID) {
            throw "Another run-import supervisor (pid $($parent.Id)) already owns tunnel port $port. Keep that run open; do not start a second copy."
        }

        Write-Warn "Killing orphaned $($process.Name) (pid $($process.Id)) holding port $port."
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
    }
}

function Test-ProcessAlive($process) {
    if ($null -eq $process) { return $false }
    try { return -not $process.HasExited } catch { return $false }
}

function Stop-Child($process, [string]$label) {
    if (-not (Test-ProcessAlive $process)) { return }
    Write-Step "Stopping $label (pid $($process.Id))."
    try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
    try { $process.WaitForExit(10000) | Out-Null } catch { }
}

# ------------------------------------------------------------ preparation ----

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Prevent two supervisors launched after this version from racing before either
# has opened its API/tunnel port. The port-owner checks above also protect an
# older supervisor that was already running when this guard was added.
$supervisorMutex = New-Object System.Threading.Mutex(
    $false,
    'Local\EPDeskAgent.OldUserDataImportSupervisor'
)
$supervisorMutexAcquired = $false
try {
    $supervisorMutexAcquired = $supervisorMutex.WaitOne(0)
}
catch [System.Threading.AbandonedMutexException] {
    $supervisorMutexAcquired = $true
}
if (-not $supervisorMutexAcquired) {
    $supervisorMutex.Dispose()
    throw 'Another run-import supervisor is already running. Use tools\import-status.ps1 to monitor it instead of starting a second copy.'
}

$loaded = Import-DotEnv (Join-Path $repoRoot '.env')
Write-Step "Loaded $loaded settings from .env."

foreach ($required in @('B2__KeyId', 'B2__ApplicationKey', 'B2__BucketName', 'B2__ServiceUrl', 'B2__Region')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($required, 'Process'))) {
        throw "$required is missing. Add it to $repoRoot\.env."
    }
}

# Mirror B2ObjectStorageService.ValidateOptions so a bad .env fails here with one
# clear line instead of a DI stack trace after migrations have already run.
$serviceUrl = [Environment]::GetEnvironmentVariable('B2__ServiceUrl', 'Process')
$serviceUri = $null
if (-not [Uri]::TryCreate($serviceUrl, [UriKind]::Absolute, [ref]$serviceUri)) {
    throw "B2__ServiceUrl is not an absolute URL: '$serviceUrl'. It needs the https:// scheme."
}
if ($serviceUri.Scheme -ne 'https') {
    throw "B2__ServiceUrl must use https, got '$($serviceUri.Scheme)'."
}

# Same for AgentFileUploadSecurityOptions.HasValidApiKey, which runs under
# ValidateOnStart and otherwise kills the host after migrations have already run.
$uploadKey = [Environment]::GetEnvironmentVariable('Security__AgentFileUploadApiKey', 'Process')
if ([string]::IsNullOrWhiteSpace($uploadKey)) {
    throw "Security__AgentFileUploadApiKey is missing. Add it to $repoRoot\.env (32+ characters)."
}
if ($uploadKey.Length -lt 32) {
    throw "Security__AgentFileUploadApiKey is $($uploadKey.Length) characters; it needs at least 32."
}
if ($uploadKey -ne $uploadKey.Trim()) {
    throw 'Security__AgentFileUploadApiKey has leading or trailing whitespace.'
}
if ($uploadKey.ToCharArray() | Where-Object { [char]::IsControl($_) }) {
    throw 'Security__AgentFileUploadApiKey contains control characters.'
}

$dbPassword = Resolve-Secret 'RAILWAY_DB_PASSWORD' 'Railway database password'
$importKey = Resolve-Secret 'EPDESK_IMPORT_KEY'   'EPDesk import API key'

if (-not (Test-Path -LiteralPath $RootPath)) {
    throw "Old user data root is not reachable: $RootPath"
}

$railwayExe = Resolve-RailwayExe
Write-Step "Using Railway CLI: $railwayExe"
$sshExe = Resolve-SshExe

if ($UseTunnel) {
    $dbHost = '127.0.0.1'
    $dbPort = $TunnelPort
    Write-Step "Database via SSH tunnel on 127.0.0.1:$TunnelPort."
}
else {
    $endpoint = Resolve-DbEndpoint $railwayExe $DbService
    $dbHost = $endpoint.Host
    $dbPort = $endpoint.Port
    Write-Step "Database via public TCP proxy at $dbHost`:$dbPort (no tunnel)."
}

$usingTunnel = $UseTunnel.IsPresent

# Pool size has to clear UploadConcurrency, because every upload loop opens its own
# scoped DbContext and they are all in flight at once.
$poolSize = [Math]::Max(20, $UploadConcurrency * 2 + 8)

$env:ConnectionStrings__DefaultConnection = "Host=$dbHost;Port=$dbPort;Database=$Database;Username=$DbUser;Password=$dbPassword;SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=$poolSize;Timeout=120;Command Timeout=120"
$env:OldUserDataImport__Enabled = 'true'
$env:OldUserDataImport__RootPath = $RootPath
$env:OldUserDataImport__ApiKey = $importKey
$env:OldUserDataImport__UploadConcurrency = "$UploadConcurrency"
$env:OldUserDataImport__PartConcurrency = "$PartConcurrency"
$env:PORT = "$ApiPort"
$env:ASPNETCORE_ENVIRONMENT = 'Production'

if ($Build -or -not (Test-Path -LiteralPath $appDll)) {
    Write-Step 'Building EPDeskServerApi (Release).'
    & dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
}

$headers = @{ 'X-Import-Key' = $importKey }
$jobsUri = "http://127.0.0.1:$ApiPort/api/admin/old-user-data"

# ----------------------------------------------------------- process mgmt ----

$tunnel = $null
$api = $null

function Start-Tunnel {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        if (Test-PortOpen $TunnelPort 500) {
            Write-Warn "Port $TunnelPort is still busy; waiting for it to free up."
            Stop-PortOwner $TunnelPort
            Start-Sleep -Seconds 3
            continue
        }

        $bindError = Test-PortBindable $TunnelPort
        if ($null -ne $bindError) {
            # Retrying cannot clear this. Nothing is listening for Stop-PortOwner to
            # find, and the holder is an unrelated long-lived process that is not
            # ours to kill, so fail loudly here instead of blaming the tunnel log.
            throw ("Port $TunnelPort cannot be bound ($bindError). Another process " +
                   "holds it; 'netstat -ano | findstr $TunnelPort' names the owner. " +
                   "Rerun with -TunnelPort <free port below 49152>.")
        }

        $sshTarget = Resolve-RailwaySshTarget $railwayExe $Service
        Write-Step "Opening Railway SSH tunnel on 127.0.0.1:$TunnelPort (attempt $attempt)."
        $forward = "127.0.0.1:$TunnelPort`:127.0.0.1:$DbRemotePort"
        $process = Start-Process -FilePath $sshExe `
            -ArgumentList @(
                # This machine receives a NAT64 AAAA answer for ssh.railway.com,
                # but that route black-holes during the SSH banner exchange.
                # Railway's IPv4 endpoint is healthy and establishes immediately.
                '-4',
                '-N',
                '-T',
                '-o', 'BatchMode=yes',
                '-o', 'StrictHostKeyChecking=accept-new',
                '-o', 'ExitOnForwardFailure=yes',
                '-o', 'ConnectTimeout=60',
                '-o', 'ServerAliveInterval=30',
                '-o', 'ServerAliveCountMax=3',
                '-L', $forward,
                $sshTarget
            ) `
            -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $tunnelOut -RedirectStandardError $tunnelErr

        # Railway's SSH edge can take 20-30 seconds to establish from this
        # network. Give it a full minute before treating it as unavailable.
        for ($wait = 0; $wait -lt 120; $wait++) {
            if (-not (Test-ProcessAlive $process)) { break }
            if (Test-PortOpen $TunnelPort 500) {
                Write-Step "Tunnel is up (pid $($process.Id))."
                return $process
            }
            Start-Sleep -Milliseconds 500
        }

        Stop-Child $process 'tunnel'
        Write-Warn "Tunnel did not come up. See $tunnelErr"
        Start-Sleep -Seconds ([Math]::Min(30, $attempt * 5))
    }
    throw "Could not open the Railway tunnel on port $TunnelPort."
}

function Start-Api {
    Clear-ApiPort $ApiPort
    Write-Step "Starting API on http://0.0.0.0:$ApiPort (logs stream below)."
    return Start-Process -FilePath 'dotnet' -ArgumentList @($appDll) `
        -WorkingDirectory $appDir -NoNewWindow -PassThru
}

function Wait-ApiReady($process, [int]$timeoutSeconds = 240) {
    # The API runs EF migrations before it binds its port, and against a remote
    # database that easily outlasts one health interval. Without this gate the
    # monitor loop counts a slow start as three failures and arms a resume that
    # nothing actually needed.
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)

    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-ProcessAlive $process)) { return $false }
        try {
            Invoke-RestMethod -Uri $jobsUri -Headers $headers -TimeoutSec 15 | Out-Null
            return $true
        }
        catch { Start-Sleep -Seconds 3 }
    }

    return $false
}

function Invoke-Resume([string]$jobId) {
    try {
        Invoke-RestMethod -Method Post -Uri "$jobsUri/$jobId/resume" `
            -Headers $headers -TimeoutSec 30 | Out-Null
        Write-Step "Resumed job $jobId."
    }
    catch {
        Write-Warn "Resume failed for $jobId : $($_.Exception.Message)"
    }
}

# ------------------------------------------------------------------- main ----

try {
    Write-Step "Upload concurrency: $UploadConcurrency file(s), $PartConcurrency part(s) per file."

    if ($usingTunnel) { $tunnel = Start-Tunnel }
    $api = Start-Api
    $apiStartedAtUtc = [DateTime]::UtcNow
    $apiCrashes = 0

    if (Wait-ApiReady $api) {
        Write-Step 'API is ready. Watching the import.'
    }
    elseif (-not $usingTunnel) {
        Write-Warn 'The API could not start through the public database proxy. Failing over to a Railway tunnel.'
        try {
            $tunnel = Start-Tunnel
            $usingTunnel = $true
            $env:ConnectionStrings__DefaultConnection = "Host=127.0.0.1;Port=$TunnelPort;Database=$Database;Username=$DbUser;Password=$dbPassword;SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=$poolSize;Timeout=120;Command Timeout=120"
            Stop-Child $api 'API'
            $api = Start-Api
            $apiStartedAtUtc = [DateTime]::UtcNow

            if (Wait-ApiReady $api) {
                Write-Step 'API is ready through the Railway tunnel. Watching the import.'
            }
            else {
                Write-Warn 'API did not answer after tunnel failover. Continuing to watch it anyway.'
            }
        }
        catch {
            Write-Warn "Startup tunnel failover did not start: $($_.Exception.Message)"
        }
    }
    else {
        Write-Warn 'API did not answer during startup. Continuing to watch it anyway.'
    }

    $consecutiveFailures = 0
    $recovering = $false
    # Files that fail for a real reason (missing on disk, policy) must not be
    # retried forever, so each job gets a bounded number of automatic resumes.
    $resumeAttempts = @{}

    while ($true) {
        Start-Sleep -Seconds $HealthIntervalSeconds

        if ($usingTunnel -and -not (Test-ProcessAlive $tunnel)) {
            Write-Warn 'Tunnel process exited. Reconnecting.'
            $tunnel = Start-Tunnel
            $recovering = $true
            $consecutiveFailures = 0
        }

        if (-not (Test-ProcessAlive $api)) {
            # An API that dies almost immediately is a fatal config error, not a blip.
            # Restarting it forever would just scroll the real stack trace off screen.
            $ranFor = ([DateTime]::UtcNow - $apiStartedAtUtc).TotalSeconds
            if ($ranFor -lt $CrashLoopSeconds) {
                $apiCrashes++
                Write-Warn "API exited after $([Math]::Round($ranFor, 1))s (crash $apiCrashes/$MaxApiCrashes)."
                if ($apiCrashes -ge $MaxApiCrashes) {
                    throw "API keeps crashing on startup. Scroll up for its exception - it is almost always a bad value in $repoRoot\.env."
                }
            }
            else {
                $apiCrashes = 0
                Write-Warn 'API process exited. Restarting.'
            }

            if ($usingTunnel) {
                Write-Warn 'Rebuilding the tunnel before restarting the API.'
                Stop-Child $tunnel 'tunnel'
                $tunnel = Start-Tunnel
            }

            $api = Start-Api
            $apiStartedAtUtc = [DateTime]::UtcNow
            $consecutiveFailures = 0
            Wait-ApiReady $api | Out-Null
            continue
        }

        $jobs = $null
        try {
            $jobs = Invoke-RestMethod -Uri $jobsUri -Headers $headers -TimeoutSec 30
            $consecutiveFailures = 0
        }
        catch {
            $consecutiveFailures++
            Write-Warn "Health check $consecutiveFailures/$FailuresBeforeRestart failed: $($_.Exception.Message)"

            if ($consecutiveFailures -ge $FailuresBeforeRestart) {
                if ($usingTunnel) {
                    Write-Warn 'Health checks are failing. Rebuilding the tunnel.'
                    Stop-Child $tunnel 'tunnel'
                    $tunnel = Start-Tunnel
                }
                else {
                    Write-Warn 'The public database proxy is still unavailable. Failing over to a Railway tunnel.'
                    try {
                        $tunnel = Start-Tunnel
                        $usingTunnel = $true
                        $env:ConnectionStrings__DefaultConnection = "Host=127.0.0.1;Port=$TunnelPort;Database=$Database;Username=$DbUser;Password=$dbPassword;SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=$poolSize;Timeout=120;Command Timeout=120"

                        # Npgsql builds its data source when the host starts, so the
                        # API must restart before it can use the fallback connection.
                        Stop-Child $api 'API'
                        $api = Start-Api
                        $apiStartedAtUtc = [DateTime]::UtcNow
                        Wait-ApiReady $api | Out-Null
                    }
                    catch {
                        Write-Warn "Tunnel failover did not start: $($_.Exception.Message)"
                    }
                }
                $recovering = $true
                $consecutiveFailures = 0
            }
            continue
        }

        $active = @($jobs | Where-Object {
                $_.status -in @('pending', 'scanning', 'uploading', 'paused', 'completed_with_errors', 'failed')
            })

        if ($active.Count -eq 0) {
            Write-Step 'No active import job. All jobs are complete.'
            $recovering = $false
            continue
        }

        foreach ($job in $active) {
            $indexed = [long]$job.indexedFileCount
            $done = [long]$job.uploadedFileCount
            $percent = 0
            if ($indexed -gt 0) { $percent = [Math]::Round(($done * 100.0) / $indexed, 1) }

            Write-Step ("{0} | {1} | {2}/{3} files ({4}%) | failed {5}" -f `
                    $job.id, $job.status, $done, $indexed, $percent, $job.failedFileCount)

            $stalled = $job.status -in @('paused', 'failed', 'completed_with_errors')
            $needsResume = $stalled -or ($recovering -and [long]$job.failedFileCount -gt 0)

            if (-not $needsResume -or $NoAutoResume) {
                # Healthy progress clears the budget, so a later outage gets a fresh one.
                $resumeAttempts[$job.id] = 0
                continue
            }

            $used = 0
            if ($resumeAttempts.ContainsKey($job.id)) { $used = $resumeAttempts[$job.id] }

            if ($used -ge $MaxAutoResumes) {
                Write-Warn ("Job {0} still reports '{1}' after {2} automatic resumes. Leaving it alone - inspect {3}/{0}/files." -f `
                        $job.id, $job.status, $used, $jobsUri)
                continue
            }

            $resumeAttempts[$job.id] = $used + 1
            Invoke-Resume $job.id
        }

        $recovering = $false
    }
}
finally {
    Write-Host ''
    Write-Step 'Shutting down.'
    Stop-Child $api 'API'
    Stop-Child $tunnel 'tunnel'
    if ($supervisorMutexAcquired) {
        try { $supervisorMutex.ReleaseMutex() } catch { }
    }
    $supervisorMutex.Dispose()
    Write-Step 'Both processes stopped. Job state is safe in the database.'
}
