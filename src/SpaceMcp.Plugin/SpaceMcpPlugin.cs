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
using Sandbox.ModAPI;
using SpaceMcp.Protocol;
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

        private Response HandleOnGameThread(Request request)
        {
            switch (request)
            {
                case ListRequest:
                    {
                        var list = new List<PbInfo>();
                        foreach (var block in EnumerateProgrammableBlocks())
                        {
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
                        return pb is null ? new ErrorResponse("Unknown entityId") : new ReadResponse(r.EntityId, ((IMyProgrammableBlock)pb).ProgramData ?? "");
                    }

                case WriteRequest w:
                    {
                        var pb = Find(w.EntityId);
                        if (pb is null) return new ErrorResponse("Unknown entityId");
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
                        if (pb is null) return new ErrorResponse("Unknown entityId");
                        ((IMyProgrammableBlock)pb).Recompile();
                        return new RecompileResponse(true, pb.DetailedInfo?.ToString() ?? "");
                    }

                case RunRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse("Unknown entityId");
                        var block = (IMyProgrammableBlock)pb;
                        block.TryRun(r.Arg ?? "");
                        // TryRun is synchronous on the game thread, so the Echo
                        // output is already in DetailedInfo by the time we read
                        // it. UNTESTED on real SE.
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
                        if (pb is null) return new ErrorResponse("Unknown entityId");
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
                        if (pb is null) return new ErrorResponse("Unknown entityId");
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
                        var output = ((IMyProgrammableBlock)pb).DetailedInfo?.ToString() ?? "";
                        return new OutputResponse(output);
                    }

                case ReadCustomDataRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse("Unknown entityId");
                        // UNTESTED: relies on IMyTerminalBlock.CustomData.
                        var data = ((IMyTerminalBlock)pb).CustomData ?? "";
                        return new ReadCustomDataResponse(data);
                    }

                case WriteCustomDataRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse("Unknown entityId");
                        // UNTESTED: relies on IMyTerminalBlock.CustomData.
                        ((IMyTerminalBlock)pb).CustomData = r.Data;
                        return new WriteCustomDataResponse(true);
                    }

                case CheckAllRequest:
                    {
                        var results = new List<CheckResponse>();
                        foreach (var block in EnumerateProgrammableBlocks())
                        {
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

        private static MyProgrammableBlock? Find(long entityId)
        {
            foreach (var block in EnumerateProgrammableBlocks())
                if (block.EntityId == entityId) return block;
            return null;
        }

        /// <summary>
        /// Heuristic for whether a recompile succeeded. UNTESTED against real
        /// SE: the public SE API does not expose a clean compile-success flag,
        /// so we treat the compiler output (DetailedInfo) as a failure when it
        /// mentions an error or exception. The raw output is always passed
        /// through via <see cref="CheckResponse.Detail"/>, so the model can read
        /// the exact message even when this heuristic is wrong.
        /// </summary>
        private static bool LooksCompiled(string detail)
        {
            return !detail.Contains("error", StringComparison.OrdinalIgnoreCase)
                && !detail.Contains("exception", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<MyProgrammableBlock> EnumerateProgrammableBlocks()
        {
            foreach (var entity in MyEntities.GetEntities())
                if (entity is MyProgrammableBlock pb) yield return pb;
        }
    }
}
