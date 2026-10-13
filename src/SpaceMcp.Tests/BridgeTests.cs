using System;
using System.Threading.Tasks;
using SpaceMcp.McpServer;
using SpaceMcp.Simulator;
using Xunit;

namespace SpaceMcp.Tests
{
    public class BridgeTests : IDisposable
    {
        private readonly SimulatorListener _listener;
        private readonly SpaceMcpClient _client;

        public BridgeTests()
        {
            _listener = new SimulatorListener(4628);
            _listener.Start();
            _client = new SpaceMcpClient("127.0.0.1", 4628);
        }

        public void Dispose()
        {
            _listener.Dispose();
        }

        [Fact]
        public async Task List_returns_seed_blocks()
        {
            var r = await _client.ListAsync();
            Assert.Equal(2, r.Blocks.Count);
        }

        [Fact]
        public async Task Read_write_roundtrip()
        {
            var before = await _client.ReadAsync(123);
            Assert.Contains("Echo", before.Code);

            var code = "public void Main() { Echo(\"changed\"); }";
            var w = await _client.WriteAsync(123, code);
            Assert.True(w.Ok);

            var after = await _client.ReadAsync(123);
            Assert.Equal(code, after.Code);
        }

        [Fact]
        public async Task Recompile_and_run_succeed()
        {
            Assert.True((await _client.RecompileAsync(123)).Ok);
            Assert.True((await _client.RunAsync(456, "go")).Ok);
        }

        [Fact]
        public async Task Unknown_entity_returns_error()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _client.ReadAsync(999));
        }
    }
}
