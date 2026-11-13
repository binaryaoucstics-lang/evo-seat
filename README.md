# EVO Seat

[简体中文](README.zh-CN.md) · [Install for Codex](docs/codex.md) · [Agent workflow](AGENTS.md) · [Releases](https://github.com/binaryaoucstics-lang/evo-seat/releases)

EVO Seat gives Codex a hidden Windows desktop for operating and testing real GUI apps. An agent can launch an app, inspect its accessibility tree, send mouse and keyboard input, capture screenshots, and verify changes without taking over your foreground desktop.

- **Observe and act.** Use window IDs, accessibility snapshots, or seat-relative pixels. Pass `observeAfter: true` to `seat_element` to get a fresh snapshot after an intermediate action.
- **Share the seat.** Use an exclusive lease or choose matching 2, 4, or 12-region layouts for concurrent agents. The seat shares the Windows account and files.
- **Capture evidence.** Request a full-resolution PNG for small text or combine accessibility data and a screenshot when verifying a receipt.

For installation, give your Codex agent the [installation guide](docs/codex.md). For operation, give it the [agent workflow](AGENTS.md).

## Windows Paint comparison

Both runs used **`gpt-6.1-sol` with `high` reasoning effort**. Each started with a blank Windows Paint canvas and saved a 1000 × 410 PNG. EVO Seat used a hidden desktop. Built-in Computer Use used the foreground Paint window.

| Measured in Codex | EVO Seat | Built-in Computer Use |
| --- | ---: | ---: |
| Time from turn start to PNG save | 5m 24.4s | 4m 42.6s |
| Full turn, through the final reply | 5m 36.3s | 5m 03.8s |
| Total tokens, including cached input | 6,002,040 | 14,759,395 |
| Cached input tokens | 5,961,856 | 14,652,288 |
| Uncached input tokens | 33,205 | 102,239 |
| Output tokens, including reasoning | 6,979 | 4,868 |
| Tool calls issued by Codex | 44 | 62 |
| Desktop API calls | 306 | 114 |
| Drag strokes | 208 | 32 |
| Average time per stroke (through PNG save) | 1.56 s | 8.83 s |
| Average total tokens per stroke (including cached input) | 28,856 | 461,231 |
| Average uncached input plus output tokens per stroke | 193 | 3,347 |

![Bar charts comparing average time and token use per stroke for EVO Seat and built-in Computer Use](docs/images/per-stroke-comparison.en.png)

Times use the Codex turn log and PNG write timestamps. Token counts sum all model requests in each turn, including the conversation history sent with those requests. Codex tool calls include orchestration, such as waiting for an ongoing batch. Desktop API calls count individual `seat_*` MCP calls for EVO Seat and `sky` API calls for built-in Computer Use, including calls inside scripts. The per-stroke figures divide time through PNG save and full-turn token counts by the number of drags, so setup, screenshots, and other work are spread across the strokes.

Built-in Computer Use saved its PNG 41.8 seconds sooner and used about 2.46× as many total tokens. The EVO Seat run batched more short strokes, while the built-in run refreshed the window after each action and started with more conversation history. These are two observed runs with different stroke counts and context sizes, so the table describes those workflows.

**EVO Seat**

![Pelican drawn in Windows Paint using EVO Seat](docs/images/pelican.png)

**Built-in Computer Use**

![Pelican drawn in Windows Paint using built-in Computer Use](docs/images/computer-use-pelican.png)

## Build from source

Use Windows x64, the .NET 10 SDK, and Node.js 22.19 or later. From the repository root:

```powershell
npm ci --ignore-scripts
dotnet publish .\src\Anode\Anode.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o native
Copy-Item .\native\anode.exe .\native\evo-seat.exe
& .\native\evo-seat.exe selftest --quick | Out-Host
```

Then follow the setup and registration steps in the [Codex guide](docs/codex.md).

## License

[MIT](LICENSE). EVO Seat is based on [Anode](https://github.com/skulitom/Anode/tree/1ad6eae42c023654fd8de463a9d7a6e9ff787e1a) and incorporates some window containment work from BgDesk. See [third-party notices](THIRD-PARTY-NOTICES.md).
