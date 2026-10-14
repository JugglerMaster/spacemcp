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
        public async Task Check_reports_status_length_and_running()
        {
            var r = await _client.CheckAsync(123);
            Assert.Equal(123, r.EntityId);
            Assert.True(r.Ok);
            Assert.True(r.Length > 0);
            Assert.True(r.IsRunning);
            Assert.Contains("simulated", r.Detail);
        }

        [Fact]
        public async Task Check_passes_through_compile_error()
        {
            var bad = "public void Helper() { }";
            Assert.True((await _client.WriteAsync(456, bad)).Ok);

            var r = await _client.CheckAsync(456);
            Assert.False(r.Ok);
            Assert.Contains("Main method", r.Detail);
            Assert.Equal(bad.Length, r.Length);
        }

        [Fact]
        public async Task Check_unknown_entity_returns_error()
        {
            var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.CheckAsync(999));
            Assert.Equal("Unknown entityId", ex.Message);
        }

        [Fact]
        public async Task GetOutput_returns_seed_echo()
        {
            var r = await _client.OutputAsync(123);
            Assert.Contains("hello", r.Output);
        }

        [Fact]
        public async Task GetOutput_after_run_reflects_last_run()
        {
            Assert.True((await _client.RunAsync(456, null)).Ok);
            var r = await _client.OutputAsync(456);
            Assert.Contains("Echo output (simulated)", r.Output);
        }

        [Fact]
        public async Task GetOutput_unknown_entity_returns_error()
        {
            var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.OutputAsync(999));
            Assert.Equal("Unknown entityId", ex.Message);
        }

        [Fact]
        public async Task Write_returns_compile_detail()
        {
            var w = await _client.WriteAsync(123, "public void Main() { }");
            Assert.True(w.Ok);
            Assert.Contains("simulated", w.Detail);
        }

        [Fact]
        public async Task Run_returns_captured_output()
        {
            var r = await _client.RunAsync(123, null);
            Assert.True(r.Ok);
            Assert.Contains("Echo output (simulated)", r.Output);
        }

        [Fact]
        public async Task CustomData_roundtrip()
        {
            const string config = "target=Base\nmode=patrol";
            Assert.True((await _client.WriteCustomDataAsync(456, config)).Ok);

            var read = await _client.ReadCustomDataAsync(456);
            Assert.Equal(config, read.Data);
        }

        [Fact]
        public async Task CheckAll_reports_every_block()
        {
            var r = await _client.CheckAllAsync();
            Assert.Equal(2, r.Results.Count);
            Assert.Contains(r.Results, c => c.EntityId == 123);
            Assert.Contains(r.Results, c => c.EntityId == 456);
        }

        [Fact]
        public async Task Unknown_entity_returns_error()
        {
            var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.ReadAsync(999));
            Assert.Equal("Unknown entityId", ex.Message);
        }

        [Fact]
        public async Task Unreachable_plugin_gives_actionable_message()
        {
            // Port 1 has nothing listening, so the client cannot reach the plugin.
            using var client = new SpaceMcpClient("127.0.0.1", 1);
            var ex = await Assert.ThrowsAsync<BridgeException>(() => client.ListAsync());
            Assert.Contains("127.0.0.1:1", ex.Message);
            Assert.Contains("Space Engineers", ex.Message);
        }
    }
}
