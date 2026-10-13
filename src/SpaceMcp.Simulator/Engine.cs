using System.Collections.Generic;
using System.Collections.Concurrent;
using SpaceMcp.Protocol;

namespace SpaceMcp.Simulator
{
    public sealed record PbState(string BlockName, string GridName, string Code, bool Running);

    public sealed class SimulatorEngine
    {
        private readonly ConcurrentDictionary<long, PbState> _blocks = new ConcurrentDictionary<long, PbState>();

        public SimulatorEngine()
        {
            _blocks[123] = new PbState("Programmable Block", "Test Ship", "public void Main()\n{\n    Echo(\"hello\");\n}", true);
            _blocks[456] = new PbState("Cargo Script [Miner]", "Miner Grid", "public void Main() { }", false);
        }

        public Response Handle(Request request)
        {
            switch (request)
            {
                case ListRequest:
                    var list = new List<PbInfo>();
                    foreach (var kv in _blocks)
                    {
                        var s = kv.Value;
                        list.Add(new PbInfo(kv.Key, s.BlockName, s.GridName, s.Running, s.Code.Length));
                    }
                    return new ListResponse(list);

                case ReadRequest r:
                    return _blocks.TryGetValue(r.EntityId, out var b)
                        ? new ReadResponse(r.EntityId, b.Code)
                        : new ErrorResponse("Unknown entityId");

                case WriteRequest w:
                    if (!_blocks.TryGetValue(w.EntityId, out var cur)) return new ErrorResponse("Unknown entityId");
                    _blocks[w.EntityId] = cur with { Code = w.Code };
                    return new WriteResponse(true, "Compiled (simulated).");

                case RecompileRequest r:
                    return _blocks.ContainsKey(r.EntityId)
                        ? new RecompileResponse(true, "Recompiled (simulated).")
                        : new ErrorResponse("Unknown entityId");

                case RunRequest r:
                    if (!_blocks.ContainsKey(r.EntityId)) return new ErrorResponse("Unknown entityId");
                    if (_blocks.TryGetValue(r.EntityId, out var st))
                        _blocks[r.EntityId] = st with { Running = true };
                    return new RunResponse(true);

                default:
                    return new ErrorResponse("Unknown command");
            }
        }
    }
}
