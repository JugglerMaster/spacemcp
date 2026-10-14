# SpaceMCP — Roadmap (deferred work)

This file tracks work intentionally deferred from the initial "MCP server for AI" milestone.
The core milestone (in-game plugin bridge + MCP server) is the priority; these are future.

## Status
- **M1 (current):** `SpaceMcp.Plugin` (in-game TCP bridge) + `SpaceMcp.McpServer` (stdio MCP server). No UI.
- `SpaceMcp.Simulator` — dev-only in-memory bridge so the MCP server can be tested without the game.
- `SpaceMcp.Tests` — integration tests against the simulator.

## Deferred backlog

### Web UI (Monaco editor)
- ASP.NET Core minimal API reusing the same bridge protocol; static `wwwroot` with Monaco (`csharp` language).
- PB picker (block/grid name), Save → `write`, live compiler-error panel from `pb.DetailedInfo`, Run button, auto-save toggle.
- Could be hosted inside the same process as the MCP server or standalone. Decision: standalone `SpaceMcp.Web` project.

### In-editor IntelliSense (SE API)
- Stretch: Roslyn-based completion using MDK SE API stub assemblies. Heavy; MVP ships syntax highlighting only.

### Transport options
- Current: localhost TCP (`127.0.0.1:4627`). Alternatives to evaluate:
  - **Named pipe** (Windows-native, no port conflicts) — preferred if port clashes reported.
  - **File-watch hybrid** (ScriptDev style): plugin watches files under `IngameScripts/local`; external editor writes files. Simpler but not live for reads/recompile status.

### Programmable block identity
- Current: keyed by stable `EntityId`. Deferred: ScriptDev-style friendly names via block name `[Script Name]` for editor familiarity.

### Multi-environment
- Current: single-player / locally hosted only (`Sync.IsServer` true). The
  multiplayer *client* flow (owned-blocks filter, terminal-Run path, output
  subscription, async results) is in progress — plan: [MP-PLAN.md](MP-PLAN.md).
  Deferred: Torch server variant for dedicated/multiplayer admin.

### Misc
- Push notifications: plugin → clients when a PB script changes in-game (so editor/MCP can refresh).
- `Storage` / `CustomData` read-write tools.
- Batch write across multiple PBs.
- Config: port + host via `SpaceMcp.json` (plugin) and `SPACE_MCP_*` env (server). Add logging.

## Build/run (current)
1. Deploy the plugin with `.\scripts\deploy-plugin.ps1` — it builds `SpaceMcp.Plugin`
   and copies it into `<SpaceEngineers>\Bin64\Plugins\Local\` (the plugin-loader folder),
   alongside a `SpaceMcp.json` config. See [DEPLOY.md](DEPLOY.md).
2. Register `SpaceMcp.McpServer` as a stdio MCP server in the AI client (see
   `docs/mcp.opencode.jsonc` and [DEPLOY.md](DEPLOY.md)).
3. For dev without the game: run `SpaceMcp.Simulator` to emulate the bridge (port 4628).
