using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SpaceMcp.McpServer;

var host = Environment.GetEnvironmentVariable("SPACE_MCP_HOST") ?? "127.0.0.1";
var port = int.Parse(Environment.GetEnvironmentVariable("SPACE_MCP_PORT") ?? "4627");

var builder = Host.CreateApplicationBuilder(args);
// MCP uses stdin/stdout for JSON-RPC; any console logging must NOT go to stdout
// or real MCP clients (opencode, Claude) will fail to parse the stream.
builder.Logging.ClearProviders();
builder.Services.AddSingleton(new SpaceMcpClient(host, port));
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
builder.Build().Run();
