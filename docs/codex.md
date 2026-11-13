# Install EVO Seat for Codex

[简体中文](codex.zh-CN.md) · [Agent workflow](../AGENTS.md)

Run these steps in PowerShell on the Windows machine where Codex will use the desktop. Use x64 Windows 10/11 Pro, Enterprise, or Education, Node.js 22.19 or later, and a signed-in Windows desktop. Install the [Codex CLI](https://learn.chatgpt.com/docs/cli) for command-line registration, or use the Codex app settings below. The release contains the .NET runtime.

## 1. Download, verify, and install

This example installs the pinned release into a versioned folder under your user account. Use a new directory if you have already unpacked this version.

```powershell
$version = 'v0.2.0'
$download = "https://github.com/binaryaoucstics-lang/evo-seat/releases/download/$version"
$asset = 'evo-seat-windows-x64.zip'
$install = Join-Path $env:LOCALAPPDATA "Programs\EVOSeat\$version"
if (Test-Path -LiteralPath $install) { throw "Install directory already exists: $install" }
$temp = Join-Path ([IO.Path]::GetTempPath()) ("evo-seat-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
$zip = Join-Path $temp $asset
$sums = Join-Path $temp 'SHA256SUMS'
Invoke-WebRequest -UseBasicParsing "$download/$asset" -OutFile $zip
Invoke-WebRequest -UseBasicParsing "$download/SHA256SUMS" -OutFile $sums
$expected = (Get-Content -LiteralPath $sums -Raw).Trim().Split(' ')[0]
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) { throw 'EVO Seat checksum mismatch' }
Expand-Archive -LiteralPath $zip -DestinationPath $install
Push-Location -LiteralPath $install
try {
    npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'MCP dependencies could not be installed' }
} finally { Pop-Location }
$seat = Join-Path $install 'native\evo-seat.exe'
& $seat selftest --quick | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'EVO Seat self-test failed' }
& $seat doctor | Out-Host
```

If `doctor` reports that Remote Desktop or child sessions need setup, have the machine owner approve the one-time setup. It enables the Remote Desktop host and Windows child sessions. It can restart Remote Desktop Services and disconnect existing RDP sessions. See the [upstream setup details](https://github.com/skulitom/Anode/blob/1ad6eae42c023654fd8de463a9d7a6e9ff787e1a/docs/SECURITY.md#what-anode-setup-changes).

```powershell
& $seat setup | Out-Host
& $seat doctor | Out-Host
```

## 2. Register with Codex

```powershell
$node = (Get-Command node.exe -ErrorAction Stop).Source
$entry = Join-Path $install 'mcp\seat-stdio.ts'
codex mcp add evo-seat -- $node $entry
codex mcp get evo-seat
```

If replacing Anode, run `codex mcp remove anode` after verifying the EVO Seat entry. In Codex Desktop, the alternative is **Settings → MCP servers → Add server → STDIO**. Set the name to `evo-seat`, command to the absolute value of `$node`, and add the absolute value of `$entry` as one argument. Save and restart Codex. See [Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp).

When upgrading EVO Seat, point the existing MCP entry to the new `$entry` and restart Codex. If the previous desktop process is still running, let its tasks finish and quit it from the EVO Seat tray menu. The next lease will start the new version.

For long GUI calls, you can set `tool_timeout_sec = 420` under the existing `[mcp_servers.evo-seat]` table in your Codex `config.toml`.

## 3. Verify from a new Codex chat

> Use EVO Seat. Call `seat_status` first and report whether the MCP server responds. Then acquire an exclusive lease, check `seat_capabilities`, and capture one screenshot inside the seat. Release the lease when done.

A stopped status is a valid response before acquisition. If acquisition fails, use the error and `doctor` output to diagnose the host. Follow the [agent workflow](../AGENTS.md) for app operations.

## 4. Watch background tasks

A new desktop lease opens a read-only live window on the foreground desktop. The exclusive seat defaults to 1440 × 900. The window fits concurrent layouts to the display when needed. Use **隐藏画面** (Hide picture) to close the window and **实时监看** (Watch live) in the system tray to reopen it.

To disable automatic monitoring, use this command before Codex takes its first lease. If a seat process is already running, quit it after its tasks finish before running the command.

```powershell
& $seat --channel proto-dual start --hidden --no-monitor | Out-Host
```
