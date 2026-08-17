<div align="center">

# Kibana MCP

[中文 README](README.zh.md) · [English README](README.md)

</div>

一个基于 .NET 10 的 Model Context Protocol（MCP）服务器，用于只读 Elasticsearch 日志排查。与直接访问 9200 端口不同——**所有对 Elasticsearch 的访问均经由 Kibana console proxy REST API**（`/api/console/proxy`），这是我们环境中唯一可用的入口：9200 不可达，而 Kibana 的 443 可达。

本项目基于现有 ElasticSearch MCP（`elastic-mcp`）从零重写，**保留全部工具功能，且每个工具的名称与描述逐字保持不变**，并围绕共享 Core 库提供 **HTTP** 与 **stdio** 两种传输方式的独立宿主项目。

## 功能特性

- **Kibana 原生传输** —— 所有 Elasticsearch 调用均经由 Kibana console proxy 转发，与参考实现 `ElasticProxyDemo` 完全一致。
- **工具面完全一致** —— 七个工具（`count_logs`、`aggregate_logs`、`search_index`、`time_series`、`compare_windows`、`search_samples`、`discover_fields`）与原 Elasticsearch MCP 保持相同名称、参数描述与 YAML 响应结构。
- **双传输方式** —— `KibanaMcp.Http`（ASP.NET Core，流式 HTTP）与 `KibanaMcp.Stdio`（标准输入/输出的换行分隔 JSON），业务逻辑全部共享在 `KibanaMcp.Core`。
- **异步 + 连接池并发** —— 每个 Kibana 主机按需懒加载一个共享 `HttpClient` 池，跨所有工具调用与线程复用；并行查询（当前/基线窗口、data-view 解析）通过 `Task.WhenAll` 并发执行；全链路异步。
- **Kibana Discover 深链** —— 每个响应都携带 `reviewLinks`（逐桶、逐窗口、上下文视图），调用方可直接打开 Kibana 查看精确结果。
- **为扩展而设计** —— 传输层、环境解析、工具/服务边界清晰分离，未来新增写入/删除/导入工具只需新增 tool + service 方法，无需重新搭建管道。

## 项目结构

```
kibana-mcp/
├── KibanaMcp.slnx
├── src/
│   ├── KibanaMcp.Core/          # 共享库：两个宿主所需的一切
│   │   ├── KibanaRestClient.cs          # Kibana console-proxy ES 客户端（池化、异步）
│   │   ├── KibanaEnvironmentProvider.cs # 读取 "Environments" 配置节
│   │   ├── KibanaLogService.cs          # 全部工具逻辑（ElasticMcp.ElasticLogService 的移植）
│   │   ├── KibanaLogTools.cs            # [McpServerTool] 声明（名称/描述保持原样）
│   │   ├── KibanaDataViewResolver.cs    # 在 .kibana* 保存对象中查找 data-view 的 id
│   │   ├── KibanaReviews.cs             # Kibana Discover 深链构造器
│   │   ├── KibanaLogToolRegistry.cs     # MCP 工具注册 + 自定义输入 Schema
│   │   ├── KibanaMcpToolSchema.cs       # env 枚举注入工具 Schema
│   │   ├── KibanaMcpServerInstructions.cs
│   │   ├── KibanaMcpServiceCollectionExtensions.cs  # AddKibanaMcpCore DI
│   │   ├── Models.cs                     # 工具输入模型 + JSON 转换器
│   │   ├── TimeRangeResolver.cs          # 预设/自定义时间窗口解析（含时区）
│   │   ├── TimeZoneResolver.cs
│   │   ├── IndexCatalog.cs               # search_index 索引族描述
│   │   ├── YamlResponse.cs               # 统一的 YAML 成功/错误封装
│   │   ├── JsonDefaults.cs
│   │   └── appsettings.json
│   ├── KibanaMcp.Http/          # HTTP 传输宿主（ASP.NET Core，无状态 /mcp 端点）
│   └── KibanaMcp.Stdio/         # stdio 传输宿主（标准输入输出换行分隔 JSON）
└── docs/
```

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（仅运行已发布二进制则需 .NET 10 运行时）
- 443 端口可达的 Kibana，且其 console proxy 允许配置的用户访问
- 网关接受的 **Basic 认证** 凭据（参见 [配置](#配置)）

## 配置

将随附的 `src/KibanaMcp.Core/appsettings.json` 复制到宿主输出目录（两个宿主工程已链接该文件并复制到输出目录）。格式遵循需求规范：

```json
{
  "Environments": {
    "prod": {
      "KibanaBaseUrl": "https://kibana-elk-pe-prod-dc2-jj.everymatrix.local",
      "UserName": "filebeat_writer",
      "Password": "kDe3LwTN8Pj56BkgCeDq"
    }
  },
  "DefaultTimeZone": "Asia/Shanghai",
  "RequestTimeoutMs": 120000,
  "Http": {
    "Path": "/mcp"
  }
}
```

| 配置项 | 说明 |
| --- | --- |
| `Environments:<名称>:KibanaBaseUrl` | Kibana 基础 URL（末尾不带 `/`）。ES 调用发往 `{base}/api/console/proxy?path=…&method=…`。 |
| `Environments:<名称>:UserName` / `Password` | 随每次代理请求发送的 HTTP Basic 凭据。 |
| `Environments:<名称>:KibanaVersion` *（可选）* | `kbn-version` 头中上报的版本。若与实际 Kibana 构建版本不匹配，Kibana 会以 400 "Browser client is out of date" 拒绝。默认 `7.17.28`（已针对目标生产网关验证）。 |
| `Environments:<名称>:ProxyApiVersion` *（可选）* | `/api/console/proxy` 的 `apiVersion` 查询参数。部分构建会以 400 "definition for this key is missing" 拒绝。默认：省略。 |
| `DefaultTimeZone` | 调用未指定时使用的 IANA 时区。 |
| `RequestTimeoutMs` | 单次 HTTP 请求超时。 |
| `Http:Path` | （仅 HTTP 宿主）端点路径，默认 `/mcp`。 |

> **凭据说明**：生产网关前有认证层（例如 Authelia SSO），在网关的用户目录中校验 Basic 凭据。网关实际可用的值可能与集群内部的 Elasticsearch 用户不同——当内置账号不被接受时，请使用网关可达的联合认证/Basic 用户。应用运行时可通过环境变量覆盖 appsettings：
>
> ```bash
> Environments__prod__UserName="you@corp" Environments__prod__Password="…" dotnet KibanaMcp.Stdio.dll
> ```

详细的代理请求结构、TLS/证书说明与排查请见 [配置深入文档](docs/configuration.md)。

## 工具列表

与原始 Elasticsearch MCP **名称与描述完全一致**地暴露七个工具（内部另保留 `export_raw_es_response`）：

| 工具 | 用途 |
| --- | --- |
| `count_logs` | 时间范围内索引目标中匹配文档的精确数量。 |
| `aggregate_logs` | 受控结构化聚合（最多 2 层分组，count/avg/min/max/sum/cardinality/percentiles 指标）。 |
| `search_index` | 列出匹配某模式的实时索引族，按逻辑前缀分组并附加目录注释。 |
| `time_series` | 按时间桶（1m…1d）统计计数/指标，可选用字段拆分。 |
| `compare_windows` | 对比当前与基线窗口的计数/指标（上涨/下跌/新增/消失）。 |
| `search_samples` | 返回少量文档，限制 `_source`，支持 search_after 分页。 |
| `discover_fields` | 字段能力（`_field_caps`），可按前缀/类型/可聚合/可搜索过滤。 |

每个工具都接收 `env`（已配置的环境名称）、原始 `index` 目标（支持逗号与 `-`/`+` 通配包含排除，如 `ubs-lottery-api*,-ubs-lottery-draw*`）以及 `timeRange`（预设字符串如 `today`/`last_30_minutes`、预设对象或自定义 `gt/gte/lt/lte` 对象）。结果以 YAML 文本块返回，包含已解析的时间窗口，并在可解析 data-view 时附带 Kibana Discover `reviewLinks`。

## 运行方式

### stdio

```bash
dotnet run --project src/KibanaMcp.Stdio
```

在 MCP 客户端中配置（例如 Claude Desktop 的 `claude_desktop_config.json`）：

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

将 MCP HTTP 客户端指向 `http://localhost:50791/mcp`。浏览器打开根路径可查看状态页。

## 性能与并发

- **全链路 `async`** —— 无阻塞 `Wait()/.Result`，长操作在 I/O 处让出线程。
- **连接池** —— 每个 Kibana 主机一个 `HttpClient`，在 `ConcurrentDictionary` 中懒创建并缓存，`SocketsHttpHandler` 连接池复用 TCP + TLS 会话。并发工具调用跨线程共享，向 ES 的扇出远少于逐请求新建客户端。
- **并行扇出** —— `compare_windows` 并发发起当前窗口、基线窗口与 data-view 查询（`Task.WhenAll`）；`count_logs`/`search_samples` 将 data-view 查询与主查询并行。
- **线程安全 JsonNode** —— 每次调用将响应解析到本地 `JsonObject` 树，请求间不共享任何可变状态。
- **取消支持** —— `CancellationToken` 从每个工具调用一路传递到 HTTP 层。

## 可靠性与错误处理

- **统一封装** —— 每个工具均返回 YAML：要么 `data:`（含 `timeWindow`、截断时 `limits`、`reviewLinks`），要么 `error:`（含 `code`、`message`、`retriable`、`details`）。
- **Kibana/ES 错误映射** —— HTTP 失败被解析：响应体是 ES 错误时以 `ELASTICSEARCH_ERROR` 返回（附 ES `type`/`reason`），否则为 `KIBANA_ERROR`，传输失败返回可重试的 `KIBANA_UNREACHABLE`/`TIMEOUT`。
- **防护** —— 结果数量上限（聚合最多 5000 行、terms 最大 1000、样本最多 100）、指标/分组校验，以及只读强制（原始导出路径仅允许 `search`、`count`、`field_caps`）。
- **优雅降级** —— data-view 查询失败（例如用户无权限读 `.kibana*`）时仅抑制 `reviewLinks`，工具结果不受影响。

## 技术栈与设计原则

- **.NET 10**、`net10.0`、ASP.NET Core（HTTP 宿主）/ 泛型 Host（stdio 宿主）
- **`ModelContextProtocol` 2.1.0** + `ModelContextProtocol.AspNetCore`
- **YAML 响应** —— 使用 `YamlDotNet`
- 以**可扩展性**为设计目标：传输宿主保持精简，业务逻辑集中在 `KibanaLogService`；新增工具（包括未来的写入/删除/导入工具）只需新增一个 `[McpServerTool]` 方法 + 一个服务方法 + 一个输入模型——Schema、DI 与 JSON 管道均已就绪。

## 文档

- [English README](README.md) · [中文 README](README.zh.md)
- [配置深入](docs/configuration.md)
- [工具参考](docs/tools.md)

## 许可证

专有。
