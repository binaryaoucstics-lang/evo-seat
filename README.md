# EVO Seat

[简体中文](README.zh-CN.md) · [Install for Codex](docs/codex.md) · [Agent workflow](AGENTS.md) · [Releases](https://github.com/binaryaoucstics-lang/evo-seat/releases)

EVO Seat gives Codex a hidden Windows desktop for operating and testing real GUI apps. An agent can launch an app, inspect its accessibility tree, send mouse and keyboard input, capture screenshots, and verify changes without taking over your foreground desktop.

- **Observe and act.** Use window IDs, accessibility snapshots, or seat-relative pixels. Pass `observeAfter: true` to `seat_element` to get a fresh snapshot after an intermediate action.
- **Share the seat.** Use an exclusive lease or choose matching 2, 4, or 12-region layouts for concurrent agents. The seat shares the Windows account and files.
- **Capture evidence.** Request a full-resolution PNG for small text or combine accessibility data and a screenshot when verifying a receipt.

The pelican below was drawn stroke by stroke in Windows Paint through Codex and EVO Seat, then saved as a 1000 × 410 PNG.

![Pelican drawn in Windows Paint using EVO Seat](docs/images/pelican.png)

For installation, give your Codex agent the [installation guide](docs/codex.md). For operation, give it the [agent workflow](AGENTS.md).

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
