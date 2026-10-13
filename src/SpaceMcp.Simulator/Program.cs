using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SpaceMcp.Protocol;

namespace SpaceMcp.Simulator
{
    public sealed class SimulatorListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly SimulatorEngine _engine = new SimulatorEngine();
        private CancellationTokenSource? _cts;

        public int Port { get; }

        public SimulatorListener(int port)
        {
            Port = port;
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        public void Start()
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_cts.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
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
                    var response = _engine.Handle(request);
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

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener.Stop(); } catch { }
            _cts?.Dispose();
        }
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            var port = args.Length > 0 ? int.Parse(args[0]) : 4627;
            var listener = new SimulatorListener(port);
            listener.Start();
            Console.WriteLine("SpaceMcp simulator listening on 127.0.0.1:" + port);
            Console.WriteLine("Press ENTER to stop.");
            Console.ReadLine();
            listener.Dispose();
        }
    }
}
