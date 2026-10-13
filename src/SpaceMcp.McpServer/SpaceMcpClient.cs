using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SpaceMcp.Protocol;

namespace SpaceMcp.McpServer
{
    public sealed class SpaceMcpClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;

        public SpaceMcpClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        private async Task<TResponse> ExchangeAsync<TResponse>(Request request, CancellationToken ct = default)
            where TResponse : Response
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_host, _port, ct);
                await using var stream = client.GetStream();
                await Bridge.SendAsync(stream, request, ct);
                var response = await Bridge.ReceiveAsync<Response>(stream, ct);
                if (response is TResponse typed) return typed;
                if (response is ErrorResponse err) throw new BridgeException(err.Message);
                throw new BridgeException("Unexpected response from bridge.");
            }
            catch (BridgeException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException ex)
            {
                throw new BridgeException(
                    $"Could not connect to the SpaceMCP plugin at {_host}:{_port} " +
                    $"({ex.SocketErrorCode}). Make sure Space Engineers is running, the SpaceMCP " +
                    "plugin is enabled in the loader, and a world is loaded before calling this tool.",
                    ex);
            }
            catch (Exception ex)
            {
                throw new BridgeException(
                    $"Lost connection to the SpaceMCP plugin at {_host}:{_port}: {ex.Message}",
                    ex);
            }
        }

        public Task<ListResponse> ListAsync(CancellationToken ct = default) => ExchangeAsync<ListResponse>(new ListRequest(), ct);

        public Task<ReadResponse> ReadAsync(long entityId, CancellationToken ct = default) => ExchangeAsync<ReadResponse>(new ReadRequest(entityId), ct);

        public Task<WriteResponse> WriteAsync(long entityId, string code, CancellationToken ct = default) => ExchangeAsync<WriteResponse>(new WriteRequest(entityId, code), ct);

        public Task<RecompileResponse> RecompileAsync(long entityId, CancellationToken ct = default) => ExchangeAsync<RecompileResponse>(new RecompileRequest(entityId), ct);

        public Task<RunResponse> RunAsync(long entityId, string? arg, CancellationToken ct = default) => ExchangeAsync<RunResponse>(new RunRequest(entityId, arg), ct);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Raised when the plugin is unreachable or returns an error. The message is
    /// written to be shown directly to an MCP client, so it names the host/port
    /// and the likely cause.
    /// </summary>
    public sealed class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }

        public BridgeException(string message, Exception inner) : base(message, inner) { }
    }
}
