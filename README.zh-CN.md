# EVO Seat

[English](README.md) · [Codex 安装教程](docs/codex.zh-CN.md) · [Agent 操作说明](AGENTS.md) · [下载发行版](https://github.com/binaryaoucstics-lang/evo-seat/releases)

EVO Seat 为 Codex 提供一个隐藏的 Windows 桌面。Agent 可以在里面打开真实应用、读取控件、操作鼠标键盘、截图和验证结果，你可以继续使用前台桌面。

- **观察并操作。** 可以按窗口 ID、无障碍控件或桌面坐标操作。中间步骤给 `seat_element` 传入 `observeAfter: true`，即可在同一次调用里拿到操作后的新快照。
- **多 Agent 协作。** 支持独占租约，以及 2、4 或 12 个分区。各分区共享 Windows 账户和文件。
- **留存操作证据。** 看小字时可取原始尺寸 PNG，核对回执时可同时获取控件数据和截图。

把[安装教程](docs/codex.zh-CN.md)交给 Codex 即可开始安装。安装后，让 Agent 按[操作说明](AGENTS.md)使用隐藏桌面。

## Windows 画图实测对比

两次都使用 **`gpt-6.1-sol`，推理强度为 `high`**，从空白画布开始绘制鹈鹕，保存为 1000 × 410 的 PNG。EVO Seat 使用隐藏桌面，内置 Computer Use 使用前台的画图窗口。

| 实测指标 | EVO Seat | 内置 Computer Use |
| --- | ---: | ---: |
| 从回合开始到保存 PNG | 5 分 24.4 秒 | 4 分 42.6 秒 |
| 完整回合耗时（到最终回复） | 5 分 36.3 秒 | 5 分 03.8 秒 |
| 总 Token（含缓存输入） | 6,002,040 | 14,759,395 |
| 缓存输入 Token | 5,961,856 | 14,652,288 |
| 未缓存输入 Token | 33,205 | 102,239 |
| 输出 Token（含推理） | 6,979 | 4,868 |
| Codex 发出的工具调用 | 44 | 62 |
| 桌面 API 调用 | 306 | 114 |
| 拖动画笔次数 | 208 | 32 |
| 平均每笔耗时（按保存 PNG 前的总时间摊算） | 1.56 秒 | 8.83 秒 |
| 平均每笔总 Token（含缓存输入） | 28,856 | 461,231 |
| 平均每笔未缓存输入与输出 Token 之和 | 193 | 3,347 |

时间取自 Codex 回合日志和 PNG 写入时间。Token 汇总各自回合内的所有模型请求，包含随请求发送的对话历史。Codex 工具调用包含等待批处理等调度调用。桌面 API 调用按实际执行次数计算，包括脚本内部的 EVO Seat `seat_*` MCP 调用和内置 Computer Use 的 `sky` API 调用。每笔平均值用保存 PNG 前的总时间、整轮 Token 分别除以拖动次数，启动、截图等开销也摊入每笔。

本次内置 Computer Use 提前 41.8 秒保存 PNG，总 Token 约为 EVO Seat 的 2.46 倍。EVO Seat 将更多短笔画分批执行，内置版每次操作后刷新窗口，且开始时已有更长的对话历史。表格记录的是这两次实际流程，笔画数量和上下文长度并未统一。

**EVO Seat**

![用 EVO Seat 在 Windows 画图中画出的鹈鹕](docs/images/pelican.png)

**内置 Computer Use**

![用内置 Computer Use 在 Windows 画图中画出的鹈鹕](docs/images/computer-use-pelican.png)

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
