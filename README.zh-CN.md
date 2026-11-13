# EVO Seat

[English](README.md) · [Codex 安装教程](docs/codex.zh-CN.md) · [Agent 操作说明](AGENTS.md) · [下载发行版](https://github.com/binaryaoucstics-lang/evo-seat/releases)

EVO Seat 为 Codex 提供一个隐藏的 Windows 桌面。Agent 可以在里面打开真实应用、读取控件、操作鼠标键盘、截图和验证结果，你可以继续使用前台桌面。

- **观察并操作。** 可以按窗口 ID、无障碍控件或桌面坐标操作。中间步骤给 `seat_element` 传入 `observeAfter: true`，即可在同一次调用里拿到操作后的新快照。
- **多 Agent 协作。** 支持独占租约，以及 2、4 或 12 个分区。各分区共享 Windows 账户和文件。
- **留存操作证据。** 看小字时可取原始尺寸 PNG，核对回执时可同时获取控件数据和截图。

下图由 Codex 调用 EVO Seat，在 Windows 画图里逐笔绘制，并保存为 1000 × 410 的 PNG。

![用 EVO Seat 在 Windows 画图中画出的鹈鹕](docs/images/pelican.png)

把[安装教程](docs/codex.zh-CN.md)交给 Codex 即可开始安装。安装后，让 Agent 按[操作说明](AGENTS.md)使用隐藏桌面。

## 从源码构建

需要 Windows x64、.NET 10 SDK 和 Node.js 22.19 或更新版本。在仓库根目录运行：

```powershell
npm ci --ignore-scripts
dotnet publish .\src\Anode\Anode.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o native
Copy-Item .\native\anode.exe .\native\evo-seat.exe
& .\native\evo-seat.exe selftest --quick | Out-Host
```

然后按[安装教程](docs/codex.zh-CN.md)完成系统配置和 MCP 注册。

## 许可

[MIT](LICENSE)。项目基于 [Anode](https://github.com/skulitom/Anode/tree/1ad6eae42c023654fd8de463a9d7a6e9ff787e1a)，并复用了 BgDesk 的部分窗口约束逻辑。详见[第三方声明](THIRD-PARTY-NOTICES.md)。
