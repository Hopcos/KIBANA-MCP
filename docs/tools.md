# Tool reference

All tools take the same core arguments and return YAML. This page documents the parameters and response shapes. Names, parameter descriptions, and response semantics are identical to the original Elasticsearch MCP.

## Common arguments

| Parameter | Description |
| --- | --- |
| `env` | Required. A configured environment name from `Environments` (schema is constrained to the configured list). |
| `index` | Required for query tools. The **raw Elasticsearch index target** — no URL, no endpoint, no leading/trailing slash. Comma-separated include/exclude wildcard patterns are allowed, e.g. `ubs-lottery-api*,-ubs-lottery-draw*`. |
| `timeRange` | Time window. One of: a preset string (`today`, `yesterday`, `yesterday_same_window`, `last_N_minutes|_hours|_days|_full_days|_full_hours`), a `{ preset, timeZone }` object, or a custom `{ gt, gte, lt, lte, timeZone }` object with at least one boundary. |
| `query` | Optional Lucene `query_string` expression, e.g. `method:PredictBonusRequest AND milliseconds:>10000`. Keep time conditions in `timeRange`, never in `query`. |

Each response returns YAML shaped as:

```yaml
timeWindow:        # the resolved window actually queried
  input: last_30_minutes
  gte: 2026-08-16T08:30:11+08:00
  lte: 2026-08-16T09:00:11+08:00
data:
  ...              # tool-specific payload (see below)
reviewLinks:       # present whenever the data view was resolved
  - https://kibana.../app/discover#/?_g=...
limits:            # present when the response was truncated
  returnedRows: 5000
  truncated: true
```

On failure the same envelope carries `error.code` / `error.message` / `error.retriable` / `error.details` instead of `data`.

## count_logs

Exact document count for an index target within the window.

- Returns: `data.count` (long).

## aggregate_logs

Guarded structured aggregation.

- `groups`: up to 2 group levels, each `{ type: "terms"|"date_histogram", ... }`. A `terms` group: `{ type, field, size?, orderBy?: count|key|<metric>, order?: desc|asc }`. A `date_histogram` group: `{ type, field, interval: 1m|5m|15m|30m|1h|1d, includeEmptyBuckets? }`. Pass a `field` like `programType` (a mapped `text` field is fine — if Elasticsearch rejects terms aggregation on it with "Text fields are not optimised", the query is automatically retried once with `.keyword` appended; the executed `field` is reported back in `data.groups`).
- `metrics`: array of `{ type: count|avg|min|max|sum|cardinality|percentiles, field?, name?, percents? }`. Defaults to `count`.
- Returns: `data.totalMatched`, `data.groups` (as executed), `data.metrics`, `data.rows[]` (each with `keys[]`, `count`, optional `metrics[]`), `data.groupMetadata[]` (per-group bucket counts and accuracy bounds). With a single `terms` group and a data view, each row key carries a `reviewLink` filtering Discover to that value.
- Errors carry `reviewLinks` too: whatever the failure, a Kibana Discover URL for the requested index/target is included so manual investigation can start from the link.

## search_index

Lists live index families.

- `pattern`: index pattern to explore (default `ubs-lottery-*`). `limit`: families to return (up to 2000).
- Returns: `data.totalMatched` (physical indices), `data.totalFamilies`, `data.families[]`, each `{ pattern, description (from the bundled catalog), indices, docsCount, storeSizeBytes?, earliestDate?, latestDate? }`.
- Families are formed by stripping the trailing date segment (`yyyy.MM.dd`) from physical index names; dates set the retention range.
- `reviewLinks` deep-links each include pattern into Discover using the pattern itself as the data-view reference (a management-style link that needs no `.kibana` read access, unlike the saved data-view links), with the picker set to the last 7 days.

## time_series

Count/metric over time buckets.

- `interval`: one of `1m|5m|15m|30m|1h|1d` (required). `splitBy`: `{ field, size? }` optional. `metric`: as in `aggregate_logs` (default count). `includeEmptyBuckets`: bool.
- Returns: `data.interval`, `data.metric`, `data.splitBy?`, `data.points[]` with `bucketStart`/`bucketEnd` (RFC 3339), the metric, and when split, `groups[]`. Each point carries a `reviewLink` into Discover for that bucket window.

## compare_windows

Compares two windows (current vs baseline).

- `current` / `baseline`: two `timeRange`s. `groupBy`: optional field to compare per value. `size`: up to 1000 groups. `metric`: default count. `minCount`: minimum current or baseline to keep a row. `includeMissingBaseline`: include keys missing from baseline (default true).
- Returns `comparisonTimeWindow` (both windows), `data.rows[]` with `key?`, `current`, `baseline`, `delta`, `deltaPct`, `changeType` (`increase` | `decrease` | `new` | `missing` | `same`). Current and baseline windows carry their own nested `reviewLink`.

## search_samples

Page of matching documents with limited `_source`.

- `sourceFields`: default `@timestamp, method, milliseconds, hostname, domainID, details`. `size`: up to 100. `sort`: `[{ field, order }]` (default `@timestamp desc`). `trackTotalHits`: exact total. `searchAfter`: sort values from the previous page for pagination.
- Returns: `data.samples[]` with `index`, `source`, and — when a data view and `_id` are available — a context-view `reviewLink`. `data.totalMatched` (and `totalMatchedRelation`), `data.nextSearchAfter` when a further page exists.

## discover_fields

Field capabilities (`_field_caps`).

- `fieldPattern`: defaults to `*`. `prefixes`: field-name prefixes. `onlyAggregatable` / `onlySearchable`: bool filters. `types`: e.g. `keyword`, `long`. `includeUnconfirmedFields`: include unconfirmed case-variant fields. `limit`: up to 2000.
- Returns: `data.totalMatched`, `data.fields[]`, each with `name`, `types[]`, `searchable`, `aggregatable`, optional `confirmedInSamples`, and per-type `indices` / `nonSearchableIndices` / `nonAggregatableIndices`.

## export_raw_es_response (internal, not registered)

A guarded pass-through to `_search` / `_count` / `_field_caps` that writes the raw JSON response to a temp file and returns its path. `method` is restricted to those three read-only verbs; `search` bodies are further guarded (size ≤ 1000, defaulted to 10). The `[McpServerTool]` attribute is commented out, matching the original project's decision to keep it compiled-but-unregistered; uncomment to expose it.
