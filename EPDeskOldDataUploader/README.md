# Old User Data Uploader

A throwaway desktop tool for the one-time push of an archived `Users Data`
folder into EPDesk. Point it at the drive, press two buttons, leave it running.

It talks to the deployed API over HTTPS and nothing else. There is no database
connection, no Railway tunnel, and no Backblaze credential on the machine
running it — the API hands back short-lived upload URLs for each part. That is
the whole reason this exists next to the server-side importer in
[`OLD_USER_DATA_IMPORT_API.md`](../EPDeskServerApi/OLD_USER_DATA_IMPORT_API.md),
which only works when the API host itself can read the drive.

## The folder it expects

The folder you pick is the one that holds a folder per person:

```text
E:\Users Data\            <- pick this
  abhay\
    Desktop\proposal.docx
    Documents\2019\rate card.xlsx
  mann\
    Downloads\invoice.pdf
```

Everything below a person's folder belongs to them however deeply it is nested.
A file lying loose in the root, owned by nobody, is filed under `_ROOT`.

### One person at a time

A large archive does not have to go up in a single run. Pick one person's folder
and tick **This folder is one person**:

```text
E:\Users Data\Niraj\      <- pick this, tick the box
  Desktop\...
  Download\...
```

Without that box ticked the app would read `Desktop` and `Download` as two
different people and file them under devices called `DESKTOP` and `DOWNLOAD`.

With it ticked, the selected folder *is* the person, and the run produces
exactly the same device code and the same stored paths as scanning the whole
root would have — so an archive can be split across as many runs as you like
without anything landing in two places. Keep the prefix the same across those
runs and the results are indistinguishable from one big upload.

## Installing it

```powershell
.\EPDeskOldDataUploader\Installer\build-msi.ps1
```

That produces `bin\msi\EPDeskOldDataUploader-<version>-x64.msi`, about 51 MB.
The .NET runtime is inside it, so the MSI installs on a machine with nothing
else on it — which matters when the archive drive is plugged into someone
else's desktop rather than a dev box.

```powershell
msiexec /i "EPDeskOldDataUploader-1.3.0-x64.msi"        # wizard
msiexec /i "EPDeskOldDataUploader-1.3.0-x64.msi" /qn    # silent, needs elevation
msiexec /x "EPDeskOldDataUploader-1.3.0-x64.msi"        # uninstall
```

It installs per-machine to `C:\Program Files\EPDesk Old User Data Uploader`
with a Start Menu shortcut under **EPDesk**, and needs administrator rights.
Installing a newer build replaces the older one in place rather than stacking
up in Add/Remove Programs.

Pass `-FrameworkDependent` for a 0.6 MB MSI instead. That one needs the .NET
Desktop Runtime 8 already installed on the target machine, and the app will
refuse to start without it — only worth it when you know the machine has it.

Building the MSI needs the WiX 4 CLI:

```powershell
dotnet tool install --global wix --version 4.0.5
wix extension add -g WixToolset.UI.wixext/4.0.5
```

## Running it

From an install, use the **EPDesk Old User Data Uploader** Start Menu shortcut.
To run it straight from the repo without installing:

```powershell
dotnet build EPDeskOldDataUploader/EPDeskOldDataUploader.csproj -c Release
.\EPDeskOldDataUploader\bin\Release\net8.0-windows\EPDeskOldDataUploader.exe
```

1. **Users Data folder** — Browse to the archive root.
2. **API address** — `https://laptop-data.excellentpublicity.co` by default.
3. **API key** — the agent file-upload key. Type it once; it is remembered under
   DPAPI for your Windows account, so it is never written to disk in clear text.
   It is also picked up automatically from a `Security__AgentFileUploadApiKey`
   environment variable, or from a `.env` beside the executable or above it —
   which fills it in for free when running from the repo, but not from an
   installed copy in Program Files.
4. **Scan folder** — reads the server's upload policy, then walks the drive and
   lists what it found per person.
5. **Tick the people you want**, then **Upload selected**.

## Choosing who goes up

The scan lists every person it found. Nothing is ticked to begin with, so a
900 GB archive cannot start by accident — tick the folders you want and the
status line totals them before you commit:

```text
[x] Abhay Pandey    OLD-ABHAY    2,753 files   13.39 GB
[ ] Account-Jenisha OLD-ACCOUNT  15,826 files  89.75 GB
[x] Niraj           OLD-NIRAJ    1,760 files   47.91 GB

Selected 2 of 40 folders: 4,513 files, 61.30 GB to upload.
```

**Upload selected** asks for confirmation, listing the folders and the device
code each will land under, then uploads only those. **Select all** and
**Select none** are there for the common cases.

### Last run

Each folder carries what the previous run left behind, so working through forty
people over several days needs no list kept on paper:

| Shown | Meaning |
| --- | --- |
| `Done - 22 Aug` | Everything went up, nothing outstanding. Row is greyed. |
| `Done, 27 skipped - 22 Aug` | Finished, but some files were empty or over the size limit. |
| `12 new since 22 Aug` | Files have been added to that folder since the last run. |
| `Stopped part way - 22 Aug` | The run was stopped or the app was closed. |
| `3 failed - 22 Aug` | Files that could not be uploaded. Shown in red. |

This is a note of what **this machine** did, kept in
`%LOCALAPPDATA%\EPDeskOldDataUploader\folder-history.json`. It is not proof the
server holds the files. Re-running a folder marked done costs almost nothing —
the server recognises each file and sends no bytes.

Settings live in `%LOCALAPPDATA%\EPDeskOldDataUploader\settings.json`.

## What it uploads

The scan only lists extensions the server's upload policy currently allows, so
the tool can never offer a file the API would reject. Two things are listed but
deliberately not sent, and appear under **Skipped and failed**:

- empty files
- files above the policy's per-file size limit

Everything else is uploaded as a resumable multipart upload and queued for text
extraction by the server, exactly like a file the desktop agent collects.

## Where files land in Backblaze

Two layouts, chosen by the **Readable folders in storage** tick box.

**Unticked (default, works today).** Files go through the same endpoints the
desktop agent uses, and the object key is hashed:

```text
devices/OLD-NIRAJ/<sha256 of the windows path>/<sha256 of the revision>/proposal.docx
```

Nothing below the device code is browsable. The real Windows path is kept in
Postgres, so files are found through the EPDesk tools rather than the Backblaze
console. Records are tagged source `automatic_upload`.

**Ticked.** Files go through `/api/agent/old-user-data/*` and land under the
same readable layout the server-side importer uses:

```text
uploads/old-user-data/Niraj/Desktop/proposal.docx
```

Records are tagged source `old_user_data`, alongside anything the server-side
importer brought in. Path segments are sanitised and `..` is dropped, so a file
can never be written outside its person's folder.

> **This tick box needs a server build that has
> `AgentOldUserDataUploadsController`.** Against an older deployment the run
> stops before uploading anything and says so. Until the API is deployed, leave
> it unticked.

### Deploying the server side

The endpoints are new and isolated — nothing the desktop agents call was
touched, and no database migration is needed, because the `old_user_data_files`
and `old_user_data_import_jobs` tables already exist.

1. Deploy `EPDeskServerApi` as usual.
2. Nothing to configure. `OldUserDataImport:ObjectKeyPrefix` is read if set and
   defaults to `uploads/old-user-data`; `OldUserDataImport:Enabled` is **not**
   required, as it only gates the server-side scanning worker.
3. Tick the box in the uploader and run one small folder to confirm.

Jobs created this way are marked `client_push` so the server-side import worker
never tries to scan a drive it cannot see.

## Device codes

Each person's folder becomes a device code: the prefix plus the folder name,
upper cased, with anything outside `A-Z 0-9 - _ .` replaced by `_`. A `-` is
inserted if the prefix does not already end in a separator.

```text
abhay              ->  OLD-ABHAY
Priya Shah (Sales) ->  OLD-PRIYA_SHAH__SALES_
```

Editing the prefix after a scan re-labels what was found rather than throwing
the scan away — worth knowing when the scan took several minutes. Changing the
folder or the one-person setting does require scanning again, because those
decide how each file was filed in the first place.

The `OLD-` prefix is what keeps these apart from real machines in
`epdesk_list_devices`. Clear the prefix box if you want the bare folder name,
but then an archive folder named after a laptop will merge with that laptop's
live uploads.

## Stopping, resuming, retrying

Safe to stop at any point. **Pause** lets the files in flight finish and holds
there; **Stop** cancels the run.

Restarting costs nothing: scan the same folder again and every file already
stored comes back as *already on the server* without a second upload, because
the API recognises the path, size, timestamp, and checksum. A part-finished
large file resumes from the parts Backblaze already holds rather than from zero.

After a run with failures, press **Start upload** again — it picks up the failed
files and leaves the finished ones alone.

## Options worth knowing

| Option | Default | What it does |
| --- | --- | --- |
| Files at once | 4 | Files uploaded in parallel. Raise it on a fast link; uploads are latency bound, not bandwidth bound. |
| Retries | 3 | Attempts per file, with a widening delay. A file the server rejects outright is skipped rather than retried. |
| Checksum every file | on | Computes SHA-256 before uploading. Costs an extra read of every file; in exchange the server can tell a moved file from a changed one and skip re-uploads. |

## When you are done

Nothing to tear down — the tool holds no server-side job state. It can be
deleted from the solution once the archive is in.
