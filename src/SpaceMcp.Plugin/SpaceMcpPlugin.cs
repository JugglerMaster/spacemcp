using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceMcp.Protocol;
using VRage.Game;
using VRage.Plugins;

namespace SpaceMcp.Plugin
{
    public class SpaceMcpPlugin : IPlugin
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public void Init(object gameInstance)
        {
            var port = LoadPort();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = AcceptLoopAsync(_cts.Token);
        }

        public void Update()
        {
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _cts?.Dispose();
        }

        private int LoadPort()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(SpaceMcpPlugin).Assembly.Location);
                var file = Path.Combine(dir ?? ".", "SpaceMcp.json");
                if (File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("port", out var p)) return p.GetInt32();
                }
            }
            catch
            {
            }
            return 4627;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    break;
                }
                _ = HandleClientAsync(client, ct);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var stream = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    var request = await Bridge.ReceiveAsync<Request>(stream, ct);
                    if (request is null) break;
                    var response = await DispatchOnGameThreadAsync(request, ct);
                    await Bridge.SendAsync(stream, response, ct);
                }
            }
            catch
            {
            }
            finally
            {
                client.Dispose();
            }
        }

        private Task<Response> DispatchOnGameThreadAsync(Request request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<Response>();
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    tcs.TrySetResult(HandleOnGameThread(request));
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(new ErrorResponse(ex.Message));
                }
            }, "SpaceMcp");
            return tcs.Task;
        }

        private const string ScriptsDisabledMessage =
            "In-game scripts are disabled in this world, so the programmable block " +
            "cannot be compiled or run. Enable 'In-game scripts' in the world's " +
            "settings (Load Game > Edit Settings > Advanced > In-game scripts), " +
            "reload the world, then try again.";

        // Used wherever an id resolves to a block the local player does not own.
        // A loaded world (especially in multiplayer or a scenario) contains
        // programmable blocks on NPC, scenario and other players' grids; those
        // are deliberately out of scope for this tool.
        private const string NotOwnedMessage =
            "Unknown entityId, or it belongs to a block the local player does not own " +
            "(it is not on a grid you own or share with your faction). " +
            "Call list_programmable_blocks for the ids you can control.";

        /// <summary>
        /// Subscribes this client to the block's console output when this
        /// process is not the simulation authority.
        ///
        /// Compiling and running a programmable block are server-authoritative:
        /// on a multiplayer client the block's console text (DetailedInfo) is
        /// not produced locally, it is pushed out by the server. Vanilla only
        /// pushes to clients that have subscribed, which happens while that
        /// block's terminal page is open
        /// (MyGuiControlGenericFunctionalBlock -> MyTerminalBlock.OpenedInTerminal
        /// -> MyProgrammableBlock.OnOpenedInTerminal -> SubscribeToProgramResponse).
        /// Raising that same public event subscribes this endpoint without
        /// needing any screen open, so run/check/output can see the server's
        /// output.
        ///
        /// On a host or in single-player the script runs in-process and
        /// DetailedInfo is already current, so this is deliberately skipped:
        /// the round-trip would only address the server's own endpoint.
        /// </summary>
        private static void EnsureProgramOutputSubscription(MyProgrammableBlock block)
        {
            try
            {
                if (Sandbox.Game.Multiplayer.Sync.IsServer) return;
                (block as Sandbox.Game.Entities.Cube.MyTerminalBlock)?.OnOpenedInTerminal(true);
            }
            catch
            {
                // Best effort. Without the subscription the tool still works on
                // the host and in single-player, just not as a remote client.
            }
        }

        private Response HandleOnGameThread(Request request)
        {
            // When the world has in-game scripts turned off, compiling and
            // running silently do nothing. Surface that as a clear error up
            // front instead of reporting misleading success.
            if (RequiresScripts(request) && !ScriptsEnabled())
                return new ErrorResponse(ScriptsDisabledMessage);

            switch (request)
            {
                case ListRequest:
                    {
                        var list = new List<PbInfo>();
                        foreach (var block in EnumerateProgrammableBlocks())
                        {
                            if (!IsOwnedByLocalPlayer(block)) continue;
                            var pb = (IMyProgrammableBlock)block;
                            list.Add(new PbInfo(
                                block.EntityId,
                                block.CustomName?.ToString() ?? "",
                                block.CubeGrid?.DisplayName ?? "",
                                pb.IsRunning,
                                pb.ProgramData?.Length ?? 0));
                        }
                        return new ListResponse(list);
                    }

                case ReadRequest r:
                    {
                        var pb = Find(r.EntityId);
                        return pb is null ? new ErrorResponse(NotOwnedMessage) : new ReadResponse(r.EntityId, ((IMyProgrammableBlock)pb).ProgramData ?? "");
                    }

                case WriteRequest w:
                    {
                        var pb = Find(w.EntityId);
                        if (pb is null) return new ErrorResponse(NotOwnedMessage);
                        EnsureProgramOutputSubscription(pb);
                        var block = (IMyProgrammableBlock)pb;
                        block.ProgramData = w.Code;
                        // Recompile so the returned Detail reflects the newly
                        // written code (otherwise it would be the previous
                        // compile's stale output). UNTESTED on real SE.
                        block.Recompile();
                        return new WriteResponse(true, block.DetailedInfo?.ToString() ?? "");
                    }

                case RecompileRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse(NotOwnedMessage);
                        EnsureProgramOutputSubscription(pb);
                        ((IMyProgrammableBlock)pb).Recompile();
                        return new RecompileResponse(true, pb.DetailedInfo?.ToString() ?? "");
                    }

                case RunRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse(NotOwnedMessage);
                        EnsureProgramOutputSubscription(pb);
                        var block = (IMyProgrammableBlock)pb;
                        // Use the same call the terminal's "Run" button makes:
                        // MyProgrammableBlock.TerminalRun -> OnRunApplied ->
                        // Run(argument, UpdateType.Terminal). On the server/host
                        // this executes synchronously, so Echo is already in
                        // DetailedInfo. On a client it raises
                        // SendRunProgramRequest to the server, which runs the
                        // script asynchronously (and only if the server accepts
                        // the request).
                        block.Run(r.Arg ?? "", Sandbox.ModAPI.Ingame.UpdateType.Terminal);
                        var output = block.DetailedInfo?.ToString() ?? "";
                        return new RunResponse(true, output);
                    }

                // === The Check, Output, CustomData, and CheckAll behaviors below
                // === are UNTESTED against a real Space Engineers instance. The
                // === SE game assemblies are unavailable in this build
                // === environment, so they have only been exercised through the
                // === simulator. They rely on IMyProgrammableBlock.DetailedInfo
                // === (compile errors after Recompile, Echo output after a run)
                // === and IMyTerminalBlock.CustomData.
                case CheckRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse(NotOwnedMessage);
                        EnsureProgramOutputSubscription(pb);
                        var block = (IMyProgrammableBlock)pb;
                        block.Recompile();
                        var code = block.ProgramData ?? "";
                        var detail = block.DetailedInfo?.ToString() ?? "";
                        var ok = LooksCompiled(detail);
                        return new CheckResponse(r.EntityId, ok, detail, code.Length, block.IsRunning);
                    }

                case OutputRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse(NotOwnedMessage);
                        // Reads the script's Echo output (SE's DetailedInfo).
                        // BEHAVIOR (from the official SE wiki, UNTESTED in this
                        // plugin integration — only simulator-verified):
                        //  - Echo appends to a StringBuilder; each call capped
                        //    at 8000 chars.
                        //  - The buffer ACCUMULATES across Echo calls and across
                        //    runs; it is not cleared per run.
                        //  - It is reset by a Recompile (constructor re-runs)
                        //    and, in singleplayer, when the terminal is opened
                        //    (a game bug). No documented total cap.
                        // PRACTICAL NOTE: check_script() recompiles and so
                        // clears this buffer (replacing it with compile status).
                        // To capture a SINGLE run's output, recompile/check
                        // first, then run_script, then get_output immediately.
                        EnsureProgramOutputSubscription(pb);
                        var output = ((IMyProgrammableBlock)pb).DetailedInfo?.ToString() ?? "";
                        return new OutputResponse(output);
                    }

                case ReadCustomDataRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse(NotOwnedMessage);
                        // UNTESTED: relies on IMyTerminalBlock.CustomData.
                        var data = ((IMyTerminalBlock)pb).CustomData ?? "";
                        return new ReadCustomDataResponse(data);
                    }

                case WriteCustomDataRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse(NotOwnedMessage);
                        // UNTESTED: relies on IMyTerminalBlock.CustomData.
                        ((IMyTerminalBlock)pb).CustomData = r.Data;
                        return new WriteCustomDataResponse(true);
                    }

                case CheckAllRequest:
                    {
                        var results = new List<CheckResponse>();
                        foreach (var block in EnumerateProgrammableBlocks())
                        {
                            if (!IsOwnedByLocalPlayer(block)) continue;
                            EnsureProgramOutputSubscription(block);
                            var pb = (IMyProgrammableBlock)block;
                            pb.Recompile();
                            var code = pb.ProgramData ?? "";
                            var detail = pb.DetailedInfo?.ToString() ?? "";
                            results.Add(new CheckResponse(block.EntityId, LooksCompiled(detail), detail, code.Length, pb.IsRunning));
                        }
                        return new CheckAllResponse(results);
                    }

                default:
                    return new ErrorResponse("Unknown command");
            }
        }

        /// <summary>
        /// Resolves a programmable block by id, but only if the local player owns
        /// it. Every per-block tool goes through here, so an unowned block can
        /// neither be read nor modified even when its id is known.
        /// </summary>
        private static MyProgrammableBlock? Find(long entityId)
        {
            foreach (var block in EnumerateProgrammableBlocks())
                if (block.EntityId == entityId && IsOwnedByLocalPlayer(block)) return block;
            return null;
        }

        /// <summary>
        /// Whether the local player owns this block, directly or through a
        /// faction share.
        ///
        /// A loaded world contains programmable blocks on grids the local player
        /// has no business touching: NPC and scenario grids out of the box, and
        /// other players' grids in multiplayer. <see cref="EnumerateProgrammableBlocks"/>
        /// walks *every* grid in the entity set, so without this filter the tool
        /// would offer those blocks up for reading, rewriting and running.
        ///
        /// Ownership is taken from the grid's owner lists (the same data the
        /// game's terminal permissions use) and from
        /// <see cref="MyCubeBlock.GetUserRelationToOwner(long, MyRelationsBetweenPlayerAndBlock)"/>,
        /// which also resolves faction ownership and sharing. When there is no
        /// local player at all (a dedicated server with nobody connected) there
        /// is nothing to scope to, so ownership is treated as satisfied.
        /// </summary>
        private static bool IsOwnedByLocalPlayer(MyCubeBlock block)
        {
            long playerId;
            try
            {
                playerId = MySession.Static?.LocalPlayerId ?? 0;
            }
            catch
            {
                // Cannot read the session; do not silently hide everything.
                return true;
            }

            if (playerId == 0) return true;

            var grid = block.CubeGrid;
            if (grid is null) return false;

            if (grid.BigOwners.Contains(playerId) || grid.SmallOwners.Contains(playerId))
                return true;

            try
            {
                var relation = block.GetUserRelationToOwner(
                    playerId, MyRelationsBetweenPlayerAndBlock.NoOwnership);
                return relation == MyRelationsBetweenPlayerAndBlock.Owner
                    || relation == MyRelationsBetweenPlayerAndBlock.FactionShare;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Heuristic for whether a recompile succeeded. The public SE API does
        /// not expose a clean compile-success flag, so we treat the compiler
        /// output (DetailedInfo) as a failure when it mentions an error or
        /// exception, or the known empty-script message ("Main method not
        /// found"). The raw output is always passed through via
        /// <see cref="CheckResponse.Detail"/>, so the model can read the exact
        /// message even when this heuristic is wrong.
        /// </summary>
        private static bool LooksCompiled(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail)) return true;
            return !detail.Contains("error", StringComparison.OrdinalIgnoreCase)
                && !detail.Contains("exception", StringComparison.OrdinalIgnoreCase)
                && !detail.Contains("Main method not found", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for requests that compile or run a script.</summary>
        private static bool RequiresScripts(Request request) =>
            request is WriteRequest
                or RecompileRequest
                or RunRequest
                or CheckRequest
                or CheckAllRequest;

        /// <summary>
        /// Whether in-game scripting is enabled for the loaded world. Defaults
        /// to true when the session is not available so we never block on an
        /// unknown state.
        /// </summary>
        private static bool ScriptsEnabled()
        {
            try
            {
                return MySession.Static?.EnableIngameScripts ?? true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Enumerates all programmable blocks. Blocks are reachable both
        /// directly from <see cref="MyEntities"/> (when present) and from each
        /// grid's fat-block set. The grid walk is the reliable path: individual
        /// cube blocks are not guaranteed to appear in <see cref="MyEntities.GetEntities"/>.
        /// </summary>
        private static IEnumerable<MyProgrammableBlock> EnumerateProgrammableBlocks()
        {
            var seen = new HashSet<long>();
            foreach (var entity in MyEntities.GetEntities())
            {
                if (entity is MyProgrammableBlock direct)
                {
                    if (seen.Add(direct.EntityId)) yield return direct;
                }
                else if (entity is MyCubeGrid grid)
                {
                    // Fat blocks (MyCubeBlock) off the grid. Cube blocks are not
                    // part of MyEntities.GetEntities(); they belong to the grid.
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block is MyProgrammableBlock pb && seen.Add(pb.EntityId))
                            yield return pb;
                    }
                    // Belt-and-braces: some builds expose blocks only via the
                    // slim-block set, whose FatBlock is the terminal block.
                    foreach (var slim in grid.CubeBlocks)
                    {
                        if (slim.FatBlock is MyProgrammableBlock spb && seen.Add(spb.EntityId))
                            yield return spb;
                    }
                }
            }
        }
    }
}
