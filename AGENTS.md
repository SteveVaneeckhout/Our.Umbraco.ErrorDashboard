# AGENTS.md

Notes for anyone - human or otherwise - working in this repository. Everything here was paid for
once already; none of it is guesswork.

## What this is

`Our.Umbraco.ErrorDashboard`: an Umbraco 17 LTS backoffice package, browser-reported 404/500/TLS errors (Network Error Logging) with anomaly alerting. It is published to NuGet and
listed on the Umbraco Marketplace, so the public surface and the README are part of the product.

```
src/Cms/            the Umbraco host. A development harness, never shipped (IsPackable=false).
src/ErrorDashboard/ the package
src/ErrorDashboard.Tests/ MSTest
scripts/            tooling outside the build
docs/               architecture, development, upgrading
```

## Branches

`main` follows the latest Umbraco. Each supported LTS gets a long-lived `v<major>/main` branch -
currently `v17/main`, for Umbraco 17 - and the **package major matches the Umbraco major** (18.x
from `main`, 17.x from `v17/main`), all under the one NuGet ID.

- Work on an LTS branch in its **own worktree**
  (`git worktree add ../Our.Umbraco.ErrorDashboard-v17 v17/main`). The SQLite database,
  `node_modules`, `wwwroot` and `bin/obj` are gitignored, so a plain checkout would share them, and
  an older Umbraco cannot boot on a database the newer one has migrated. Both sites use port 44366,
  so only one runs at a time.
- **Release order matters.** `dotnet add package` takes the highest stable version and NuGet only
  *warns* (NU1608) when its dependencies do not fit, so an LTS release must never be the highest
  version on NuGet - `main` must always have released a higher major first. Publish LTS GitHub
  Releases with *Set as latest release* unticked.
- A fix that applies to both lines is **cherry-picked** across. Never merge the branches into each
  other: the port itself would come along with the fix.

**This is the `v17/main` branch.** What differs from `main` is deliberately small: the version pins,
the OpenAPI composer (Umbraco 17 still uses Swashbuckle - see *OpenAPI* below), the regenerated
`Client/src/api`, one `IEmailSender.SendAsync` call, the uSync folder name, and the docs. The
services, the controllers and the client source are otherwise the same code.

Inside `src/ErrorDashboard/`:

```
  Alerting/     AlertService and the anomaly detector
  Composers/    DI registration and the package's OpenAPI document
  Configuration/ ErrorDashboardOptions - the whole config surface
  Controllers/  the backoffice API
  Jobs/         the hourly aggregation job
  Middleware/   the NEL policy headers and the collector endpoint
  Migrations/   the package migration that creates five tables
  Persistence/  NPoco DTOs
  Reporting/    parsing, host validation, the write queue
  Services/     stats, subscriptions
  Resources/    .resx for the alert email (see Translating)
  Client/       TypeScript + Vite source for the backoffice UI
    src/lang/   the UI dictionaries, one file per language
  wwwroot/      Vite output, served at /App_Plugins/ErrorDashboard (generated, NOT committed)
```

## Running it

```bash
dotnet run --project src/Cms
```

Backoffice at **https://localhost:44366/umbraco**, admin `hello@example.com`, password in
`src/Cms/appsettings.json`. The configured application URL is
`https://testsite1.127.0.0.1.nip.io:44366/`, which is the host the single uSync domain binds to.
Everything secret in this repository is deliberately public - it is a throwaway local harness.

On first boot the site creates a SQLite database, installs unattended, and imports `src/Cms/uSync/v17`.

**A running site holds the package DLL open**, so stop it before `dotnet build`:
`Get-Process -Name Cms | Stop-Process -Force`.

## Build, test, format

```bash
dotnet build Our.Umbraco.ErrorDashboard.slnx -p:BuildClient=false
dotnet test --solution Our.Umbraco.ErrorDashboard.slnx
dotnet format Our.Umbraco.ErrorDashboard.slnx --verify-no-changes
```

- **Tests are MSTest on Microsoft.Testing.Platform.** The .NET 10 SDK refuses to run MTP projects
  through the legacy VSTest target, so `global.json` carries
  `"test": { "runner": "Microsoft.Testing.Platform" }`. It is **not** `dotnet.config`, which looks
  plausible and does nothing. In this mode a project is `dotnet test --project <path>`, not
  positional, and `dotnet test A.csproj B.csproj` is rejected outright.
- Porting assertions from xUnit: `Assert.AreEqual(expected, actual, x)` takes a **delta**, where
  xUnit's third argument was a *decimal-place count* - carrying a `10` across turns a tight assertion
  into "within ±10". `Assert.AreEqual` compares collections by **reference**; use
  `CollectionAssert.AreEqual`. `Assert.Contains`/`DoesNotContain` do keep xUnit's
  `(substring, value)` order, unlike `StringAssert.Contains(value, substring)`.
- `dotnet format --verify-no-changes` is a CI gate. `.gitattributes` normalises the working tree to
  LF; without that it fails on line endings alone.
- `-p:BuildClient=false` skips the MSBuild target that shells out to npm. **That target only fires
  when the bundle is missing**, so it will happily reuse a stale `wwwroot` - which is exactly how
  source maps once shipped inside a release package. CI builds the client explicitly, then passes
  this flag everywhere.

## Dependencies

This package depends on nothing that is not published by **Microsoft or Umbraco**, and that is a
deliberate constraint, not an accident. Before adding a package, check whether the .NET SDK or
Umbraco already covers it. SourceLink, for instance, needs no PackageReference - it is in the SDK.

## Namespaces

The root namespace is `Our.Umbraco.ErrorDashboard`, which **shadows the global `Umbraco` namespace**. Inside
it, a fully-qualified `Umbraco.Cms.Core.Constants` resolves `Umbraco` to `Our.Umbraco` and fails
with `CS0246: The type or namespace name 'Cms' does not exist in the namespace 'Our.Umbraco'`. The
same applies to XML `cref` attributes, which fail as `CS1574`.

Use a file-scoped alias - `using UmbConstants = Umbraco.Cms.Core.Constants;` - which sits outside the
namespace and resolves globally. These references are usually fully qualified in the first place
because each package declares its own `Constants` class that shadows Umbraco's, so the alias fixes
both problems at once. For crefs, import the namespace and use the simple type name.

Deliberately **not** renamed, and not to be renamed: `App_Plugins/ErrorDashboard`, the bundle aliases,
`umbraco-package.json`'s `id`, and `Constants.ApiName` (`"errordashboard"`, which is both the OpenAPI
document name and the route prefix `/umbraco/errordashboard/api/v1/…`).

## Client workflow

```bash
cd src/ErrorDashboard/Client
npm install            # NOT --legacy-peer-deps
npm run build          # or: npm run watch
npm run generate-client   # regenerate src/api from the live OpenAPI doc (site must be running)
```

- **`npm install`, never `--legacy-peer-deps`.** Umbraco's docs suggest that flag, but it skips the
  peers (`lit`, `@umbraco-ui/uui`, `rxjs`) the TypeScript build needs for types. Without them you
  get a wall of "module has no exported member" errors.
- **Bump `version` in `Client/public/umbraco-package.json` on every client change.** Umbraco uses it
  as the cache-buster (`?umb__rnd=`); leave it and the browser serves the old bundle.
- **Static web assets are baked at build time.** After `npm run build`, new chunks are only served
  once the site is rebuilt and restarted.

Import rules: templating from `@umbraco-cms/backoffice/external/lit` (`@umbraco-cms/backoffice/lit`
does not exist; bare `lit` bundles a second copy and breaks reactive-element identity), elements
extend `UmbLitElement` from `@umbraco-cms/backoffice/lit-element`, and
`rollupOptions.external: [/^@umbraco/]` must stay - the backoffice resolves those specifiers through
the import map it serves at `/umbraco/backoffice/umbraco-package.json`.

## Localization

**No user-facing string belongs in a template.** Text comes from `Client/src/lang/en.ts` via
`this.localize.term(...)`, manifest labels are `"#area_key"`, and dates and numbers go through
`this.localize.date/number/relativeTime` - plain `Intl` follows the *browser* language, not the
backoffice one.

Two guards fail the build rather than the UI: `npm run build` runs `scripts/check-lang.mjs`, which
rejects a key `en.ts` does not define, and every non-English file is typed
`Translation<ErrorDashboardLocalizations>` so a missing key is a `tsc` error. Never translate `en.ts` in
place, and never give it a regional culture like `en-us` - `en` is Umbraco's default and acts as the
per-key fallback.

The `.resx` files in `src/ErrorDashboard/Resources` cover the one surface a browser cannot localize.

## OpenAPI

**Umbraco 17 generates OpenAPI with Swashbuckle**; Umbraco 18 replaced it with
Microsoft.AspNetCore.OpenApi, which is why `ErrorDashboardApiComposer` is the one file that really
differs between the branches. The document is at `/umbraco/swagger/errordashboard/swagger.json`
(OpenAPI 3.0), not `/umbraco/openapi/errordashboard.json`.

- **Operation IDs are named HTTP method + route** (`GetAlerts`, `PutSubscription`) by
  `ErrorDashboardOperationIdHandler`. hey-api derives the client's function names from them, and
  that scheme reproduces exactly the names `main`'s generator gives, so `Client/src` needs no
  changes between the branches. Note it is the **route**, not the action: `UpdateSubscription` is
  `PUT subscription`, and `main` calls it `putSubscription`. ContentDashboard's v17 handler uses the
  action name, which would rename it `putUpdateSubscription` here - do not copy that one across.
- **Swashbuckle is used transitively**, through `Umbraco.Cms.Api.Management`. The 17 extension
  template references `Swashbuckle.AspNetCore` directly; this package does not, because of the
  Microsoft-or-Umbraco dependency rule above.
- Swashbuckle marks request bodies optional, so the regenerated client types `body?:` rather than
  `body:`. Harmless, but it is why `src/api` differs from `main`'s.
- The explicit `[ProducesResponseType(StatusCodes.Status403Forbidden)]` on `Subscribers` and
  `Recompute` does **not** break the document on 17 (verified on 17.7.0). On `main` the `401`/`403`
  duplicate-key trap is real; see that branch's AGENTS.md before copying such an attribute across.

## What matters in this package

Read `docs/architecture.md` before changing anything about collection or the alert maths. **The
statistics are not arbitrary.** In particular:

- The anomaly test uses **median + median absolute deviation**, not mean + standard deviation: a mean
  is dragged around by exactly the spikes it is meant to detect. There is a Poisson-ish floor of
  `sqrt(median + 1)` so a flat baseline does not produce an infinite score.
- Several gates suppress alerts nobody wants - minimum baseline length, `MinObserved`, `MinRatio`,
  and a per-host cooldown. Three errors after a fortnight of none is a statistical mountain and an
  operational non-event.
- **Aggregation is watermarked** on `errorDashboardNelReport.id`, stored in `umbracoKeyValue` under
  `ErrorDashboard.AggregationWatermark` as `"<id>|<timestamp>"`. Anything that inserts raw reports
  *and* hourly rows must move the watermark too, or the next hourly run double-counts.
- Buckets are keyed on **when the error happened**, not when the report arrived. Reports land minutes
  late, and bucketing on receipt would manufacture a spike whenever a batch arrives.
- **Never write a paging clause into a SQL string.** `TOP (n)` and
  `OFFSET … ROWS FETCH NEXT … ROWS ONLY` are T-SQL only and look perfectly at home surrounded by
  `GetQuotedTableName` calls - which is why they survive review and then fail on SQLite with
  `SQLite Error 1: 'near "FROM": syntax error'`. Use `Database.SkipTake<T>`, keep the `ORDER BY`,
  and do not renumber `@0…@n` (NPoco appends its own parameters after yours). Prefer `SkipTake` over
  `Page<T>` wherever the method computes its own total, and *always* over a `GROUP BY` whose total
  counts groups: NPoco's generated count counts the pre-group rows.
- **`BulkInsertRecords` cannot write `double` columns on SQL Server** - `SqlBulkCopy`'s schema
  reader throws `numericPrecision must be specified for "float" type columns`. Use NPoco's
  `InsertBatch`.
- **Recurring jobs derive from `RecurringBackgroundJobBase`**, not `IRecurringBackgroundJob`, and
  are registered as **singletons** - so no scoped dependencies.
- **`IEmailSender.SendAsync` silently no-ops** when neither `Smtp:Host` nor
  `Smtp:PickupDirectoryLocation` is set: a debug log, no exception. Call `CanSendRequiredEmail()`
  first. The dev pickup directory must already exist or `File.Open` throws.
- **`IUserService.GetAllAsync` needs a performing user** and permission-filters the result, so it is
  the wrong call from a background job. Use `GetAsync(IEnumerable<Guid> keys)`.
- **An unrecognised `Host` header is not rejected by default.** ASP.NET Core only filters it when
  `AllowedHosts` is configured, and nothing here configures it - so anything trusting
  `Request.Host` for an authorization-shaped decision is trusting the caller. Use Umbraco's
  `IDomainService` domains, as `Reporting/ReportHostValidator.cs` does.
- **`colspan` does nothing on `uui-table-cell`.** Browsers apply it to `td`/`th` only, never to a
  custom element with `display: table-cell` - so a "full width" detail row is really an ordinary
  cell in the first column, and auto table layout widens that column to fit it. See the referrer
  panel in `Client/src/dashboards/pages.element.ts`.
- **`uui-select` cannot display an empty-string value.** An option with `value: ""` leaves the
  control looking blank once selected. Use a non-empty sentinel - `ANY_OPTION` in
  `Client/src/dashboards/shared.ts`.
- Package migrations are gated on `Umbraco:CMS:Unattended:PackageMigrationsUnattended`, **not**
  anything under `Umbraco:CMS:PackageMigration:`, and Umbraco 17 (like 18) runs them in a
  background service **after** Kestrel starts - so middleware is live while the tables do not yet
  exist. Guard database work on `IRuntimeState.Level == RuntimeLevel.Run`.
- **Migrations derive from `AsyncMigrationBase`.** Umbraco 17 still has the synchronous
  `MigrationBase`, but 18 removed it, so use the async base to keep the code identical to `main`:
  override `MigrateAsync()`, and use `SqlSyntax.DoesTableExist(Context.Database, name)` - there is
  no `TableExists` helper.
- **Call `IEmailSender.SendAsync` with `expires` spelled out.** On 17 the two-argument
  `SendAsync(message, emailType)` binds to an overload marked obsolete (CS0618); 18 has only the
  four-parameter one, so naming `enableNotification` and `expires` compiles cleanly on both.

## The fixture

The dashboards are empty until seeded, and **cannot** be filled by browsing: the collector
rejects the fixture's host (`ReportHostValidator` accepts only Umbraco's configured domains), and
NEL is Chromium-only and late. Run `scripts/seed-error-fixture/seed.cs` with the site stopped. It
rebases every timestamp so the fixture is always recent, and moves the aggregation watermark past it.
Rows sit under **seeded.example** so they can never collide with genuine local reports.

`src/Cms/uSync/v17` was re-exported from scratch, so it contains no delete tombstones and matches the
database exactly. A plain uSync Export **adds and updates files but never removes stale ones**, which
is how a domain pointing at a long-deleted node survived in the original export - empty the folder
first if you want a clean one.

## Verifying a change

The Chrome extension is not installed here. Use the Playwright browsers from another project:

```bash
PLAYWRIGHT_BROWSERS_PATH="C:/Users/zippy/AppData/Local/ms-playwright" node script.mjs
```

Install `playwright` (the library only) into a scratch directory with
`PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`. Playwright's CSS engine **pierces open shadow roots**, so
ordinary selectors work against the backoffice. Log in at `#username-input` (type `text`, not
`email`) and `#password-input`, submit `#umb-login-button`, and wait for the form explicitly -
`isVisible()` does not wait and returns false before the page has rendered.

To call the API from a logged-in Playwright page without an API user, `fetch` from the page with
the header `Authorization: Bearer [redacted]` - literally that. The backoffice keeps its tokens in
HttpOnly cookies (`__Host-umbAccessToken`) and sends that placeholder, which the server swaps for
the cookie; a `fetch` without it gets a 401. Verified on 17.7.0.

For server-side checks, get a token with the `.env` client credentials against
`POST /umbraco/management/api/v1/security/back-office/token` (`grant_type=client_credentials`).
**A fresh clone has no API user** - uSync does not export users, so create one in the backoffice
first. **Restart the site afterwards.** On 17.7.0 with SQLite, creating an API user and its client
credentials through the Management API left the database locked: the user was saved, but every
later query - OpenIddict lookups, cache sync, server registration - timed out after 30 seconds,
until a restart cleared it. Seen once, while porting; the same package calls, repeated on a fresh
process without creating a user, ran cleanly.

For database assertions the site runs on SQLite and there is no `sqlite3` CLI on this machine. Use a
file-based C# script - `dotnet run q.cs` with `#:package Microsoft.Data.Sqlite@10.0.10` on the first
line - and **stop the site first**, because it holds the file and a WAL. Note that file-based apps
disable reflection-based JSON by default; add
`#:property JsonSerializerIsReflectionEnabledByDefault=true` if you need it. Do not put such a script
under `src/Cms/`: the SDK globs it into the project and the build fails with
`CS9298: '#:' directives can be only used in file-based programs`.

**The Bash tool mangles backslashes and can inject control characters into heredocs.** `\\`
collapses to `\` even inside a quoted heredoc. Anything with awkward escaping should go through a
file write instead. Use forward slashes for Windows paths - .NET accepts them.

## Releasing

Version lives in `Directory.Build.props`. A published GitHub Release whose tag is the version
(`v1.0.0`, or `v1.0.0-rc.1` marked pre-release) triggers `.github/workflows/release.yml`, which
packs with `-p:Version=` from the tag and pushes to NuGet using **trusted publishing** - an OIDC
exchange, no API key. The nuget.org policy is keyed on the workflow **file name**, so `release.yml`
must not be renamed. Bump `Client/public/umbraco-package.json` too, and add to `CHANGELOG.md`.
