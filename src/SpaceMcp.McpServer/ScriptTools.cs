using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using SpaceMcp.Protocol;

namespace SpaceMcp.McpServer
{
    [McpServerToolType]
    public sealed class ScriptTools
    {
        private readonly SpaceMcpClient _client;

        public ScriptTools(SpaceMcpClient client)
        {
            _client = client;
        }

        [McpServerTool, Description("List all programmable blocks in the running Space Engineers world. Returns each block's entityId, block name, grid name, running state, and current script length.")]
        public async Task<string> ListProgrammableBlocks(CancellationToken ct)
        {
            var r = await _client.ListAsync(ct);
            return JsonSerializer.Serialize(r.Blocks, new JsonSerializerOptions { WriteIndented = true });
        }

        [McpServerTool, Description("Read the full C# script source of a programmable block, identified by its entityId from list_programmable_blocks.")]
        public async Task<string> ReadScript(
            [Description("Entity id of the programmable block")] long entityId,
            CancellationToken ct)
        {
            var r = await _client.ReadAsync(entityId, ct);
            return r.Code;
        }

        [McpServerTool, Description("Write C# script source into a programmable block and trigger a recompile. Returns ok and the compiler detail (errors/warnings) so you can self-correct failed compiles.")]
        public async Task<string> WriteScript(
            [Description("Entity id of the programmable block")] long entityId,
            [Description("Full C# script source (the body of the Program class)")] string code,
            CancellationToken ct)
        {
            var r = await _client.WriteAsync(entityId, code, ct);
            return "ok=" + r.Ok + "\ndetail=" + r.Detail;
        }

        [McpServerTool, Description("Force a recompile of a programmable block's current script.")]
        public async Task<string> RecompileScript(
            [Description("Entity id of the programmable block")] long entityId,
            CancellationToken ct)
        {
            var r = await _client.RecompileAsync(entityId, ct);
            return "ok=" + r.Ok + "\ndetail=" + r.Detail;
        }

        [McpServerTool, Description("Run a programmable block, optionally passing a run argument string.")]
        public async Task<string> RunScript(
            [Description("Entity id of the programmable block")] long entityId,
            [Description("Optional run argument")] string? argument,
            CancellationToken ct)
        {
            var r = await _client.RunAsync(entityId, argument, ct);
            return "ok=" + r.Ok;
        }
    }
}
