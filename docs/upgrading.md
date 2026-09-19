# Upgrading Umbraco

What to check when the CMS version moves, and — more usefully — *why* those particular things.

`scripts/verify-upgrade.mjs` does the mechanical half. This file is the half that needs judgement:
which surfaces are actually fragile, and which failures only ever show up in a browser.

> **Last verified against:** 18.1.1 → 18.2.0.
> The version-specific claims below (core export names, alias values) were true at that point.
> When one goes stale, correct it or delete it — a wrong entry here is worse than a missing one,
> because it reads as authoritative.

## Before you start

Stop the site. The running process locks the package DLLs and `dotnet build` fails on the copy step:

```powershell
Get-Process -Name Cms | Stop-Process -Force
```

Write down the version you are coming from. You need it for the alias diff in *String aliases* below,
and once the old package is out of the NuGet cache that comparison gets much harder.

## 1. Move the pins

Four places, and all four must agree:

| Where | What |
| --- | --- |
| `Cms/Directory.Packages.props` | `Umbraco.Cms`, plus every other `Umbraco.Cms.*` pin |
| `ContentDashboard/ErrorDashboard/TrueCopy` `.csproj` | four `Umbraco.Cms.*` `PackageReference` versions each |
| each `Client/package.json` | `@umbraco-cms/backoffice` |
| each `Client/public/umbraco-package.json` | `version` — the cache-buster, see step 2 |

**Expect the client peer set to move too.** Bumping `@umbraco-cms/backoffice` alone can fail to
resolve because its peers changed — 18.2.0 moved `@hey-api/openapi-ts` to `>=0.99.0 <1.0.0`, so the
bump had to be:

```bash
npm install --save-dev "@umbraco-cms/backoffice@^18.2.0" "@hey-api/openapi-ts@^0.99.0"
```

**Bump the peer; never reach for `--legacy-peer-deps`.** That flag is what the conflict message
suggests and it is the wrong answer here: it skips the peers (`lit`, `@umbraco-ui/uui`, `rxjs`) the
TypeScript build needs for types, and you get a wall of "module has no exported member" from
`@umbraco-cms/backoffice/external/lit`. See the root `CLAUDE.md`.

Note that bumping `@hey-api/openapi-ts` changes what `npm run generate-client` emits. It does not
regenerate anything on its own, so `src/api` stays as it was until someone runs it — and then the
whole diff lands at once, unrelated to whatever they were working on. Consider regenerating
deliberately, as its own change.

Check the pins agree without needing the site up:

```bash
node scripts/verify-upgrade.mjs --static-only
```

## 2. Build, in this order

```bash
cd <Package>/Client && npm install && npm run build   # check-lang, then tsc, then vite
```

Bump `version` in `Client/public/umbraco-package.json` for any package whose bundle changed. Umbraco
uses it as the `?umb__rnd=` cache-buster; leave it and browsers serve the old bundle.

Then, with the site still stopped:

```bash
dotnet build UContentDashboard.slnx
```

Static web assets are baked at build time, so new hashed chunks are not served until `Cms` is rebuilt
*and* restarted. Restart headless with the ports from `Cms/Properties/launchSettings.json` — see
`CLAUDE.md`.

## 3. Run the mechanical sweep

```bash
node scripts/verify-upgrade.mjs
```

It asserts version-pin agreement, that all three OpenAPI documents generate, that each manifest and
its bundle are served, that every parameterless `GET` answers 200, that paging returns a genuinely
different second page, and that the `Error`/`Fatal` log is clean. Exit code 1 on any failure.

`SKIP` is not failure — it means there was no data to assert against, which is normal on a fresh
install. TrueCopy always skips the sweep: it has one endpoint and it is a `POST`.

**The OpenAPI check is the highest-value probe and that is why it runs first.** One request per
package catches a controller that no longer binds, DI that no longer resolves, and the
`ProducesResponseType` trap below — all of which otherwise present as a vague 500 much later.

**Do not delete logs to make the log check green.** It is reporting something that actually happened.

## 4. What the script cannot do

Everything below needs a browser. The Chrome extension is not installed here; drive Playwright from
the browsers another project already downloaded (see `CLAUDE.md` for the path and the scratch-install
recipe). Backoffice UI lives in nested shadow roots, so walk them recursively — CSS selectors from
`document` will not find anything.

**The three dashboards render.** `/umbraco/section/content/dashboard/{my-content,scheduled,ownership}`
and `/umbraco/section/settings/dashboard/{errors,error-pages,error-alerts}`. Hook `console` and
`response` and assert zero errors, rather than eyeballing a screenshot.

**TrueCopy's entity action opens — the single most fragile thing in this repo.** Open a document,
click the ellipsis in the workspace header (to the right of the name field, *not* the `...` in the
tree), and confirm **True Copy…** appears just below core's **Duplicate to…**. Click it and confirm
the destination modal opens with the tree pre-expanded to the document's ancestors.

Two traps that will cost you an hour otherwise:

- A synthetic `element.click()` on a `uui-menu-item` host does **not** trigger it — the handler is on
  a button inside its shadow root. Use a real mouse click at the item's coordinates.
- Resolving *which* element a console warning came from is worth the effort: take the message's
  `args()`, `evaluate` on the handle, and walk `getRootNode().host` upwards. That is how the
  `UUI-BUTTON needs a label` warning was traced to core's own login page rather than to this repo.

**The rewriters actually rewrite.** Unit tests cover them as pure functions, but only a real copy
exercises the notification handler, the key map and the save path together. Use the fixture — see
*Current test data* in `CLAUDE.md`; it exists for exactly this and is the only way to re-verify it.
Copy **United Kingdom** onto **Europe** with descendants, then assert on the copy of *Alton Towers*
that the content picker, tree picker, URL picker, rich-text `{localLink:…}` and the block-list
self-link all point at the **copies**, that the Europa-Park links (outside the copy) are untouched
*and* reported, and that the originals are unchanged. Then delete the copy.

## Known-fragile surfaces

### Core exports the client imports

TrueCopy imports more core surface than anything else here, and a rename breaks it at **click** time:

```
UMB_DUPLICATE_DOCUMENT_MODAL, UmbDocumentTreeRepository, UmbDocumentItemRepository,
UMB_DOCUMENT_ENTITY_TYPE, UMB_DOCUMENT_ROOT_ENTITY_TYPE, UMB_USER_PERMISSION_DOCUMENT_DUPLICATE
    from @umbraco-cms/backoffice/document
UmbDocumentTypeDetailRepository, UmbDocumentTypeStructureRepository
    from @umbraco-cms/backoffice/document-type
UMB_ENTITY_IS_NOT_TRASHED_CONDITION_ALIAS  from @umbraco-cms/backoffice/recycle-bin
linkEntityExpansionEntries                 from @umbraco-cms/backoffice/utils
```

**`tsc` catches a removed or renamed export — but only if `node_modules` matches the running server.**
That is the real reason step 1 insists the client pin move with the CMS. Before the 18.2.0 bump the
clients still had 18.1.1 installed, so `tsc` was cheerfully checking against last version's API
surface while the browser loaded the new one. The build passing proved nothing about the upgrade.

### String aliases, which `tsc` cannot catch

Aliases are plain strings resolved at runtime, so nothing fails at build time — the feature just
quietly stops working.

**Property editor UI aliases.** Diff what the backoffice registers between the two versions:

```bash
grep -rho "Umb\.PropertyEditorUi\.[A-Za-z.]*" \
  ~/.nuget/packages/umbraco.cms.staticassets/<version>/ | sort -u
```

Run it for the old and new version and diff. Then check the `editorUiAlias` of every data type
against the result — a stale one renders *"The configured property editor UI could not be found"* and
the property becomes uneditable, while the stored value stays intact. `Umbraco.MultiNodeTreePicker`
resolves to `Umb.PropertyEditorUi.ContentPicker`, which is not the name you would guess.

**Manifest condition aliases** (`Umb.Condition.SectionAlias`, `Umb.Condition.CurrentUser.IsAdmin`,
`Umb.Condition.UserPermission.Document`) and **modal aliases**. A condition alias that no longer
exists makes the extension silently not appear — there is no error, the dashboard or menu item is
simply absent. If something has vanished from the UI, check its conditions before anything else.

### Stored property-value shapes

The five rewriters in `TrueCopy/Rewriting` encode the on-disk JSON of Content Picker, Multinode
Treepicker, Multi URL Picker, Rich Text and Block List/Grid. A shape change in core means silently
wrong links rather than an exception, because a rewriter that does not recognise a value returns
`null` and leaves it alone. `TrueCopy/README.md` has the table of shapes; the fixture check in step 4
is what actually catches this.

### Paging

`Database.SkipTake`/`Page` emit provider-correct keywords; a hand-written `TOP (n)` or
`OFFSET … FETCH NEXT` is T-SQL only and dies on SQLite. Both providers are supported here, so if a
list reads fine but page 2 throws, that is the cause. The script's paging probe exists for this.

### The OpenAPI `ProducesResponseType` trap

Never declare a `401` **or** `403` on a controller action. Umbraco's
`BackOfficeSecurityRequirementsTransformer` adds both to every operation, and declaring either
yourself throws `An item with the same key has already been added` while the document generates —
surfacing as a 500 on `/umbraco/openapi/<apiname>.json` with nothing in the trace naming your
controller.

## Things that look like upgrade fallout but are not

A sweep this thorough turns up pre-existing problems, and it is easy to waste time hunting for an
upgrade cause that does not exist. Everything found in the 18.1.1 → 18.2.0 pass was in this category.
Before blaming the upgrade, check whether the thing was ever right:

- **A bad alias or config value.** Grep the *previous* version's package for the alias too. The
  `Umb.PropertyEditorUi.TreePicker` on the TrueCopy Tree Picker data type existed in neither 18.1.1
  nor 18.2.0 — it had simply always been wrong.
- **A console message.** Resolve it to its owning element before assuming it is ours (see step 4).
- **Template scaffolding.** `dotnet new umbraco-extension` leaves a `console.log("Hello from my
  extension 🎉")` in the entrypoint.
- **Config that was never filled in.** `Smtp:PickupDirectoryLocation` shipped as the literal
  placeholder `C:/[your-directory]/…`.

## Practical notes

- **`appsettings.json` hot-reloads.** Config-only changes need no restart — but the `bin/` copy goes
  stale, so rebuild before you finish.
- **`Smtp:PickupDirectoryLocation` is resolved against the content root** (`Cms/`) when relative, and
  `~/` is **not** expanded — it becomes a literal directory segment and throws
  `DirectoryNotFoundException`. Use `umbraco/Data/MailPickup`. The directory must exist; it is kept by
  `.gitkeep`.
- **To prove email works**, `POST /umbraco/management/api/v1/security/forgot-password` with an
  existing user's address writes a real `.eml` into the pickup directory. Delete it afterwards — it
  contains a reset link. The SMTP health check only inspects `Smtp:Host` and reports an error
  regardless, so it cannot confirm pickup-directory delivery.
- **`DELETE /umbraco/management/api/v1/document/{id}` deletes permanently**, it does not fill the
  recycle bin — do not count on being able to restore.
- **Alert email failures are swallowed.** `AlertService` wraps the send in `try`/`catch` so a failure
  cannot cost you the alert record. A broken mail path is therefore a missing email and a log line,
  never a crash.
