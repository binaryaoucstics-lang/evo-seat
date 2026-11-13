# 在 Codex 中安装 EVO Seat

[English](codex.md) · [Agent 操作说明](../AGENTS.md)

在将要运行 Codex 和隐藏桌面的 Windows 主机上用 PowerShell 执行。需要 x64 Windows 10/11 专业版、企业版或教育版，Node.js 22.19 或更新版本，以及已登录的 Windows 桌面。使用 [Codex CLI](https://learn.chatgpt.com/docs/cli) 或 Codex 桌面应用注册。发布包自带 .NET 运行时。

## 1. 下载、校验和安装

下列命令将固定版本装入当前账户的版本目录。如已解压过该版本，请换一个新目录。

```powershell
$version = 'v0.4.0'
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

如果 `doctor` 显示需要配置远程桌面或子会话，请让机器所有者批准这项一次性配置。它会启用远程桌面主机和 Windows 子会话，必要时重启远程桌面服务，这可能断开现有的 RDP 连接。详见[上游的设置说明](https://github.com/skulitom/Anode/blob/1ad6eae42c023654fd8de463a9d7a6e9ff787e1a/docs/SECURITY.md#what-anode-setup-changes)。

```powershell
& $seat setup | Out-Host
& $seat doctor | Out-Host
```

## 2. 注册到 Codex

```powershell
$node = (Get-Command node.exe -ErrorAction Stop).Source
$entry = Join-Path $install 'mcp\seat-stdio.ts'
codex mcp add evo-seat -- $node $entry
codex mcp get evo-seat
```

如果要替换 Anode，确认 EVO Seat 注册成功后运行 `codex mcp remove anode`。也可在 Codex 桌面应用的 **设置 → MCP 服务器 → 添加服务器 → STDIO** 注册，名称填 `evo-seat`，命令填 `$node` 的绝对路径，添加一个参数，填 `$entry` 的绝对路径。保存并重启 Codex。参见 [Codex MCP 配置文档](https://learn.chatgpt.com/docs/extend/mcp)。

升级 EVO Seat 时，将现有 MCP 配置中的入口改成新的 `$entry`，然后重启 Codex。如果旧版桌面进程仍在运行，等任务结束后从 EVO Seat 托盘菜单退出，下次申请租约时会启动新版。

如需执行耗时较长的界面操作，可在 Codex `config.toml` 已有的 `[mcp_servers.evo-seat]` 表下加入 `tool_timeout_sec = 420`。

## 3. 在新的 Codex 对话中验证

> 使用 EVO Seat。先调用 `seat_status` 并报告 MCP 是否响应，再用默认参数申请桌面租约，检查 `seat_capabilities`，在隐藏桌面内截一张图，最后释放租约。

申请租约前，返回停止状态也说明连接成功。申请失败时根据工具报错和 `doctor` 的结果排查。后续按 [Agent 操作说明](../AGENTS.md) 操作应用。

## 4. 监看后台任务

申请新的桌面租约后，前台会自动打开只读实时窗口。独占桌面默认 1440 × 900。并发时，监看窗口会按需缩放以显示全部分区，每个分区都有编号和边框，使用中的分区更亮。点击「接管鼠标」后，可以用鼠标和键盘操作画面中的后台桌面；点击「归还控制」恢复只读。点击「隐藏画面」收起窗口，可在系统托盘选择「实时监看」重新打开。

并发时，让每个 Codex 对话分别申请租约，省略 `layerCount`。第三个任务加入时，监看窗口会新增一排。前两个任务保留各自的应用和坐标。

要关闭自动监看，请在 Codex 首次申请租约前执行下列命令。如果已有桌面进程在运行，请等任务结束并退出该进程后再执行。

```powershell
& $seat --channel proto-dual start --hidden --no-monitor | Out-Host
```
