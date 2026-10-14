using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SpaceMcp.Protocol
{
    [JsonDerivedType(typeof(ListRequest), "list")]
    [JsonDerivedType(typeof(ReadRequest), "read")]
    [JsonDerivedType(typeof(WriteRequest), "write")]
    [JsonDerivedType(typeof(RecompileRequest), "recompile")]
    [JsonDerivedType(typeof(RunRequest), "run")]
    [JsonDerivedType(typeof(CheckRequest), "check")]
    [JsonDerivedType(typeof(OutputRequest), "output")]
    [JsonDerivedType(typeof(ReadCustomDataRequest), "readCustomData")]
    [JsonDerivedType(typeof(WriteCustomDataRequest), "writeCustomData")]
    [JsonDerivedType(typeof(CheckAllRequest), "checkAll")]
    public abstract record Request;

    public sealed record ListRequest : Request;

    public sealed record ReadRequest(long EntityId) : Request;

    public sealed record WriteRequest(long EntityId, string Code) : Request;

    public sealed record RecompileRequest(long EntityId) : Request;

    public sealed record RunRequest(long EntityId, string? Arg) : Request;

    /// <summary>
    /// Validates a block's current script by forcing a recompile and reporting
    /// the result. <see cref="CheckResponse.Detail"/> carries the compiler
    /// output (errors/warnings), which is surfaced verbatim by MCP tools so the
    /// model can self-correct failed compiles.
    /// </summary>
    public sealed record CheckRequest(long EntityId) : Request;

    /// <summary>
    /// Reads a block's current console output (the text written by the script's
    /// Echo calls, exposed by SE as DetailedInfo). Does NOT recompile, so the
    /// output from the previous run is preserved.
    /// </summary>
    public sealed record OutputRequest(long EntityId) : Request;

    public sealed record ReadCustomDataRequest(long EntityId) : Request;

    public sealed record WriteCustomDataRequest(long EntityId, string Data) : Request;

    /// <summary>
    /// Validate every programmable block at once (see <see cref="CheckRequest"/>).
    /// </summary>
    public sealed record CheckAllRequest : Request;

    [JsonDerivedType(typeof(ErrorResponse), "error")]
    [JsonDerivedType(typeof(ListResponse), "listResponse")]
    [JsonDerivedType(typeof(ReadResponse), "readResponse")]
    [JsonDerivedType(typeof(WriteResponse), "writeResponse")]
    [JsonDerivedType(typeof(RecompileResponse), "recompileResponse")]
    [JsonDerivedType(typeof(RunResponse), "runResponse")]
    [JsonDerivedType(typeof(CheckResponse), "checkResponse")]
    [JsonDerivedType(typeof(OutputResponse), "outputResponse")]
    [JsonDerivedType(typeof(ReadCustomDataResponse), "readCustomDataResponse")]
    [JsonDerivedType(typeof(WriteCustomDataResponse), "writeCustomDataResponse")]
    [JsonDerivedType(typeof(CheckAllResponse), "checkAllResponse")]
    public abstract record Response;

    public sealed record ErrorResponse(string Message) : Response;

    public sealed record PbInfo(long EntityId, string BlockName, string GridName, bool IsRunning, int Length);

    public sealed record ListResponse(IReadOnlyList<PbInfo> Blocks) : Response;

    public sealed record ReadResponse(long EntityId, string Code) : Response;

    public sealed record WriteResponse(bool Ok, string Detail) : Response;

    public sealed record RecompileResponse(bool Ok, string Detail) : Response;

    public sealed record RunResponse(bool Ok, string Output) : Response;

    /// <summary>
    /// Result of a code check. <see cref="Ok"/> is a heuristic based on whether
    /// the compiler output looks like a failure (contains "error"/"exception");
    /// the raw <see cref="Detail"/> is always passed through so the model can
    /// read the exact compiler message regardless of the heuristic.
    /// </summary>
    public sealed record CheckResponse(long EntityId, bool Ok, string Detail, int Length, bool IsRunning) : Response;

    /// <summary>
    /// Current console output of a block (Echo text). Based on SE's
    /// DetailedInfo, which (per the official SE wiki) accumulates across Echo
    /// calls AND across runs — it is not cleared each run. Each Echo call is
    /// capped at 8000 characters. The buffer is reset by a recompile
    /// (constructor re-runs) and, in singleplayer, when the block's terminal is
    /// opened (a game bug). There is no documented total cap, so this may
    /// contain output from multiple runs since the last recompile.
    /// </summary>
    public sealed record OutputResponse(string Output) : Response;

    public sealed record ReadCustomDataResponse(string Data) : Response;

    public sealed record WriteCustomDataResponse(bool Ok) : Response;

    /// <summary>
    /// Fleet-wide result of <see cref="CheckAllRequest"/>. Each entry is the
    /// same per-block status reported by <see cref="CheckResponse"/>.
    /// </summary>
    public sealed record CheckAllResponse(IReadOnlyList<CheckResponse> Results) : Response;

    public static class Bridge
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions();

        public static byte[] Encode(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

        public static T? Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options);

        public static async Task SendAsync(Stream stream, object message, CancellationToken ct = default)
        {
            var bytes = Encode(message);
            await stream.WriteAsync(bytes, 0, bytes.Length, ct);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, 0, 1, ct);
            await stream.FlushAsync(ct);
        }

        public static async Task<T?> ReceiveAsync<T>(Stream stream, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        if (ms.Length == 0) return default;
                        return Decode<T>(ms.ToArray()!);
                    }
                    ms.WriteByte(buffer[i]);
                }
            }
            return ms.Length == 0 ? default : Decode<T>(ms.ToArray()!);
        }
    }
}
