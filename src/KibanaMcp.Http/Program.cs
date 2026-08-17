using System.Net;
using KibanaMcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddKibanaMcpCore();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithKibanaLogTools();

WebApplication app = builder.Build();
string mcpPath = builder.Configuration["Http:Path"] ?? "/mcp";
string escapedMcpPath = WebUtility.HtmlEncode(mcpPath);
string welcomeHtml = $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Kibana MCP</title>
  <style>
    :root {
      color-scheme: light dark;
      font-family: "Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif;
      background: #f5f7fb;
      color: #172033;
    }

    body {
      margin: 0;
      min-height: 100vh;
      display: grid;
      place-items: center;
      padding: 32px;
    }

    main {
      width: min(760px, 100%);
      border: 1px solid #d8deea;
      border-radius: 8px;
      background: #ffffff;
      padding: 32px;
      box-shadow: 0 10px 30px rgb(20 31 54 / 8%);
    }

    h1 {
      margin: 0 0 10px;
      font-size: 32px;
      line-height: 1.2;
      letter-spacing: 0;
    }

    p {
      margin: 0;
      color: #526070;
      line-height: 1.6;
    }

    dl {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 12px 18px;
      margin: 28px 0 0;
    }

    dt {
      color: #647084;
      font-weight: 600;
    }

    dd {
      margin: 0;
      min-width: 0;
    }

    code {
      display: inline-block;
      max-width: 100%;
      overflow-wrap: anywhere;
      border: 1px solid #d8deea;
      border-radius: 6px;
      background: #f8fafc;
      padding: 3px 7px;
      color: #111827;
      font: 14px ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
    }

    .status {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      color: #0f7a45;
      font-weight: 600;
    }

    .status::before {
      content: "";
      width: 9px;
      height: 9px;
      border-radius: 50%;
      background: #16a34a;
    }

    @media (max-width: 520px) {
      body {
        padding: 18px;
      }

      main {
        padding: 22px;
      }

      dl {
        grid-template-columns: 1fr;
        gap: 6px;
      }
    }

    @media (prefers-color-scheme: dark) {
      :root {
        background: #101826;
        color: #eef3fb;
      }

      main {
        border-color: #293548;
        background: #172033;
        box-shadow: none;
      }

      p,
      dt {
        color: #aab6c7;
      }

      code {
        border-color: #344258;
        background: #101826;
        color: #eef3fb;
      }
    }
  </style>
</head>
<body>
  <main>
    <h1>Kibana MCP</h1>
    <p>HTTP transport is available for MCP clients that connect to this service. All Elasticsearch access flows through the Kibana console proxy.</p>
    <dl>
      <dt>Status</dt>
      <dd><span class="status">Running</span></dd>
      <dt>MCP endpoint</dt>
      <dd><code>{{escapedMcpPath}}</code></dd>
    </dl>
  </main>
</body>
</html>
""";

app.MapGet("/", () => Results.Content(welcomeHtml, "text/html; charset=utf-8"));
app.MapMcp(mcpPath);
await app.RunAsync();
