<div align="center">

# Kibana MCP

[中文 README](README.zh.md) · [English README](README.md)

</div>

A .NET 10 Model Context Protocol (MCP) server that provides read-only Elasticsearch log investigation. Instead of talking to Elasticsearch on port 9200 directly, **all access goes through the Kibana console proxy REST API** (`/api/console/proxy`) — the only working entry point for clusters whose 9200 port is not reachable but whose Kibana 443 is.

It is a from-scratch rewrite of the existing Elasticsearch MCP (`elastic-mcp`), keeping **all tool functionality and every tool name/description byte-for-byte identical**, and it ships both **HTTP** and **stdio** transports as separate host projects around one shared Core library.

## Features

- **Kibana-native transport** — every Elasticsearch call is routed through the Kibana console proxy, exactly like the reference `ElasticProxyDemo`.
- **Identical tool surface** — the seven tools (`count_logs`, `aggregate_logs`, `search_index`, `time_series`, `compare_windows`, `search_samples`, [`discover_fields`](#)) keep the same names, parameter descriptions, and YAML response shapes as the original Elasticsearch MCP.
- **Dual transports** — `KibanaMcp.Http` (ASP.NET Core, streamable HTTP) and `KibanaMcp.Stdio` (newline-delimited JSON over stdin/stdout), sharing all logic in `KibanaMcp.Core`.
- **Async + pooled concurrency** — one lazy per-host `HttpClient` pool is shared across all tool calls and threads; parallel queries (current/baseline windows, data-view lookups) run concurrently via `Task.WhenAll`; everything is `async` end to end.
- **Kibana Discover deep links** — every response carries `reviewLinks` (per-bucket, per-window, context view) so the caller can open the exact result in Kibana.
- **Designed for extension** — the transport layer, environment resolution, and tool/service boundary are cleanly separated so adding write/delete/ingest tools later is a matter of adding tool+service methods, not re-plumbing.

## Project structure

```
kibana-mcp/
├── KibanaMcp.slnx
├── src/
│   ├── KibanaMcp.Core/          # shared library: everything both hosts need
│   │   ├── KibanaRestClient.cs          # Kibana console-proxy ES client (pooled, async)
│   │   ├── KibanaEnvironmentProvider.cs # reads "Environments" config section
│   │   ├── KibanaLogService.cs          # all tool logic (port of ElasticMcp.ElasticLogService)
│   │   ├── KibanaLogTools.cs            # [McpServerTool] declarations (names/descriptions preserved)
│   │   ├── KibanaDataViewResolver.cs    # data-view id lookup in .kibana* saved objects
│   │   ├── KibanaReviews.cs             # Kibana Discover deep-link builder
│   │   ├── KibanaLogToolRegistry.cs     # MCP tool registration + custom input schemas
│   │   ├── KibanaMcpToolSchema.cs       # env enum injection into tool schemas
│   │   ├── KibanaMcpServerInstructions.cs
│   │   ├── KibanaMcpServiceCollectionExtensions.cs  # AddKibanaMcpCore DI
│   │   ├── Models.cs                     # tool input models + JSON converters
│   │   ├── TimeRangeResolver.cs          # preset/custom time-window resolution (+TZ)
│   │   ├── TimeZoneResolver.cs
│   │   ├── IndexCatalog.cs               # search_index family descriptions
│   │   ├── YamlResponse.cs               # consistent YAML success/error envelope
│   │   ├── JsonDefaults.cs
│   │   └── appsettings.json
│   ├── KibanaMcp.Http/          # HTTP transport host (ASPNET Core, stateless /mcp endpoint)
│   └── KibanaMcp.Stdio/         # stdio transport host (newline-delimited JSON stdio)
└── docs/
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or .NET 10 runtime to run published binaries)
- A Kibana instance reachable on 443 whose console proxy allows the configured user
- Configured **Basic-auth** credentials that the gateway accepts (see [Configuration](#configuration))

## Configuration

Copy the shipped `src/KibanaMcp.Core/appsettings.json` to the host output directory next to the executable (both host projects already link to it and copy it to the output). The format follows the specification:

```json
{
  "Environments": {
    "prod": {
      "KibanaBaseUrl": "https://kibana.example.local",
      "UserName": "filebeat_writer",
      "Password": "kDe3LwTN8Pj56BkgCeDq"
    }
  },
  "DefaultTimeZone": "Asia/Shanghai",
  "RequestTimeoutMs": 120000,
  "Kibana": {
    "TlsInsecureHostPattern": "(^\\.)?(.*)\\.(local|internal)$"
  },
  "Http": {
    "Path": "/mcp"
  }
}
```

| Setting | Description |
| --- | --- |
| `Environments:<name>:KibanaBaseUrl` | Base URL of Kibana (no trailing slash). The ES calls go to `{base}/api/console/proxy?path=…&method=…`. |
| `Environments:<name>:UserName` / `Password` | HTTP Basic credentials sent with every proxy request. |
| `Environments:<name>:KibanaVersion` *(optional)* | Version reported in the `kbn-version` header. Kibana answers 400 "Browser client is out of date" when this does not match the running build. Defaults to `7.17.28` (proven against the target production gateway). |
| `Environments:<name>:ProxyApiVersion` *(optional)* | `apiVersion` query parameter for `/api/console/proxy`. Some builds reject it with 400 "definition for this key is missing". Default: omitted. |
| `Environments:<name>:SessionCookie` *(optional)* | Verbatim `Cookie` header sent with every proxy request, for gateways that require the SSO session cookie (for example Authelia) in addition to Basic. Capture `authelia_session=<value>` from a logged-in browser. |
| `Kibana:TlsInsecureHostPattern` | Regex matched against the host name; matching hosts skip TLS chain validation (private CA). Empty string = validate every host. Defaults to internal single-label domains. |
| `DefaultTimeZone` | IANA time zone used when a call does not specify one. |
| `RequestTimeoutMs` | Per-request HTTP timeout. |
| `Http:Path` | (HTTP host only) endpoint path, default `/mcp`. |

> **Note on credentials**: the production gateway sits behind an auth layer (for example Authelia SSO) that validates Basic credentials in its own user directory. The values that work at the gateway may differ from the cluster's Elasticsearch users — bring your own reachable-federated/Basic user when the shipped AWS entry is not accepted. Environment variables override appsettings at runtime when needed:
>
> ```bash
> Environments__prod__UserName="you@corp" Environments__prod__Password="…" dotnet KibanaMcp.Stdio.dll
> ```

See the [configuration deep dive](docs/configuration.md) for full details, including the proxy request anatomy and TLS/Certificate notes.

## Tools

All seven tools (plus the internally-kept `export_raw_es_response`) are exposed with the **same names and descriptions** as the original Elasticsearch MCP:

| Tool | Purpose |
| --- | --- |
| `count_logs` | Exact number of matching documents in an index target within a time range. |
| `aggregate_logs` | Guarded structured aggregations (up to 2 group levels, count/avg/min/max/sum/cardinality/percentiles metrics). |
| `search_index` | Lists live index families matching a pattern, grouped by logical prefix with catalog annotations. |
| `time_series` | Counts/metric over time buckets (1m…1d), optionally split by a field. |
| `compare_windows` | Compares counts/metrics between current and baseline windows (increase/decrease/new/missing). |
| `search_samples` | Returns a small page of docs with limited `_source`, supports search_after pagination. |
| `discover_fields` | Field capabilities (`_field_caps`) with filter by prefix/type/aggregatable/searchable. |

Each tool takes `env` (a configured environment name), a raw `index` target (commas and `-`/`+` include/exclude wildcards allowed, e.g. `ubs-lottery-api*,-ubs-lottery-draw*`), and a `timeRange` (preset string like `today` / `last_30_minutes`, preset object, or custom `gt/gte/lt/lte` object). Results are returned as YAML text blocks that include the resolved time window and `reviewLinks` into Kibana Discover whenever the data view is resolvable. `search_index` additionally emits a management-style Discover link per include pattern using the pattern itself as the data-view reference, which works without `.kibana` read access. **Errors carry `reviewLinks` too** — a failed query still returns a Kibana Discover URL for the requested index, so manual investigation starts from the link. Aggregations over a mapped `text` field are automatically retried once with `.keyword` appended when Elasticsearch rejects them.

## Running

### stdio

```bash
dotnet run --project src/KibanaMcp.Stdio
```

Configure in an MCP client (for example Claude Desktop `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "kibana": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\kibana-mcp\\src\\KibanaMcp.Stdio"]
    }
  }
}
```

### HTTP

```bash
dotnet run --project src/KibanaMcp.Http --urls http://localhost:50791
```

Point an MCP HTTP client at `http://localhost:50791/mcp`. Opening the root URL in a browser shows a status page.

## Performance & concurrency

- **`async` everywhere** — no blocking `Wait()/.Result`; long operations yield on I/O.
- **Connection pooling** — one `HttpClient` per Kibana host, created lazily and cached in a `ConcurrentDictionary`, with `SocketsHttpHandler` connection pooling (TCP + TLS session reuse). Concurrent tool calls across all threads share it, so fan-out to Elasticsearch uses far fewer connections than per-request clients.
- **Parallel fan-out** — `compare_windows` issues current, baseline, and data-view lookups concurrently (`Task.WhenAll`); `count_logs`/`search_samples` fire the data-view lookup in parallel with the main query.
- **Thread-safe JsonNodes** — responses are parsed per call into local `JsonObject` trees; nothing mutable is shared across requests.
- **Cancellation** — a `CancellationToken` is carried from each tool call into the HTTP layer.

## Reliability & error handling

- **Consistent envelope** — every tool returns YAML with either `data:` (a `timeWindow`, `limits` when truncated, `reviewLinks`) or `error:` (`code`, `message`, `retriable`, `details`).
- **Kibana/ES error mapping** — HTTP failures are parsed and surfaced as `ELASTICSEARCH_ERROR` (with ES `type`/`reason`) when the body is an ES error, otherwise as `KIBANA_ERROR`, and transport failures as retriable `KIBANA_UNREACHABLE`/`TIMEOUT`. Auto-redirect is disabled and both a `3xx Location` to the SSO portal and a recognized 2xx Authelia login page degrade to retriable `AUTH_REQUIRED`, telling the caller the request never reached Elasticsearch and that the gateway credentials need attention (Basic user accepted by the gateway's SSO directory, or a `SessionCookie` when required). Other gateway HTML pages (reverse-proxy errors, blanket 401/502/504) are `KIBANA_GATEWAY_ERROR` with the page title and a body snippet; a 2xx body that is empty or otherwise not valid JSON degrades to `ES_RESPONSE_INVALID` with a snippet of the raw body. No path ever exposes a raw `System.Text.Json` parser error.
- **Guards** — result-size limits (max 5000 agg rows, max 1000 terms size, max 100 samples), metric/group validation, and read-only enforcement (only `search`, `count`, `field_caps` reach the raw-export path).
- **Graceful degradation** — data-view lookups that fail (for example the user cannot read `.kibana*`) simply suppress `reviewLinks`; the tool result is unaffected.

## Technology, principles

- **.NET 10**, `net10.0`, ASP.NET Core (HTTP host) / generic Host (stdio host)
- **`ModelContextProtocol` 2.1.0** + `ModelContextProtocol.AspNetCore`
- **YAML responses** via `YamlDotNet`
- Designed with **extensibility** in mind: transport hosts remain thin, business logic lives in `KibanaLogService`, and adding new tools (including future write/delete/ingest tools) means adding a `[McpServerTool]` method + one service method + one input model — the schema, DI, and JSON plumbing are already in place.

## Documentation

- [English README](README.md) · [中文 README](README.zh.md)
- [Configuration deep dive](docs/configuration.md)
- [Tool reference](docs/tools.md)

## License

Proprietary.
