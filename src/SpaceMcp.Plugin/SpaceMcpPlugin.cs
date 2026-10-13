using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Game;
using Sandbox.Game.Entities;
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
                    client = await _listener!.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
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
                await using var stream = client.GetStream();
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
            });
            return tcs.Task;
        }

        private Response HandleOnGameThread(Request request)
        {
            switch (request)
            {
                case ListRequest:
                    {
                        var list = new List<PbInfo>();
                        var blocks = new List<MyProgrammableBlock>();
                        MyEntities.GetEntitiesOfType(blocks);
                        foreach (var pb in blocks)
                        {
                            list.Add(new PbInfo(
                                pb.EntityId,
                                pb.CustomName,
                                pb.CubeGrid?.CustomName ?? "",
                                pb.IsRunning,
                                pb.ProgramData?.Length ?? 0));
                        }
                        return new ListResponse(list);
                    }

                case ReadRequest r:
                    {
                        var pb = Find(r.EntityId);
                        return pb is null ? new ErrorResponse("Unknown entityId") : new ReadResponse(r.EntityId, pb.ProgramData ?? "");
                    }

                case WriteRequest w:
                    {
                        var pb = Find(w.EntityId);
                        if (pb is null) return new ErrorResponse("Unknown entityId");
                        pb.ProgramData = w.Code;
                        return new WriteResponse(true, pb.DetailedInfo ?? "");
                    }

                case RecompileRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse("Unknown entityId");
                        pb.Recompile();
                        return new RecompileResponse(true, pb.DetailedInfo ?? "");
                    }

                case RunRequest r:
                    {
                        var pb = Find(r.EntityId);
                        if (pb is null) return new ErrorResponse("Unknown entityId");
                        pb.TryRun(r.Arg ?? "");
                        return new RunResponse(true);
                    }

                default:
                    return new ErrorResponse("Unknown command");
            }
        }

        private MyProgrammableBlock? Find(long entityId)
        {
            var blocks = new List<MyProgrammableBlock>();
            MyEntities.GetEntitiesOfType(blocks);
            foreach (var pb in blocks)
                if (pb.EntityId == entityId) return pb;
            return null;
        }
    }
}
