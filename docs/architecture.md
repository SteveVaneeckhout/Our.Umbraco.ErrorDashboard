# ErrorDashboard

> How this package works, and why. For getting it running locally see
> [development.md](development.md); for what it does for an editor see the
> [README](https://github.com/SteveVaneeckhout/Our.Umbraco.ErrorDashboard#readme). Paths below are relative to `src/ErrorDashboard/` unless stated otherwise.


Tracks the errors real visitors hit — 404s, 500s, TLS and DNS failures — by asking their browsers to
report them, then shows the result on three dashboards in the Settings section and emails subscribed
users when a site's error volume goes abnormal.

Server logs record what the server *thinks* it served. Network Error Logging moves the observation
point into the browser, so it also catches failures the server never saw: certificate problems,
connection resets, DNS failures, and errors on assets served from an edge or CDN.

## How it works

```
browser  --NEL/Report-To headers-->  NelPolicyMiddleware   (policy on front-end HTML responses)
browser  --POST /nel-report------->  NelPolicyMiddleware   (terminal branch, 204, never blocks)
                                          |
                                     NelReportQueue        (bounded channel, drops oldest)
                                          |
                                     NelReportWriterService (batches every 5s)
                                          |
                                     errorDashboardNelReport
                                          |
                                     ErrorDashboardAggregationJob (hourly)
                                       aggregate -> alert -> prune
                                          |
                       errorDashboardStatHourly + errorDashboardStatUrlDaily
```

## What NEL cannot tell you

These constraints are the package's design, not incidental, and the dashboards state them on their
own faces because otherwise the numbers get misread:

- **Chromium only.** Firefox and Safari send nothing, and neither do bots or non-browser clients.
  Every figure is a lower bound, not a count.
- **`Report-To`, not `Reporting-Endpoints`.** NEL is still bound to the deprecated header; the modern
  Reporting API header does not drive it. Emitting only the modern one collects nothing.
- **HTTPS only**, with a clean certificate. Chrome treats `localhost` as trustworthy, so the dev
  cert works.
- **Reports arrive late** — seconds to minutes, occasionally longer. Never write a test that expects
  one promptly; POST to the collector with curl instead.
- **Soft 404s are invisible.** A page that returns 200 with "not found" text produces no report.
- Most real volume is missing *assets*, not pages. The Error Pages tab says so.

## Design decisions

### The collector is middleware, not a controller

`NelPolicyMiddleware` handles `POST /nel-report` itself and short-circuits. Registered through
`UmbracoPipelineFilter.PrePipeline`, it is the first thing inside `app.UseUmbraco()` — ahead of
static files, routing, authentication and antiforgery. That means the collector needs none of them,
and it sidesteps the fact that no default input formatter binds `application/reports+json`. It also
means the policy headers reach responses Umbraco's routing never produced, 404s included.

Headers are written inside `Response.OnStarting`. As the outermost middleware we do not know the
status code or content type on the way in, and once the response has started they are frozen.

### The endpoint is public, so it is guarded

Anyone can POST to it. In order: a 64 KB body cap checked against `Content-Length` *and* enforced
while reading, at most 100 reports per payload, an in-memory per-IP rate limit (`RateLimitPerMinute`,
default 60 payloads per minute, then 429), and a host check.

**The host check reads Umbraco's own configured domains**, not the request's `Host` header. Comparing
against that header is worthless on its own: ASP.NET Core only rejects an unrecognised `Host` when
`AllowedHosts` is configured, and it is not by default, so a single `curl -H "Host: anything"`
defeats it. `ReportHostValidator` resolves the accepted set from `IDomainService` plus
`ErrorDashboard:AllowedHosts`, cached for five minutes.

When **no** domains are configured — a single-site install, or local development — there is nothing
authoritative to check against, and it falls back to the request host. That fallback is only as
strong as whatever host filtering sits in front of the application, so the Alerts tab shows the
accepted host list and flags the fallback explicitly rather than leaving it implicit.

**Nothing identifying is stored,** with one deliberate exception: the report's `referrer`, which is
the only way to answer "what is still linking to this 404". The body's `server_ip` is *our* address
as the browser resolved it, not the visitor's. Client IP and user-agent are not persisted, and query
strings are stripped from URLs — they are noisy and the most likely place for personal data.

### Writes are buffered

A visitor's request must never wait on the database. Reports go into a bounded `Channel` (10 000,
`DropOldest`) and a `BackgroundService` drains it every 5 seconds or 500 rows. Shedding telemetry
under load always beats stalling a request.

Writes use NPoco's `InsertBatch`, **not** Umbraco's `BulkInsertRecords`. On SQL Server the latter
goes through `SqlBulkCopy`, whose schema reader throws `numericPrecision must be specified for
"float" type columns` on any `double` column — which `samplingFraction` is.

### Aggregation is watermarked, and buckets on when the error happened

The hourly job folds new raw rows into `errorDashboardStatHourly` and `errorDashboardStatUrlDaily`,
tracking progress by row id in `umbracoKeyValue`. Two things that look like details but are not:

- Buckets use `occurredUtc` (receipt minus the report's `age`), never `receivedUtc`. Reports arrive
  late and in batches; bucketing on receipt manufactures a spike every time a batch lands.
- Rows newer than one minute are left for the next run. Identity is assigned at insert but only
  becomes visible at commit, so in a load-balanced setup a lower id can appear after a higher one has
  already been passed.

`errorDashboardStatUrlDaily` is capped at `TopPathsPerDay` distinct paths per day; the tail collapses
into a single `(other)` row. A site being scanned for nonexistent URLs would otherwise add a row per
probe and the summary table would dwarf the raw one.

**Referrers are deliberately not aggregated.** A broken URL can be linked from anywhere, so the
cardinality is unbounded in a way the per-path totals are not — aggregating them would reintroduce
exactly the problem the cap above exists to prevent. The Error Pages tab fetches them on demand from
the raw table instead, which means they are only visible within `RawRetentionDays`; the response
reports the window it could actually cover, and the UI says so when it is shorter than the range the
table is showing.

The consequence is that some rows have nothing worth expanding. The aggregates outlive the reports
they were rolled up from, so a row can carry counts long after the raw rows behind it were pruned;
the `(other)` placeholder never had any of its own; and a URL every visitor reached directly has
reports but no `Referer` header in any of them. `GetOffendingPagesAsync` therefore answers
`hasReferrers` per row — one grouped query over the indexed `urlHash` for the whole page of rows,
deliberately using the same window `GetReferrersAsync` will, so the arrow and the panel behind it
cannot disagree — and the dashboard renders the disclosure arrow only where it leads somewhere. Rows
without one keep an invisible arrow of the same width, so the paths stay aligned down the column.

Within a panel that does open, the no-referrer group is still listed: alongside real referrers,
"and 20 more arrived with no referrer" is part of the answer rather than the absence of one.

### Each path is resolved back to its content node

`GetOffendingPagesAsync` asks `IDocumentUrlService.GetDocumentKeyByUri` what document a reported path
belongs to, so the dashboard can deep-link straight into the workspace. Resolution goes through the
URI rather than a hand-built route string so Umbraco applies its own domain and culture rules.

Published content is checked first, then drafts. **The draft pass is the interesting one:** a 404 on
a path that resolves only as a draft means the page exists but is not live — a different and far more
fixable problem than a link to a page that never existed. Those rows are tagged `Unpublished`.

A path that resolves to nothing stays plain text, which for a genuine 404 is itself the answer.

### The Error Pages table is `uui-table`, not `umb-table`

`umb-table` renders its own rows from a flat item list, so there is nowhere to put a detail row. The
referrer panel has to sit directly beneath the row it belongs to — at 50 rows a page, a panel at the
bottom of the table is not "expanding a row" in any useful sense. Dropping to `uui-table` costs a
hand-rolled sort header and buys inline expansion.

Two things about that expansion are load-bearing.

**A row is identified by host, path, type *and* status code**, not by the URL. The same path appears
once per problem it produced — `/404-test` is both an `http.error` 404 and a `dns.address_changed` —
so keying the open row on the path alone expanded every row sharing that URL at once. Referrers
remain a property of the URL rather than of the problem, so those rows do all show the same list;
they just open one at a time.

**The detail panel is sized against the table container, not the cell it sits in.** `colspan` does
nothing here: browsers only honour it on a real `td`, not on a custom element with `display:
table-cell`, so the detail cell is an ordinary cell in the first column. Under `uui-table`'s default
auto layout that column then widened to fit a panel full of long referrer URLs and squeezed the other
three — the columns jumped on every expand. The panel is therefore two nested elements: a zero-width
outer box, which is what the column calculation sees (a cell whose content has a definite width
contributes exactly that width, and what overflows it is not measured), and inside it the panel
proper at `calc(100cqw - …)`, measured against `.table-container`, which is a query container for
this one purpose. The row still gets its height from the panel, because the overflow is horizontal.

### The anomaly test: median + MAD, not mean + standard deviation

`Alerting/AnomalyDetector.cs` is a pure static class — no I/O, no clock, no DI — because all of the
risk in this feature lives in thirty lines of arithmetic, and this is the only way to exercise them
without running a site for a fortnight and breaking it on purpose.

It computes the modified z-score (Iglewicz–Hoaglin) of the last 24 hours against the preceding 14
complete days:

```
median = Median(baseline)
scale  = 1.4826 x MedianAbsoluteDeviation          // consistent sigma estimate
         -> if 0:  1.2533 x MeanAbsoluteDeviation  // MAD collapses on flat baselines
         -> if 0:  sqrt(median + 1)                // Poisson-ish floor, never zero
score  = (observed - median) / scale

alert iff  baselineDays >= MinBaselineDays
      and  score    >= ScoreThreshold
      and  observed >= MinObserved
      and  observed >= MinRatio x max(median, 1)
      and  no alert for this host within CooldownHours
```

**Why this shape.** The obvious approach — compare error *rate* against a baseline rate — needs a
traffic denominator, which NEL only provides if you also sample successful requests. MAD needs none:
it measures *this site's own* day-to-day variability, so a busy site with a noisy error baseline gets
a wide band and a quiet site gets a narrow one, with no per-site tuning.

A mean and standard deviation would not work here. Both are dragged by a single bad day, so one
outage teaches the detector to ignore the next one. The median is what makes the overlap between the
rolling 24h window and yesterday's baseline day tolerable too — one contaminated day in fourteen
barely moves it.

The three fallbacks on `scale` are not defensive padding. A healthy site has fourteen identical days,
which makes MAD exactly zero; without a floor the score is infinite and every blip alerts.

Worked behaviour, all covered by tests in `ErrorDashboard.Tests`:

| Situation | median | observed | fires? |
| --- | --- | --- | --- |
| Busy site, +100 on a 10 000/day baseline | 10 000 | 10 100 | no |
| Quiet site, 50 errors on a 0–2/day baseline | 1 | 50 | **yes** |
| Quiet site, random blip | 0 | 3 | no — below `MinObserved` |
| Quiet site, genuinely broken | 0 | 60 | **yes** |
| Steady site, +50% | 20 | 30 | no — below `MinRatio` |

**Known limitation:** a count-based test also fires when *traffic* doubles, not only when the site
breaks. Setting `SuccessFraction` above zero makes Chromium sample successful requests too, which
gives `estimatedRequests` a real value and would let the same detector run on error *rate* instead.
The columns are already there; only the detector's input would change, not the schema.

### Alerts are per host, subscriptions are self-service

Each host is judged against its own history — a quiet staging hostname has nothing to say about a
busy public one's normal. Users opt in from the toggle on the Errors tab; there is no way for one
user to sign another up for email they did not ask for.

`IEmailSender.SendAsync` **silently does nothing** — a debug log, no exception — when neither
`Smtp:Host` nor `Smtp:PickupDirectoryLocation` is configured, so the code checks
`CanSendRequiredEmail()` first and the dashboard shows a warning. Without that, alerts would vanish
without trace.

An alert row is written even when the email fails. The dashboard history is the fallback channel;
losing it as well would leave no evidence anything happened.

### Prose is a UI concern, so the API sends data

Three response fields used to carry finished English sentences: the anomaly `explanation`, the
recompute `summary`, and a `deliveryWarning` next to `canReceiveEmail`. All three are gone. Every one
of them already sat beside the structured values it was built from — `rowsAggregated`/`rowsPruned`,
`canReceiveEmail`, `observed`/`median`/`score` — so composing the sentence on the server only threw
away the ability to say it in another language, and forced current-culture number formatting on a
reader who might not use it.

`AnomalyResult.Explanation` is now an `AnomalyExplanation` enum naming the gate that decided the
verdict, plus `ExplanationArgs`, the raw figures behind it. The dashboard maps the enum onto a
dictionary key through a `Record<AnomalyExplanation, string>`, so **adding a gate on the server is a
TypeScript error until it has a translation** — the compiler enforces what a convention would not.

Logs are the exception and stay fixed English: `AnomalyResult.ToLogString()` is invariant-culture, on
purpose. A log line whose decimal separator depended on whichever user happened to trigger the job
would be miserable to search.

## Configuration

Everything has a working default; the section is optional.

```jsonc
"ErrorDashboard": {
  "Enabled": true,
  "ReportPath": "/nel-report",
  "MaxAgeSeconds": 2592000,        // how long the browser caches the policy
  "IncludeSubdomains": false,
  "FailureFraction": 1.0,          // share of failed requests reported
  "SuccessFraction": 0.0,          // >0 also samples successes, giving a traffic denominator
  "AllowedHosts": [],              // extra hosts whose reports are accepted; own host always is
  "MaxPayloadBytes": 65536,
  "MaxReportsPerPayload": 100,
  "RateLimitPerMinute": 60,
  "RawRetentionDays": 21,          // must exceed Alerts.BaselineDays
  "UrlStatRetentionDays": 90,
  "TopPathsPerDay": 200,
  "Alerts": {
    "Enabled": true,
    "BaselineDays": 14,
    "MinBaselineDays": 7,
    "ScoreThreshold": 3.5,
    "MinObserved": 10,
    "MinRatio": 2.0,
    "CooldownHours": 24
  }
}
```

Email needs `Umbraco:CMS:Global:Smtp` with a `From` address and either a `Host` or a
`PickupDirectoryLocation`. This repo's Development config uses a pickup directory
(`Cms/umbraco/Data/MailPickup`), which writes each message as a `.eml` file — **the directory must
already exist**, or `File.Open(..., FileMode.CreateNew)` throws.

## Tables

Created by `ErrorDashboardMigrationPlan`, a `PackageMigrationPlan` that is auto-discovered and runs
on boot. The gate is `Umbraco:CMS:Unattended:PackageMigrationsUnattended` (default true) — **not**
anything under `Umbraco:CMS:PackageMigration:`. If an operator has turned it off, the tables are
never created and the package silently collects nothing.

Umbraco 18 runs package migrations in a background service *after* Kestrel starts, so on the first
boot after installing this package the collector is already accepting POSTs while the tables do not
yet exist. Everything touching the database guards on `IRuntimeState.Level == RuntimeLevel.Run`.

| Table | Holds |
| --- | --- |
| `errorDashboardNelReport` | Raw reports. Pruned after `RawRetentionDays`, on receipt time. |
| `errorDashboardStatHourly` | Errors per hour/host/type/status. Drives the charts *and* the alert baseline; daily figures are 24 of these summed. |
| `errorDashboardStatUrlDaily` | Per-URL daily breakdown, capped at `TopPathsPerDay`. |
| `errorDashboardSubscription` | Who wants alert emails, keyed on user Guid. |
| `errorDashboardAlert` | Alert history; doubles as the cooldown ledger. |

`NVARCHAR(2048)` cannot carry a nonclustered index (SQL Server's 1700-byte key limit), which is why
`urlHash` and `pathHash` exist and `url`/`path` are never indexed.

### Nothing here assumes a provider

The package runs on SQL Server and on SQLite, and the queries are hand-written, so both halves of
that have to be deliberate. Identifiers go through `ISqlSyntaxProvider`
(`GetQuotedTableName`/`GetQuotedColumnName`) rather than being typed with brackets or quotes, and
**paging goes through NPoco** — `Database.SkipTake<T>(skip, take, …)`, never a `TOP (n)` or
`OFFSET … ROWS FETCH NEXT … ROWS ONLY` written into the SQL string. NPoco renders the right keywords
per provider: `OFFSET/FETCH` for `SqlServer2012DatabaseType`, `LIMIT/OFFSET` for
`SQLiteDatabaseType`. The `ORDER BY` has to stay in the statement either way, because SQL Server's
paging clause requires one.

`SkipTake` rather than `Page<T>` in every case: each of these reads already counts its own total, and
`GetOffendingPagesAsync` counts *groups* through a subquery, where NPoco's generated count would
count the rows going into the `GROUP BY` and overstate the total on every page.

The DTO attributes are provider-neutral by the same token. `[SpecialDbType(NVARCHARMAX)]` on
`topPathsJson` reads as SQL-Server-flavoured but is a size hint Umbraco's syntax providers each map
themselves — SQLite renders it as `TEXT`, and the `double` columns as `REAL`.

## API

All under `/umbraco/errordashboard/api/v1/`, requiring Settings section access. Browsable at
`/umbraco/openapi` under the `errordashboard` document.

| Route | Notes |
| --- | --- |
| `GET overview?days&host` | Series, breakdowns, anomaly verdict |
| `GET pages?days&host&type&statusCode&skip&take&orderBy&direction` | Offending URLs |
| `GET referrers?host&path&days&take` | What still links to one broken URL |
| `GET reports?host&skip&take` | Raw rows, for confirming the pipeline works |
| `GET hosts` | Distinct hosts |
| `GET settings` | Effective configuration |
| `GET alerts?skip&take` | Alert history |
| `GET` / `PUT subscription` | Caller's own opt-in |
| `GET subscribers` | Admin only |
| `POST recompute` | Admin only; runs the hourly job now |

Never declare a 401 `ProducesResponseType` on any action — Umbraco's
`BackOfficeSecurityRequirementsTransformer` adds one to every operation and a second throws while the
document is generated.

## Working on it

```bash
cd ErrorDashboard/Client
npm install            # NOT --legacy-peer-deps
npm run build          # or: npm run watch
npm run generate-client   # regenerate src/api from the live OpenAPI doc (site must be running)
```

Bump `version` in `Client/public/umbraco-package.json` on every client change — it is the
`?umb__rnd=` cache-buster. Static web assets are baked at build time, so after `npm run build` you
must rebuild and restart `Cms` before the new chunks are served. The running site locks
`ErrorDashboard.dll`; stop it first.

### Translating

Text lives in two places, because it is delivered two different ways.

**The dashboards** — `Client/src/lang/`. `en.ts` is the source of truth; every other language is a
copy of it with the values translated, registered by a `type: "localization"` manifest in
`lang/manifest.ts`. Adding a language is those two edits and nothing else. English (`en`) is
Umbraco's default culture, so it is loaded whatever the editor's language is and acts as the per-key
fallback; never translate `en.ts` in place, and never give it a regional culture like `en-us`, which
would *not* be loaded alongside another language and would break the fallback.

Two guards keep a translation honest, and both fail the build rather than the UI:

- Every non-English file is typed `Translation<ErrorDashboardLocalizations>`, so a missing key is a
  `tsc` error instead of a string that silently renders in English.
- `npm run build` runs `scripts/check-lang.mjs` first, which fails if anything references an
  `errorDashboard*` key that `en.ts` does not define. `tsc` cannot catch that, because
  `localize.term()` takes a plain string.

Three details specific to this package:

- **Numbers go through `this.localize.number()`**, not `toFixed`/`toLocaleString`. These dashboards
  are mostly figures, and a Dutch reader expecting `4,7` who gets `4.7` reads the page as broken
  rather than as untranslated. Dates likewise: `utils/format.ts` takes the controller, and
  `renderDailyChart` formats its axis with it.
- **Entries containing markup** (`linkingTo`, `alertBody`, and the two `<code>` notes) are rendered
  with `localize.htmlString()`, which escapes the arguments before interpolating. Everything else
  goes through `term()` and is escaped by Lit. A translation may move a placeholder within the
  sentence — that is the point of keeping the markup in the dictionary — but should keep the tags
  around it.
- **`(other)` is data, not text.** The aggregator stores that literal for a day's folded-up long
  tail, so it is in the database and in the API response; `pages.element.ts` translates it only on
  the way to the screen.

**The alert email** — `Resources/AlertEmailResources.resx`, plus one
`AlertEmailResources.<culture>.resx` per language, which .NET builds into a satellite assembly. This
one cannot live in the client dictionary: there is no browser. `AlertService` groups subscribers by
the culture resolved from their `IUser.Language` and sends one message per language, formatting every
figure for that culture too. Grouping on the resolved `CultureInfo` rather than the raw string folds
`nl` and `nl-NL` together, so nobody gets the same mail twice.

`dotnet test ErrorDashboard.Tests` covers the anomaly detector and the collector's validation rules.
Both are pure functions, and they carry all the logic that is easy to get quietly wrong.

To exercise the collector without waiting for a browser:

```bash
curl -sk -X POST https://localhost:44366/nel-report \
  -H "Content-Type: application/reports+json" \
  -d '[{"age":20,"type":"network-error","url":"https://localhost:44366/missing-page","body":{"sampling_fraction":1.0,"elapsed_time":823,"method":"GET","phase":"application","protocol":"http/1.1","referrer":"","server_ip":"127.0.0.1","status_code":404,"type":"http.error"}}]'
```

Then `POST /umbraco/errordashboard/api/v1/recompute` — but note aggregation ignores rows younger than
a minute, so an immediate recompute reports zero. That is the commit-settle guard, not a bug.
