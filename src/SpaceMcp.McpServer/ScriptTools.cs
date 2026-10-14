using System;
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
            return await SafeCall(async () =>
            {
                var r = await _client.ListAsync(ct);
                return JsonSerializer.Serialize(r.Blocks, new JsonSerializerOptions { WriteIndented = true });
            });
        }

        [McpServerTool, Description("Read the full C# script source of a programmable block, identified by its entityId from list_programmable_blocks.")]
        public async Task<string> ReadScript(
            [Description("Entity id of the programmable block")] long entityId,
            CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.ReadAsync(entityId, ct);
                return r.Code;
            });
        }

        [McpServerTool, Description("Write C# script source into a programmable block and trigger a recompile. Returns ok and the compiler detail (errors/warnings) so you can self-correct failed compiles.")]
        public async Task<string> WriteScript(
            [Description("Entity id of the programmable block")] long entityId,
            [Description("Full C# script source (the body of the Program class)")] string code,
            CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.WriteAsync(entityId, code, ct);
                return "ok=" + r.Ok + "\ndetail=" + r.Detail;
            });
        }

        [McpServerTool, Description("Force a recompile of a programmable block's current script.")]
        public async Task<string> RecompileScript(
            [Description("Entity id of the programmable block")] long entityId,
            CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.RecompileAsync(entityId, ct);
                return "ok=" + r.Ok + "\ndetail=" + r.Detail;
            });
        }

        [McpServerTool, Description("Check the code status of a programmable block. Forces a recompile and reports whether it compiled (ok), the compiler output with any errors/warnings (detail, passed through verbatim), the current script length in characters (length), and whether the block is running. Use this after writing or editing code to confirm the script is healthy.")]
        public async Task<string> CheckScript(
            [Description("Entity id of the programmable block")] long entityId,
            CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.CheckAsync(entityId, ct);
                return "ok=" + r.Ok
                    + "\nlength=" + r.Length
                    + "\nrunning=" + r.IsRunning
                    + "\ndetail=" + r.Detail;
            });
        }

        [McpServerTool, Description("Read the current console output (Echo text) of a programmable block. This reads the block's DetailedInfo WITHOUT recompiling, so prior output is preserved. SE behavior (documented, but untested in this integration): Echo output ACCUMULATES across runs and is NOT cleared each run; each Echo call is capped at 8000 chars; the buffer is reset only by a recompile or by opening the terminal in singleplayer. So this may return output from MULTIPLE runs since the last recompile. To capture a SINGLE run's output, call check_script (or recompile) first to reset the buffer, then run_script, then get_output immediately — because check_script itself recompiles and would wipe run output.")]
        public async Task<string> GetOutput(
            [Description("Entity id of the programmable block")] long entityId,
            CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.OutputAsync(entityId, ct);
                return "output=" + r.Output;
            });
        }

        [McpServerTool, Description("Read the CustomData string of a programmable block. In-game scripts read Me.CustomData for configuration, so this is how you inspect a block's current config (target names, toggles, settings) without editing its code.")]
        public async Task<string> ReadCustomData(
            [Description("Entity id of the programmable block")] long entityId,
            CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.ReadCustomDataAsync(entityId, ct);
                return "customData=" + r.Data;
            });
        }

        [McpServerTool, Description("Write the CustomData string of a programmable block. In-game scripts read Me.CustomData for configuration, so this lets you configure or reconfigure a script (target names, toggles, settings) without rewriting its code.")]
        public async Task<string> WriteCustomData(
            [Description("Entity id of the programmable block")] long entityId,
            [Description("Full CustomData string to store on the block")] string data,
            CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.WriteCustomDataAsync(entityId, data, ct);
                return "ok=" + r.Ok;
            });
        }

        [McpServerTool, Description("Check the code status of EVERY programmable block at once. For each block reports whether it compiled (ok), the compiler output/errors (detail), script length, and running state. Use this for a fleet-wide health check after editing multiple scripts.")]
        public async Task<string> CheckAllScripts(CancellationToken ct)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.CheckAllAsync(ct);
                var sb = new System.Text.StringBuilder();
                foreach (var c in r.Results)
                {
                    sb.AppendLine("entityId=" + c.EntityId
                        + " ok=" + c.Ok
                        + " length=" + c.Length
                        + " running=" + c.IsRunning
                        + " detail=" + c.Detail);
                }
                return sb.ToString().TrimEnd();
            });
        }

        [McpServerTool, Description("Run a programmable block, optionally passing a run argument string. Returns ok and the block's Echo/console output captured from this run (TryRun is synchronous, so the output is available immediately). Note SE's output buffer accumulates across runs; use check_script first to reset it if you want only this run's output.")]
        public async Task<string> RunScript(
            [Description("Entity id of the programmable block")] long entityId,
            [Description("Optional run argument")] string? argument = null,
            CancellationToken ct = default)
        {
            return await SafeCall(async () =>
            {
                var r = await _client.RunAsync(entityId, argument, ct);
                return "ok=" + r.Ok + "\noutput=" + r.Output;
            });
        }

        // The MCP SDK replaces an unhandled exception with a generic
        // "An error occurred invoking '<tool>'" message that hides the real
        // cause (usually the plugin not being reachable). Return the message as
        // tool output instead, so the model can see and act on it.
        private static async Task<string> SafeCall(Func<Task<string>> action)
        {
            try
            {
                return await action();
            }
            catch (BridgeException ex)
            {
                return "ERROR: " + ex.Message;
            }
            catch (OperationCanceledException)
            {
                return "ERROR: The request was cancelled.";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
