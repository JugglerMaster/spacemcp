using System.Collections.Generic;
using System.Collections.Concurrent;
using SpaceMcp.Protocol;

namespace SpaceMcp.Simulator
{
    public sealed record PbState(string BlockName, string GridName, string Code, bool Running, string Output, string CustomData);

    public sealed class SimulatorEngine
    {
        private readonly ConcurrentDictionary<long, PbState> _blocks = new ConcurrentDictionary<long, PbState>();

        public SimulatorEngine()
        {
            _blocks[123] = new PbState("Programmable Block", "Test Ship", "public void Main()\n{\n    Echo(\"hello\");\n}", true, "hello\n(simulated echo output)", "");
            _blocks[456] = new PbState("Cargo Script [Miner]", "Miner Grid", "public void Main() { }", false, "", "");
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
                    _blocks[w.EntityId] = cur with { Code = w.Code, Output = "" };
                    return new WriteResponse(true, "Compiled (simulated).");

                case RecompileRequest r:
                    return _blocks.ContainsKey(r.EntityId)
                        ? new RecompileResponse(true, "Recompiled (simulated).")
                        : new ErrorResponse("Unknown entityId");

                case RunRequest r:
                    {
                        if (!_blocks.ContainsKey(r.EntityId)) return new ErrorResponse("Unknown entityId");
                        if (_blocks.TryGetValue(r.EntityId, out var st))
                            _blocks[r.EntityId] = st with { Running = true, Output = "Echo output (simulated) for " + st.BlockName };
                        var updated = _blocks[r.EntityId];
                        return new RunResponse(true, updated.Output);
                    }

                case CheckRequest r:
                    {
                        if (!_blocks.TryGetValue(r.EntityId, out var state))
                            return new ErrorResponse("Unknown entityId");
                        // Lightweight validation: a real PB compile happens in
                        // the plugin. Here we sanity-check the script so the
                        // check/error-passthrough flow can be exercised in tests.
                        var ok = !string.IsNullOrWhiteSpace(state.Code) && state.Code.Contains("Main");
                        var detail = ok
                            ? "Scripts OK (simulated)."
                            : "Error: No Main method found (simulated).";
                        return new CheckResponse(r.EntityId, ok, detail, state.Code.Length, state.Running);
                    }

                case OutputRequest r:
                    {
                        if (!_blocks.TryGetValue(r.EntityId, out var blockState))
                            return new ErrorResponse("Unknown entityId");
                        return new OutputResponse(blockState.Output);
                    }

                case ReadCustomDataRequest r:
                    {
                        if (!_blocks.TryGetValue(r.EntityId, out var cd))
                            return new ErrorResponse("Unknown entityId");
                        return new ReadCustomDataResponse(cd.CustomData);
                    }

                case WriteCustomDataRequest r:
                    {
                        if (!_blocks.TryGetValue(r.EntityId, out var cdState))
                            return new ErrorResponse("Unknown entityId");
                        _blocks[r.EntityId] = cdState with { CustomData = r.Data };
                        return new WriteCustomDataResponse(true);
                    }

                case CheckAllRequest:
                    {
                        var results = new List<CheckResponse>();
                        foreach (var kv in _blocks)
                        {
                            var s = kv.Value;
                            var ok = !string.IsNullOrWhiteSpace(s.Code) && s.Code.Contains("Main");
                            var detail = ok
                                ? "Scripts OK (simulated)."
                                : "Error: No Main method found (simulated).";
                            results.Add(new CheckResponse(kv.Key, ok, detail, s.Code.Length, s.Running));
                        }
                        return new CheckAllResponse(results);
                    }

                default:
                    return new ErrorResponse("Unknown command");
            }
        }
    }
}
