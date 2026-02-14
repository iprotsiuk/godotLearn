# Setup (Godot 4.6 .NET + C# net8.0)

## Prerequisites (Exact)

1. Godot Editor: **Godot 4.6 .NET** build (Mono/.NET enabled editor).
2. Godot Export Templates: **4.6 .NET templates** matching the editor version.
3. .NET SDK: **8.0.x** (tested with `8.0.418`).
4. OS shell tools for scripts (`bash`) for the provided local run helpers.

## Verify .NET SDK

```bash
dotnet --version
```

Expected: `8.0.x` (tested: `8.0.418`).

## Open + Build

1. Open Godot 4.6 .NET editor.
2. Import/open this project folder.
3. Let Godot generate/refresh C# glue files.
4. Build C# once:

```bash
dotnet build NetRunnerSlice.csproj
```

## Run

- Manual: run from Godot editor, then choose Host/Join in menu.
- Scripted local multi-instance:

```bash
./Scripts/run_host.sh 7777
./Scripts/run_client.sh 127.0.0.1 7777 1
```

## Notes

- Physics tick is pinned to 60 in `project.godot`.
- Movement netcode is explicit packet-based; it does not use RPC/MultiplayerSynchronizer for movement replication.
- `Builds/`, `Logs/`, and `Tools/` are ignored by Godot import via `.gdignore`.
