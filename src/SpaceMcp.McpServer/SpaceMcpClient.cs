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
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, ct);
            await using var stream = client.GetStream();
            await Bridge.SendAsync(stream, request, ct);
            var response = await Bridge.ReceiveAsync<Response>(stream, ct);
            if (response is TResponse typed) return typed;
            if (response is ErrorResponse err) throw new InvalidOperationException(err.Message);
            throw new InvalidOperationException("Unexpected response from bridge.");
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
}
