#!/bin/sh
set -eu

fail_config() {
    echo "Invalid ClamAV sandbox configuration: $1" >&2
    exit 64
}

validate_uint() {
    name="$1"
    value="$2"
    minimum="$3"
    maximum="$4"
    case "$value" in
        ''|*[!0-9]*) fail_config "$name must be an integer" ;;
    esac
    if [ "$value" -lt "$minimum" ] || [ "$value" -gt "$maximum" ]; then
        fail_config "$name is outside its permitted range"
    fi
}

validate_path() {
    name="$1"
    value="$2"
    maximum_length="$3"
    case "$value" in
        /*) ;;
        *) fail_config "$name must be an absolute container path" ;;
    esac
    case "$value" in
        /|*[!A-Za-z0-9_./-]*) fail_config "$name contains unsafe characters" ;;
    esac
    normalized="/${value#/}/"
    case "$normalized" in
        */../*|*/./*) fail_config "$name contains an unsafe path segment" ;;
    esac
    if [ "${#value}" -gt "$maximum_length" ]; then
        fail_config "$name is too long"
    fi
}

validate_version() {
    name="$1"
    value="$2"
    case "$value" in
        ''|*[!0-9.]*) fail_config "$name must contain only numeric version components" ;;
    esac
    if ! dpkg --validate-version "$value" >/dev/null 2>&1; then
        fail_config "$name is not a valid version"
    fi
}

max_input_bytes="${Sandbox__MaxInputBytes:-26214400}"
max_files="${Sandbox__MaxArchiveEntries:-2000}"
max_threads="${Sandbox__MaxConcurrentRequests:-1}"
scan_timeout_seconds="${Sandbox__MalwareScanTimeoutSeconds:-120}"
connect_timeout_seconds="${Sandbox__ClamDaemonConnectTimeoutSeconds:-5}"
signature_age_hours="${Sandbox__MaxClamSignatureAgeHours:-72}"
minimum_clam_version="${Sandbox__MinimumClamVersion:-1.4.5}"
database_path="${Sandbox__ClamDatabasePath:-/var/lib/clamav}"
socket_path="${Sandbox__ClamSocketPath:-/run/clamav/clamd.sock}"
temporary_path="${Sandbox__TempRoot:-/tmp/epdesk-extraction-sandbox}"

validate_uint "Sandbox__MaxInputBytes" "$max_input_bytes" 1 536870912
validate_uint "Sandbox__MaxArchiveEntries" "$max_files" 1 100000
validate_uint "Sandbox__MaxConcurrentRequests" "$max_threads" 1 8
validate_uint "Sandbox__MalwareScanTimeoutSeconds" "$scan_timeout_seconds" 1 900
validate_uint "Sandbox__ClamDaemonConnectTimeoutSeconds" "$connect_timeout_seconds" 1 30
validate_uint "Sandbox__MaxClamSignatureAgeHours" "$signature_age_hours" 1 720
validate_version "Sandbox__MinimumClamVersion" "$minimum_clam_version"
validate_path "Sandbox__ClamDatabasePath" "$database_path" 200
validate_path "Sandbox__ClamSocketPath" "$socket_path" 100
validate_path "Sandbox__TempRoot" "$temporary_path" 200
case "$database_path" in
    /var/lib/clamav|/var/lib/clamav/*) ;;
    *) fail_config "Sandbox__ClamDatabasePath must stay under /var/lib/clamav" ;;
esac
case "$socket_path" in
    /run/clamav/clamd.pid) fail_config "Sandbox__ClamSocketPath is reserved" ;;
    /run/clamav/*) ;;
    *) fail_config "Sandbox__ClamSocketPath must stay under /run/clamav" ;;
esac
case "$temporary_path" in
    /tmp/epdesk-clamd|/tmp/epdesk-clamd/*)
        fail_config "Sandbox__TempRoot overlaps the scanner's private temporary directory"
        ;;
    /tmp/*) ;;
    *) fail_config "Sandbox__TempRoot must stay under /tmp" ;;
esac

clamd_version_output="$(clamscan --version)"
installed_clam_version="${clamd_version_output#ClamAV }"
installed_clam_version="${installed_clam_version%%/*}"
validate_version "installed ClamAV version" "$installed_clam_version"
if ! dpkg --compare-versions \
    "$installed_clam_version" ge "$minimum_clam_version"; then
    fail_config "installed ClamAV $installed_clam_version is below $minimum_clam_version"
fi
echo "ClamAV package version: $installed_clam_version"

scan_timeout_milliseconds=$((scan_timeout_seconds * 1000))
clamd_threads=$((max_threads + 1))
max_queue=$((clamd_threads * 2 + 2))
clamd_config="/etc/clamav/epdesk-clamd.conf"
freshclam_config="/etc/clamav/epdesk-freshclam.conf"
clamd_temporary_path="/tmp/epdesk-clamd"
clamd_pid_file="/run/clamav/clamd.pid"
socket_directory="$(dirname "$socket_path")"

install -d -o clamav -g clamav -m 0750 \
    "$database_path" "$clamd_temporary_path"
install -d -o clamav -g epdesk-scan -m 0750 "$socket_directory"
install -d -o app -g app -m 0700 "$temporary_path"
chown -R clamav:clamav "$database_path"
rm -f "$socket_path" "$clamd_pid_file"
umask 077

cat > "$clamd_config" <<EOF
Foreground yes
DatabaseDirectory $database_path
OfficialDatabaseOnly yes
LocalSocket $socket_path
LocalSocketGroup epdesk-scan
LocalSocketMode 0660
FixStaleSocket yes
PidFile $clamd_pid_file
TemporaryDirectory $clamd_temporary_path
StreamMaxLength $max_input_bytes
MaxFileSize $max_input_bytes
MaxScanSize $max_input_bytes
MaxScanTime $scan_timeout_milliseconds
MaxFiles $max_files
MaxRecursion 17
MaxThreads $clamd_threads
MaxQueue $max_queue
ReadTimeout $scan_timeout_seconds
CommandReadTimeout $connect_timeout_seconds
AlertExceedsMax yes
SelfCheck 300
ConcurrentDatabaseReload yes
BytecodeUnsigned no
AllowAllMatchScan no
ExitOnOOM yes
EOF
chown root:clamav "$clamd_config"
chmod 0640 "$clamd_config"

cat > "$freshclam_config" <<EOF
DatabaseDirectory $database_path
DatabaseMirror database.clamav.net
DatabaseOwner clamav
Checks 24
ConnectTimeout 30
ReceiveTimeout 120
EOF
chown root:clamav "$freshclam_config"
chmod 0640 "$freshclam_config"

bootstrap_pid=""
clamd_pid=""
freshclam_pid=""
app_pid=""

run_as_clamav() {
    exec setpriv \
        --reuid=clamav \
        --regid=clamav \
        --init-groups \
        --no-new-privs \
        env -i \
        HOME=/var/lib/clamav \
        PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        "$@"
}

run_as_app() {
    exec setpriv \
        --reuid=app \
        --regid=app \
        --init-groups \
        --no-new-privs \
        env HOME=/tmp "$@"
}

stop_children() {
    trap - TERM INT

    for child_pid in "$app_pid" "$freshclam_pid" "$clamd_pid" "$bootstrap_pid"; do
        if [ -n "$child_pid" ] && kill -0 "$child_pid" 2>/dev/null; then
            kill -TERM "$child_pid" 2>/dev/null || true
        fi
    done

    grace_seconds=20
    while [ "$grace_seconds" -gt 0 ]; do
        children_alive=0
        for child_pid in "$app_pid" "$freshclam_pid" "$clamd_pid" "$bootstrap_pid"; do
            if [ -n "$child_pid" ] && kill -0 "$child_pid" 2>/dev/null; then
                children_alive=1
            fi
        done
        [ "$children_alive" -eq 0 ] && break
        grace_seconds=$((grace_seconds - 1))
        sleep 1
    done

    for child_pid in "$app_pid" "$freshclam_pid" "$clamd_pid" "$bootstrap_pid"; do
        if [ -n "$child_pid" ] && kill -0 "$child_pid" 2>/dev/null; then
            kill -KILL "$child_pid" 2>/dev/null || true
        fi
        if [ -n "$child_pid" ]; then
            wait "$child_pid" 2>/dev/null || true
        fi
    done
}

trap 'stop_children; exit 143' TERM
trap 'stop_children; exit 130' INT

# Bound the cold-start update so Railway still gets an HTTP process within its
# startup window. Existing databases are acceptable only when clamd loads them
# and its VERSION timestamp passes the application's freshness check.
run_as_clamav timeout --signal=TERM --kill-after=10s 240s \
    freshclam --config-file="$freshclam_config" --stdout &
bootstrap_pid=$!
if wait "$bootstrap_pid"; then
    echo "Initial ClamAV signature update completed."
else
    echo "Initial ClamAV signature update failed or timed out; readiness will fail closed unless a fresh database already exists." >&2
fi
bootstrap_pid=""

run_as_clamav clamd \
    --foreground \
    --config-file="$clamd_config" &
clamd_pid=$!

# Notify the long-lived daemon after each successful database update.
run_as_clamav freshclam \
    --config-file="$freshclam_config" \
    --daemon \
    --foreground \
    --stdout \
    --daemon-notify="$clamd_config" &
freshclam_pid=$!

run_as_app dotnet EPDeskExtractionSandbox.dll &
app_pid=$!

exit_status=1
while :; do
    if ! kill -0 "$app_pid" 2>/dev/null; then
        if wait "$app_pid"; then
            exit_status=0
        else
            exit_status=$?
        fi
        break
    fi
    if ! kill -0 "$clamd_pid" 2>/dev/null; then
        echo "ClamAV daemon exited; stopping the sandbox so Railway can restart it." >&2
        break
    fi
    if ! kill -0 "$freshclam_pid" 2>/dev/null; then
        echo "FreshClam daemon exited; stopping the sandbox so Railway can restart it." >&2
        break
    fi
    sleep 1
done

stop_children
exit "$exit_status"
