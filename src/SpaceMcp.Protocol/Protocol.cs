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
    public abstract record Request;

    public sealed record ListRequest : Request;

    public sealed record ReadRequest(long EntityId) : Request;

    public sealed record WriteRequest(long EntityId, string Code) : Request;

    public sealed record RecompileRequest(long EntityId) : Request;

    public sealed record RunRequest(long EntityId, string? Arg) : Request;

    [JsonDerivedType(typeof(ErrorResponse), "error")]
    [JsonDerivedType(typeof(ListResponse), "listResponse")]
    [JsonDerivedType(typeof(ReadResponse), "readResponse")]
    [JsonDerivedType(typeof(WriteResponse), "writeResponse")]
    [JsonDerivedType(typeof(RecompileResponse), "recompileResponse")]
    [JsonDerivedType(typeof(RunResponse), "runResponse")]
    public abstract record Response;

    public sealed record ErrorResponse(string Message) : Response;

    public sealed record PbInfo(long EntityId, string BlockName, string GridName, bool IsRunning, int Length);

    public sealed record ListResponse(IReadOnlyList<PbInfo> Blocks) : Response;

    public sealed record ReadResponse(long EntityId, string Code) : Response;

    public sealed record WriteResponse(bool Ok, string Detail) : Response;

    public sealed record RecompileResponse(bool Ok, string Detail) : Response;

    public sealed record RunResponse(bool Ok) : Response;

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
