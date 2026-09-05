# Sharing the EPDesk skill

## What to send

`epdesk-files-skill.zip` — upload it in Claude as a Skill (Settings → Capabilities
→ Skills → Upload skill). The zip contains `epdesk-files/SKILL.md`.

The skill on its own does nothing. The recipient also has to add the connector,
which is why the setup steps are written into the bottom of the skill itself:

```
https://epdesk-mcp-production.up.railway.app/mcp
```

Settings → Connectors → Add custom connector → paste the URL → save. It takes no
credentials.

Custom connectors are not available on every Claude plan. If the recipient
cannot add one, the same URL works in Claude Desktop's config file or in Claude
Code — both are covered in the skill.

## What this hands over

The connector has **no authentication**. Whoever holds that URL can:

- list every file collected from every staff laptop, with original Windows paths
  and the employee usernames in them
- mint download links for any of it — invoices, purchase orders, client
  proposals, and personal documents belonging to named staff
- do this from any browser or client, with no login, no account, and no audit
  trail tying it back to them

Sending the URL is the same as sending the data. It cannot be un-sent, and there
is no way to revoke access for one person short of changing the endpoint for
everyone.

If this is going to someone outside the company, the safer shape is to put the
API key check back in front of the service first, so each person gets a
credential that can be revoked on its own. `EPDeskMcpServer/README.md` describes
that change under **Exposure**; it is small and self-contained.

## Keeping the skill current

`share/epdesk-files/SKILL.md` is a standalone copy for outside use — it has the
connector setup baked in and leaves out repo paths and Railway internals.

`.claude/skills/epdesk-files/SKILL.md` is the version this repository uses.

They will drift. When the tool surface changes, update both, then rebuild the
zip:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$dst = 'share\epdesk-files-skill.zip'
Remove-Item $dst -ErrorAction SilentlyContinue
$zip = [System.IO.Compression.ZipFile]::Open($dst, 'Create')
$entry = $zip.CreateEntry('epdesk-files/SKILL.md', 'Optimal')
$w = New-Object System.IO.StreamWriter($entry.Open())
$w.Write([System.IO.File]::ReadAllText((Resolve-Path 'share\epdesk-files\SKILL.md')))
$w.Dispose(); $zip.Dispose()
```

Use this rather than `Compress-Archive`: on Windows PowerShell 5.1 that cmdlet
writes `epdesk-files\SKILL.md` with a backslash, which is not a valid zip path
separator and can fail to unpack.
