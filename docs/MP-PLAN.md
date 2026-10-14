# Multiplayer programmable-block flow — plan

Goal: make the tools behave, on a multiplayer client, the way they do for a
player at the keyboard — **write the script → save it to the server → press Run
→ read the result** — without bypassing any of the game's permission checks.

This file is the work-through-later plan. Nothing here is committed or
live-verified yet; items marked DONE are in the working tree.

## Where we are

| # | Item | State |
|---|------|-------|
| A | Restrict every tool to blocks owned by the local player | DONE (working tree; needs live check) |
| B | `run_script` uses the terminal Run button's call | DONE (working tree; needs live check) |
| C | Subscribe the client to server-pushed console output | DONE (working tree; MP-untested) |
| D | Async result handling (write/run/read on a client) | PLANNED |
| E | Multiplayer verification | PLANNED (needs a server + 2 clients) |
| F | Document multiplayer support level | PLANNED |

## Verified facts (from the shipped assemblies)

Read out of `Sandbox.Game.dll` / `Sandbox.Common.dll` by IL inspection. These
are the ground truth the design is built on.

**Authority.** Compiling and running are server-authoritative. A client's
`MyProgrammableBlock.Run(...)` raises `SendRunProgramRequest`; the server's
`RunProgramRequest` decompresses and runs the server's own copy of the script.
`Compile(...)` returns early unless `Sync.IsServer`.

**Writing is the editor's save.** `pb.ProgramData = code` on a client raises
`SendUpdateProgramRequest`. The server handler `UpdateProgram(byte[])` accepts
it when `MyEventContext.Current.IsLocallyInvoked() || MySession.Static.IsUserScripter(sender)`
**and** `program.Length <= 100000`; otherwise it calls
`MyMultiplayerServerBase.ValidationFailed(...)`. No GUI involvement — the code
editor is not needed to write.

**Running is the button.** The terminal's Run button
(`MyProgrammableBlock.TerminalRun`, `<>c::<CreateTerminalControls>b__36_6`) is:

```csharp
block.Run(block.TerminalRunArgument, UpdateType.Terminal)   // UpdateType.Terminal = 1
```

(The toolbar/action Run path passes `UpdateType.Trigger` = 2 — different, and
not what "clicked Run in the terminal" means.)

**Reading needs a subscription.** A client's `DetailedInfo` is written only by
`MyProgrammableBlock.WriteProgramResponseForSubscribers(string)` — a `[Client]`
event that clears `DetailedInfo`, appends the server's text and raises
properties changed. The server pushes to the endpoints in `m_subscribers`.

Subscription happens through the block's terminal page being open:

```
MyGuiControlGenericFunctionalBlock..ctor
  -> MyTerminalBlock.OnOpenedInTerminal(true)
  -> (event OpenedInTerminal)
  -> MyProgrammableBlock.OnOpenedInTerminal
  -> MyMultiplayer.RaiseEvent<MyProgrammableBlock,bool>(... SubscribeToProgramResponse, true, ...)
  -> server: m_subscribers.Add(sender)  then immediately push current DetailedInfo
```

`MyGuiControlGenericFunctionalBlock.OnRemoving` raises the same event with
`false`, i.e. closing the page unsubscribes.

Note it is the block's **terminal page**, not the code editor screen. Only four
systems subscribe to `OpenedInTerminal` (`MyProgrammableBlock`, `MyJumpDrive`,
`MyConveyorSorter`, `MyEntityCapacitorComponent`), each to its own block, so
raising it on a PB affects nothing else.

**Subscribing ourselves.** `MyTerminalBlock.OnOpenedInTerminal(bool)` is
**public** and does nothing but raise the event. So the plugin can subscribe the
local client with one call and no screen open — that is item C.

**Client detection.** `Sandbox.Game.Multiplayer.Sync.IsServer` (static) and
`MyAPIGateway.Multiplayer.IsServer` both exist.

## C — subscribe to server-pushed output (done)

`EnsureProgramOutputSubscription(MyProgrammableBlock)` in `SpaceMcpPlugin.cs`
raises `OnOpenedInTerminal(true)` **only when `Sync.IsServer` is false**:

- On a host / single-player the script runs in-process and `DetailedInfo` is
  already current, so the round-trip is skipped — and that also avoids
  `SubscribeToProgramResponse` adding the server's own (possibly invalid)
  endpoint to `m_subscribers` and trying to push to it.
- On a client it subscribes this endpoint, so server pushes land in the local
  `DetailedInfo` that `run_script`, `check_script` and `get_output` read.
- It is called per operation (write, recompile, run, check, check-all, output)
  rather than once, because a terminal page closing unsubscribes us.

## D — async result handling (planned)

The honest gap. On a client, write and run are *requests*: the plugin returns
before the server has acted, so the value returned by `write_script` /
`run_script` / `check_script` is best-effort and may be empty or stale. Options,
cheapest first:

- **D1 — document + poll.** Keep the immediate return as "submitted" and have
  the MCP client call `get_output` again after a moment. `run_script`'s
  description already says the multiplayer output may be stale or empty. No code
  change; relies on the caller. Probably enough for a first cut.
- **D2 — wait for the push.** Block for a bounded time until `DetailedInfo`
  changes. Must not block the game thread (the handler already runs inside
  `MySandboxGame.Static.Invoke`), so this means either a background wait plus a
  cached "last output" or a protocol change (`RunResponse.WaitMs`). Bigger
  change; only if D1 proves annoying.
- **D3 — detect rejection.** When the server rejects a write it calls
  `ValidationFailed`; the client is not told, so `write_script` currently reports
  `ok=true` optimistically. There is no public API to observe this. Either
  accept and document, or after a write re-read `ProgramData` and compare (a
  round-trip later) to infer whether it landed.
- **D4 — drop the extra `Recompile()`.** `write_script` assigns `ProgramData`
  and then calls `Recompile()`. Locally `ProgramData=` already recompiles; on a
  client the extra call races the update and can ask the server to compile the
  *old* source. Make it conditional or remove it once D1 is decided.

## E — verification (planned)

Single-player / host (the immediate next live test):

1. `list_programmable_blocks` returns only the owned PB (the scenario/NPC blocks
   are gone).
2. `check_script` on a blank PB → `ok=false`, "Main method not found".
3. `write_script` + `run_script` still produce the script's Echo.
4. `get_output` and the remaining tools unchanged.

Multiplayer (needs a dedicated server or two clients, and a scripter role where
relevant):

1. Client (scripter) against a dedicated server: `list` shows only owned blocks;
   `write_script` lands; `run_script` then `get_output` (with a short delay)
   returns the server's Echo — this is what item C buys.
2. Client *not* a scripter: `write_script` is rejected server-side. Confirm the
   failure mode is only "optimistic ok" and nothing worse (D3).
3. Confirm C does not disturb the editor lock: another client can still open the
   block's editor (we raise the event, we do not set `IsOpenedInTerminal` or
   touch `UserId`).

## F — document the support level (planned)

`docs/DEPLOY.md` / `ROADMAP.md` should state plainly: on a world you do not
control, the tools can only write/compile/run if the server grants you the
scripter role; where in-game scripts are disabled the tools error out (already
handled via `MySession.Static.EnableIngameScripts`); and on a client the results
are asynchronous.

## Open questions

- Does opening the code editor from the terminal page keep the terminal page
  (and therefore the vanilla subscription) alive, or does it unsubscribe us?
  Item C makes this moot for our own calls, but it decides what to tell the user
  about the manual path. Check live.
- Whether to keep the per-operation subscribe or coalesce it (minor; only the
  server's `m_subscribers` set and one extra push each time).
