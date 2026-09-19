# Development

Everything you need to run this package locally, change it, and verify the change.

The repository contains the package itself plus a complete Umbraco site to run it in. That site is a
development harness — it is never shipped, and `IsPackable` is `false` on it.

```
src/Cms/            the Umbraco host: content, templates, uSync export, dev certificate
src/ErrorDashboard/  the package
src/ErrorDashboard.Tests/  its tests
scripts/            tooling that is not part of the build
docs/               these documents
```

## Prerequisites

- **.NET 10 SDK**
- **Node 22+** (the backoffice client is TypeScript built with Vite)

Nothing else. The site uses SQLite, created on first run.

## First run

```bash
git clone https://github.com/SteveVaneeckhout/Our.Umbraco.ErrorDashboard.git
cd Our.Umbraco.ErrorDashboard
dotnet run --project src/Cms
```

The first build takes a while: it restores NuGet packages and runs `npm ci` + `npm run build` for
the backoffice client, because `wwwroot/` is generated and not committed.

On first boot the site, unattended:

1. creates `src/Cms/umbraco/Data/Umbraco.sqlite.db`,
2. installs Umbraco and the admin user,
3. runs the package migration that creates the five `errorDashboard*` tables,
4. and imports every document type, data type, template and document from `src/Cms/uSync/v18`
   (`uSync:Settings:ImportOnFirstBoot`).

Then open **https://localhost:44366/umbraco** and sign in:

| | |
| --- | --- |
| Email | `hello@example.com` |
| Password | `YVXVRvFlmUfwdGVWdprZWiZDKpwKiXfc` |

Those credentials, and the certificate password below, are in `src/Cms/appsettings.json` on purpose.
This is a throwaway local harness with no data worth protecting — treat every secret in this
repository as public, because it is.

Open the **Settings** section: the *Errors*, *Error Pages* and *Error Alerts* tabs are the package.

## The demo content

The demo content is a theme-park site, "Coaster World": 67 documents and one domain.

**The dashboards will be empty until you seed them**, and you cannot fill them by browsing the site:
Network Error Logging only reports from Chromium, only over HTTPS, and reports arrive minutes late.
So there is a fixture:

```bash
cd scripts/seed-error-fixture
dotnet run seed.cs              # seed (replaces any existing fixture rows)
dotnet run seed.cs -- --clear   # remove it again
```

**Stop the site first** — it holds the SQLite file and its write-ahead log.

That gives you a fortnight of quiet baseline days, a spike on the most recent day across 25 distinct
paths, 25 alert rows and 60 raw reports carrying referrers. Every timestamp is rebased on each run,
so the fixture is always recent rather than slowly falling off the end of the charts, and the
aggregation watermark is moved past it so the hourly job does not roll the same rows up twice.

The rows sit under the host **seeded.example**, which the collector itself *rejects* —
`ReportHostValidator` only accepts Umbraco's own configured domains — so they can never collide
with genuine reports from your local site, and can only be written to the database directly.

### Collecting a real report

Reports only come from Chromium, over HTTPS, with a valid certificate. The quickest honest check is
to POST to the collector yourself rather than wait for a browser:

```bash
curl -k -X POST https://localhost:44366/nel-report \
  -H 'Content-Type: application/reports+json' \
  -d '[{"age":0,"type":"network-error","url":"https://testsite1.127.0.0.1.nip.io:44366/nope",
        "body":{"type":"http.error","status_code":404,"phase":"application","method":"GET"}}]'
```

The collector answers `204` whatever happens — it must never block a browser — so check the log
or the database to see whether the report was accepted.
## Three optional steps

None of these are needed to run the site; each removes a specific annoyance.

### 1. Trust the development certificate

The site is served over HTTPS using a self-signed wildcard certificate for `*.127.0.0.1.nip.io`
(committed at `src/Cms/ssl/nip-io-wildcard.pfx` — without it Kestrel will not start). Your browser
will warn about it until you trust it:

```powershell
# From an elevated PowerShell prompt
./src/Cms/ssl/Install-NipIoCertificate.ps1
```

The script adds only the public certificate to `LocalMachine\Root`, never the private key, and
`-Uninstall` removes it again. Firefox needs `security.enterprise_roots.enabled` set to true to
honour the machine store. `New-NipIoCertificate.ps1` regenerates the certificate if you would rather
not use the committed one.

Why `nip.io` rather than `localhost`: it resolves any `*.127.0.0.1.nip.io` name to loopback, which
gives the harness real hostnames — and this package's host validation and per-host alerting only mean anything with a real domain.

### 2. Set up the Umbraco MCP server

`.mcp.json` configures `@umbraco-cms/mcp-dev`, which lets an AI assistant read and change content
through Umbraco's management API. It needs credentials, and **a fresh clone has none** — uSync does
not export users, so the API user does not exist in your new database.

1. In the backoffice, go to **Users → API Users** and create one with access to the Content section.
2. Open its profile and copy the client id and secret.
3. `cp .env.example .env` and fill them in. `.env` is gitignored.

`scripts/verify-upgrade.mjs` reads the same file for its live checks.

### 3. Work on the backoffice client

```bash
cd src/ErrorDashboard/Client
npm install          # NOT --legacy-peer-deps
npm run watch        # rebuild on change
npm run build        # one-off
npm run generate-client   # regenerate src/api from the live OpenAPI document
```

Four things that will otherwise cost you an afternoon:

- **Never `npm install --legacy-peer-deps`.** Umbraco's own docs suggest it, but it skips the peer
  dependencies (`lit`, `@umbraco-ui/uui`, `rxjs`) that the TypeScript build needs for types. Without
  them you get a wall of "module has no exported member" errors.
- **Bump `version` in `Client/public/umbraco-package.json` whenever you ship a client change.**
  Umbraco uses it as the cache-busting key (`?umb__rnd=`); leave it alone and browsers keep serving
  the old bundle.
- **Static web assets are baked at build time.** After `npm run build`, the new chunks are only
  served once you rebuild and restart the site.
- **Stop the site before `dotnet build`.** A running site holds the package DLL open and the copy
  step fails: `Get-Process -Name Cms | Stop-Process -Force`.

`npm run generate-client` reads
`https://localhost:44366/umbraco/openapi/errordashboard.json`, so the site has to be running. Run it
whenever a controller signature or view model changes.

## Building and testing

```bash
dotnet build Our.Umbraco.ErrorDashboard.slnx
dotnet test --solution Our.Umbraco.ErrorDashboard.slnx
dotnet format Our.Umbraco.ErrorDashboard.slnx --verify-no-changes
```

Three things about this that are not obvious:

- Tests are **MSTest** on **Microsoft.Testing.Platform**. The .NET 10 SDK refuses to run MTP
  projects through the old VSTest target, so `global.json` opts in with
  `"test": { "runner": "Microsoft.Testing.Platform" }`. In this mode a project is passed as
  `dotnet test --project <path>`, not positionally.
- `dotnet format --verify-no-changes` is enforced by CI, so run it before opening a pull request.
  `.gitattributes` normalises the working tree to LF; without that the check fails on line endings
  alone.
- `-p:BuildClient=false` skips the MSBuild target that shells out to npm. CI passes it everywhere and
  builds the client explicitly first, because that target only fires when the bundle is *missing* and
  will happily reuse a stale one.

## Checking an Umbraco upgrade

```bash
node scripts/verify-upgrade.mjs              # static checks plus live probes
node scripts/verify-upgrade.mjs --static-only
```

It checks that the version pins agree across the host, the package and the client, then — with the
site running — that the OpenAPI document generates, the App_Plugins bundle is served, and every
parameterless endpoint answers. [upgrading.md](upgrading.md) covers the half that needs judgement.

## Contributing

Open an issue before a large change, so we can agree the shape of it first. For anything smaller:
fork, branch, make sure `dotnet build`, `dotnet format --verify-no-changes` and `dotnet test` all pass,
and open a pull request. CI runs exactly those.

New dependencies are worth a conversation: this package deliberately depends on nothing that is not
published by Microsoft or Umbraco.
