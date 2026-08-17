# Configuration

## Connection model

The server is **Kibana-only**: there is no Elasticsearch base URL anywhere. Every Elasticsearch call is a `POST` to the Kibana console proxy, which forwards the verb you select in the query string:

```
POST {KibanaBaseUrl}/api/console/proxy
  ?path={es-path}            e.g. ubs-lottery-exception-*/_count
  &method=GET|POST|...       the ES verb the proxy should forward
  [+&apiVersion=...]
```

Headers sent on every request:

| Header | Value | Why |
| --- | --- | --- |
| `Authorization: Basic …` | Basic of `UserName:Password` | Gateway/ES auth |
| `kbn-xsrf` | `true` | Kibana anti-CSRF requirement |
| `kbn-version` | `KibanaVersion` config (default `7.17.28`) | Kibana rejects browser-origin requests whose version does not match the running build |

## Settings

```json
{
  "Environments": {
    "prod": {
      "KibanaBaseUrl": "https://kibana-elk-pe-prod-dc2-jj.everymatrix.local",
      "UserName": "filebeat_writer",
      "Password": "kDe3LwTN8Pj56BkgCeDq",
      "KibanaVersion": "7.17.28",
      "ProxyApiVersion": null
    }
  },
  "DefaultTimeZone": "Asia/Shanghai",
  "RequestTimeoutMs": 120000,
  "Http": { "Path": "/mcp" }
}
```

### Per-environment

| Key | Required | Description |
| --- | --- | --- |
| `KibanaBaseUrl` | yes | Base URL of Kibana, no trailing slash. |
| `UserName` / `Password` | yes | HTTP Basic credentials for the proxy. |
| `KibanaVersion` | no | Version string reported in the `kbn-version` header. If unset, the client default `7.17.28` is used. Set it when the deployment's actual Kibana build differs. |
| `ProxyApiVersion` | no | `apiVersion` query parameter for `/api/console/proxy`. Omit (leave blank) for builds that reject it. Set e.g. `1.0` when the deployment requires it. |
| `SessionCookie` | no | Verbatim `Cookie` header sent on every proxy request. Some gateways terminate the SSO session server-side and re-validate only against a login cookie (Authelia's `authelia_session`), so a browser-captured cookie is required in addition to — or instead of — Basic credentials. Grab it from DevTools on the gateway, then set `Environments:<name>:SessionCookie: "authelia_session=<value>"`. |

### Global

| Key | Description |
| --- | --- |
| `DefaultTimeZone` | IANA identifier used when a call does not pass one. Defaults to `Asia/Shanghai`. |
| `RequestTimeoutMs` | Per-request HTTP timeout in milliseconds. Default `120000`. |
| `Http:Path` | HTTP-host-only endpoint path served at `/`. Default `/mcp`. |

## Overriding at runtime

The app is a normal .NET configuration host, so any setting can be overridden with standard environment variables or the `--` command-line switch, e.g.:

```bash
# environment variables (double underscore separates sections/keys)
Environments__prod__UserName="you@corp" Environments__prod__Password="…" \
  dotnet KibanaMcp.Stdio.dll

# or command line
dotnet KibanaMcp.Stdio.dll --Environments:prod:UserName "you@corp" --Environments:prod:Password "…"
```

## TLS / certificates

Cluster hosts in the `everymatrix.local` domain are served by a private CA. The proxy client skips chain validation **only** when the request's host name ends in `.everymatrix.local` (or equals `everymatrix.local`); all other host names are validated strictly. The recommended production setup is to install the enterprise root CA on the host and remove that relaxation.

## Known gateway behavior (verified on prod 2026-08)

1. The gateway (openresty + Authelia) validates Basic credentials against its own user directory **before** the proxy sees the request. Credentials that exist only inside Elasticsearch (for example a `filebeat_writer` service account) are rejected with `302 → Authelia login` and never reach Kibana. Use a gateway-known user.
2. Kibana answers `400 "Browser client is out of date"` when `kbn-version` does not match the running build. The deployment here runs Kibana **7.17.28**.
3. This Kibana build rejects an `apiVersion` query parameter with `400 "definition for this key is missing"`. The default is therefore to omit it.
4. A `_cat/indices` request reaches the proxy as a query-string-only `GET` and is accepted with the exact options shown in `search_index` (path includes `?format=json&bytes=b&h=index…`), so only the messages described above were required to make the full tool surface work.

## Troubleshooting

### Tool returns `error: code: AUTH_REQUIRED`
The gateway redirected the proxy request to its SSO login (or inlined the login page), which means the request never reached Elasticsearch. Auto-redirect is disabled in the environment client so the `302 → Authelia` (`Location: https://authelia.…`) is surfaced directly, and a 2xx login-page body is recognized as the SSO page. Check credentials at the *gateway*:

1. Use a login-capable account that the gateway's directory knows (Basic credentials are validated by the gateway **before** the proxy sees them; an Elasticsearch-only service account such as `filebeat_writer` is rejected and redirected).
2. If the gateway requires an SSO session cookie, set `Environments:<env>:SessionCookie: "authelia_session=<value>"` (captured from a logged-in browser via DevTools).
3. If `UserName`/`Password` have rotated, update them and restart.

Re-test after a fix with the curl probe below.

<details>
<summary>curl probe script</summary>

```bash
curl -sS -k -D - -X POST \
  "https://kibana.../api/console/proxy?path=ubs-lottery-exception-*/_count&method=GET" \
  -u "user:pass" \
  -H "kbn-xsrf: true" \
  -H "kbn-version: 7.17.28" \
  -H "Content-Type: application/json" \
  -H 'Cookie: authelia_session=<value>' \
  -d '{"query":{"bool":{"filter":[]}}}'
# -D - prints response headers so you can see the 302 Location (or the final 200).
```
</details>

### Tool returns `error: code: ES_RESPONSE_INVALID`
A 2xx response whose body is empty or not valid JSON — the hardening case when a 200 is served with an HTML/empty body that was not recognized as an SSO page. The message includes a snippet of the raw body.

### `error: code: KIBANA_ERROR` / `KIBANA_UNREACHABLE`
Transport or proxy-level failure (non-2xx with no parseable ES error body). Confirm Kibana is reachable on 443 from the host running the server, the CA trust situation, and the `KibanaVersion`/`ProxyApiVersion` settings.
