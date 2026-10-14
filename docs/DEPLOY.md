# Deploying SpaceMCP

SpaceMCP has two halves that talk over a loopback TCP socket:

```
AI client (opencode / Claude)
      │  MCP stdio (JSON-RPC)
      ▼
SpaceMcp.McpServer          ← .NET 8 console app, started by the client
      │  TCP 127.0.0.1:4627 (length-prefixed JSON, see SpaceMcp.Protocol)
      ▼
SpaceMcp.Plugin             ← loaded inside SpaceEngineers.exe
```

Both halves must run on the same machine. Nothing is exposed off-loopback.

---

## Prerequisites

- **Space Engineers** (Steam app `244850`), with a plugin loader installed.
  - The endorsed **Plugin Loader** has been retired; its replacement is
    **Pulsar**. Which runtime a plugin needs — and where it is dropped — depends
    on the **Pulsar flavour**:
      - **Legacy** — SE1 on .NET Framework 4.8. Local plugins live in the
        game's `Bin64\Plugins\Local\`, and the **`net48`** build is used.
      - **Interim** — SE1 on .NET 10 via dotnet-compat (**CoreCLR**). Only
        .NET 8+ assemblies load (the `net48` build is skipped as `Host!`), and
        local plugins live under `<PulsarRoot>\Legacy\Local\` — **not** the
        game's `Bin64`. Use the **`net8.0`** build.
      - **Modern** — SE2 (not currently supported).
- **.NET SDK 8** — needed to build the plugin and to run the MCP server.
  Check with `dotnet --list-sdks`.
- Windows PowerShell 5.1 (built in) to run the deploy script.

> The plugin is a class library, **not** a standalone app — it is loaded into the
> game process, so it never needs `dotnet` at runtime. It multi-targets `net48`
> (classic loader / Pulsar-Legacy) and `net8.0` (Pulsar-Interim/CoreCLR); see
> "Deploying to Pulsar" below.

---

## 1. Build and deploy the plugin

The deploy script resolves the game folder (from `-GameDir`, then the
`SE_GAME_DIR` environment variable, then the Steam registry key), builds the
plugin, and copies a minimal file set into `Bin64\Plugins\Local`.

Stage only (safe, writes nothing into the game):

```powershell
.\scripts\deploy-plugin.ps1 -StageOnly
```

Build and install into the game:

```powershell
.\scripts\deploy-plugin.ps1
```

Useful switches:

| Switch | Effect |
| --- | --- |
| `-GameDir <path>` | Override game discovery (Bin64 folder or game root). |
| `-Configuration Debug` | Build the Debug flavour instead. |
| `-NoBuild` | Deploy the existing build output. |
| `-StageOnly` | Only populate `dist\plugin`; do not touch the game. |
| `-Port 5000` | Port written to a freshly created `SpaceMcp.json`. |
| `-Force` | Overwrite an existing `SpaceMcp.json`. |

**Close Space Engineers before deploying** — the game locks the DLLs.

### Deploying to Pulsar (Interim / CoreCLR)

The Interim flavour runs Space Engineers 1 on **.NET 10 (CoreCLR)**, so it loads
only .NET 8+ assemblies. Use the plugin's `net8.0` build, which is
**self-contained**: the wire protocol is compiled into it and `System.Text.Json`
comes from the .NET 10 shared framework, so nothing else has to travel with it.

```powershell
# 1. Build the net8 (Pulsar-Interim) flavour.
dotnet build src\SpaceMcp.Plugin\SpaceMcp.Plugin.csproj -c Release -f net8.0

# 2. Copy it into Pulsar's local-plugin folder as `plugin.dll`.
#    Interim resolves its plugin directory to <PulsarRoot>\Legacy\Local\.
$PulsarRoot = 'C:\path\to\Pulsar-v2.x.y-win-x64'
$dst = Join-Path $PulsarRoot 'Legacy\Local\SpaceMcp'
New-Item -ItemType Directory -Force -Path $dst | Out-Null
$bin = 'src\SpaceMcp.Plugin\bin\Release\net8.0'
Copy-Item (Join-Path $bin 'SpaceMcp.Plugin.dll') (Join-Path $dst 'plugin.dll') -Force
Copy-Item (Join-Path $bin 'SpaceMcp.Plugin.pdb') (Join-Path $dst 'plugin.pdb') -Force
```

Notes:

- A **folder** plugin must contain a file named exactly `plugin.dll` (or
  `<folderName>.dll`). Do **not** copy the game assemblies (`Sandbox.*`,
  `VRage.*`) next to it — the game already provides them, and stray DLLs there
  would be loaded as plugins themselves.
- Add `SpaceMcp.json` (`{ "port": 4627 }`) to the same folder.
- Enable it in `<PulsarRoot>\Legacy\Profiles\Current.xml`:

  ```xml
  <Profile xmlns:xsi="..." xmlns:xsd="...">
    <Name>Current</Name>
    ...
    <Local>
      <string>SpaceMcp.dll</string>
    </Local>
    ...
  </Profile>
  ```

  The id is `<folderName>.dll` — `SpaceMcp.dll` for the folder `SpaceMcp`
  (the loader reports it as `Enabled Plugins: SpaceMcp (SpaceMcp.dll)`).

- After launching, confirm in `<PulsarRoot>\Legacy\info.log`:

  ```
  Runtime: CoreCLR
  Enabled Plugins: SpaceMcp (SpaceMcp.dll), ...
  ```

**Close Space Engineers before copying** — the running game holds `plugin.dll`.

### What gets copied (and why so little)

The game's `Bin64` folder already ships every managed dependency the plugin
needs (`Sandbox.*`, `VRage.*`, `System.Memory`, `System.Buffers`,
`Microsoft.Bcl.AsyncInterfaces`, …). The script only copies:

- `SpaceMcp.Plugin.dll` — the plugin itself
- `SpaceMcp.Protocol.dll` — the shared wire protocol
- `System.Text.Json.dll` — **only if its version differs from the game's**

The last item exists because System.Text.Json is referenced at assembly version
`8.0.0.5` while the current game build ships `8.0.0.4` and configures **no
binding redirect** for it (`SpaceEngineers.exe.config` redirects several other
assemblies but not `System.Text.Json`). Shipping the exact referenced version
avoids a `FileLoadException`. Once the game ships `>= 8.0.0.5`, the script stops
copying it automatically.

> **Why the protocol also targets `net48`.** A `netstandard2.0` library
> references System.Text.Json at assembly version `8.0.0.0`, whereas a `net48`
> library references `8.0.0.5`. If `SpaceMcp.Protocol` were `netstandard2.0`
> only, the plugin would carry two different STJ references and the protocol's
> (`8.0.0.0`) would fail to resolve at runtime. `SpaceMcp.Protocol` therefore
> multi-targets `netstandard2.0;net48`; the plugin consumes the `net48` build so
> both halves reference `8.0.0.5` exactly. The MCP server (`net8.0`) uses the
> `netstandard2.0` build.

### Plugin configuration — `SpaceMcp.json`

Placed next to the plugin (`Bin64\Plugins\Local\SpaceMcp.json`):

```json
{
  "port": 4627
}
```

`port` is the only key. If the file is missing or unreadable, the plugin
defaults to `4627`. The MCP server and plugin must agree on the port.

> Environment note: `SE_GAME_DIR` (a user variable pointing at `Bin64`) is used
> both by `Directory.Build.props` and by the deploy script.

---

## 2. Register the MCP server

The server is a stdio MCP server. Point your client at the built DLL.

### opencode (V2)

The quickest path:

```powershell
opencode mcp add spacemcp -- dotnet "C:\Users\Chris\Documents\GitHub\spacemcp\src\SpaceMcp.McpServer\bin\Release\net8.0\SpaceMcp.McpServer.dll"
```

Or add it to `opencode.jsonc` by hand (see `docs/mcp.opencode.jsonc`):

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "spacemcp": {
        "type": "local",
        "command": [
          "dotnet",
          "C:\\Users\\Chris\\Documents\\GitHub\\spacemcp\\src\\SpaceMcp.McpServer\\bin\\Release\\net8.0\\SpaceMcp.McpServer.dll"
        ],
        "environment": {
          "SPACE_MCP_HOST": "127.0.0.1",
          "SPACE_MCP_PORT": "4627"
        }
      }
    }
  }
}
```

Note that V2 nests servers under `mcp.servers` and uses `disabled` (not
`enabled`). Verify with:

```powershell
opencode mcp list
```

### Claude Desktop

`%AppData%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "spacemcp": {
      "command": "dotnet",
      "args": [
        "C:\\Users\\Chris\\Documents\\GitHub\\spacemcp\\src\\SpaceMcp.McpServer\\bin\\Release\\net8.0\\SpaceMcp.McpServer.dll"
      ],
      "env": { "SPACE_MCP_HOST": "127.0.0.1", "SPACE_MCP_PORT": "4627" }
    }
  }
}
```

### Keep the MCP server build current

The client launches the DLL directly, so rebuild after changing server code:

```powershell
dotnet build src\SpaceMcp.McpServer\SpaceMcp.McpServer.csproj -c Release
```

---

## 3. Verify end to end

1. Start Space Engineers and enable the plugin in the plugin-loader menu (or, on
   Pulsar, add it to the profile as shown above).
2. Load a world that contains a programmable block.
3. In your AI client, call `list_programmable_blocks`. It should return the
   blocks in the world with their `entityId`, names, running state and script
   length.
4. Then try `read_script`, `write_script`, `recompile_script` and `run_script`
   with the returned `entityId`.

No game required, for testing the server alone: run `SpaceMcp.Simulator`, which
listens on `4628` and answers the same protocol with two seeded blocks.

---

## Troubleshooting

| Symptom | Likely cause / fix |
| --- | --- |
| Tool call hangs, then times out | The game isn't running, or no world is loaded, so the plugin never answers. Start SE and load a world. |
| `Connection refused` from a tool | Plugin isn't loaded or is bound to a different port. Check the plugin-loader log and `SpaceMcp.json`. |
| Plugin doesn't appear in the loader | Confirm the DLL is in `Bin64\Plugins\Local\` (not `%AppData%`), and that the loader is installed. |
| `Could not load file or assembly 'System.Text.Json, Version=8.0.0.5'` | `System.Text.Json.dll` wasn't copied next to the plugin. Re-run the deploy script without `-StageOnly`. |
| `Could not load file or assembly 'System.Text.Json, Version=8.0.0.0'` | The protocol was built for `netstandard2.0` only. It must multi-target `netstandard2.0;net48` (see "What gets copied") so the plugin consumes the `net48` build; then re-deploy. |
| Deploy copy fails / loops | Space Engineers is still running and holding the DLL. Close it and retry. |
| `Unknown entityId` | The block list is stale; call `list_programmable_blocks` again. |

Game logs live under `%AppData%\SpaceEngineers\` — check the plugin-loader log
there for load errors.
