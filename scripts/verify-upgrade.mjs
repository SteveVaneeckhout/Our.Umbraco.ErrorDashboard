#!/usr/bin/env node
/**
 * The mechanical half of an Umbraco upgrade check: everything that can be asserted without a
 * browser. See `docs/upgrading.md` for the half that needs judgement (and for what this script
 * deliberately does not cover).
 *
 * Two phases, because they fail for different reasons and one of them does not need the site:
 *
 *   Static  - do the version pins agree with each other? This is the check that would have caught
 *             the clients sitting on `@umbraco-cms/backoffice@18.1.1` while the server ran 18.2.0.
 *             That drift is invisible at runtime (the bundles externalise `@umbraco*` and bind to
 *             whatever import map the backoffice serves) but it silently disarms `tsc`: a core
 *             export removed in the new version still type-checks against the old package.
 *
 *   Live    - does the running site still answer? The OpenAPI documents come first on purpose.
 *             A 500 there is the single highest-value signal in an upgrade: it catches the
 *             `[ProducesResponseType(401)]` duplicate-key trap, a controller that no longer binds,
 *             and DI that no longer resolves, in one request per package.
 *
 * No dependencies - Node's built-in fetch and fs only. Usage:
 *
 *   node scripts/verify-upgrade.mjs
 *   node scripts/verify-upgrade.mjs --base https://localhost:44366
 *   node scripts/verify-upgrade.mjs --static-only
 *
 * Credentials come from `.env` at the repo root (the same `UMBRACO_CLIENT_ID` /
 * `UMBRACO_CLIENT_SECRET` the umbraco-mcp server uses). Exit code is 1 if anything FAILs, so this
 * is usable as a gate; SKIPs never fail the run - they mean "no data to assert against", which is
 * the normal state of a fresh install.
 */
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

// The dev certificate is self-signed and this only ever talks to a local site.
process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";

const repoRoot = fileURLToPath(new URL("..", import.meta.url));

// The projects live under src/; the repo root holds only tooling, docs and .env.
const srcRoot = join(repoRoot, "src");

/** This repository ships one backoffice package: its OpenAPI document name and App_Plugins folder. */
const PACKAGES = [
  { project: "ErrorDashboard", apiName: "errordashboard", appPlugins: "ErrorDashboard" },
];

const args = process.argv.slice(2);
const staticOnly = args.includes("--static-only");
const baseArg = args.indexOf("--base");
const BASE = (baseArg !== -1 && args[baseArg + 1]) || process.env.UMBRACO_VERIFY_BASE_URL || "https://localhost:44366";

const results = [];
const record = (status, name, detail = "") => {
  results.push({ status, name, detail });
  const tag = { pass: "PASS", fail: "FAIL", skip: "SKIP" }[status];
  console.log(`${tag}  ${name}${detail ? `\n        ${detail}` : ""}`);
};
const pass = (n, d) => record("pass", n, d);
const fail = (n, d) => record("fail", n, d);
const skip = (n, d) => record("skip", n, d);

const section = (title) => console.log(`\n── ${title} ${"─".repeat(Math.max(0, 66 - title.length))}`);

// ---------------------------------------------------------------------------
// Static checks - version pins. No site required.
// ---------------------------------------------------------------------------

/** The `Umbraco.Cms` version every other Umbraco pin in the repo should agree with. */
function cmsPinnedVersion() {
  const props = join(srcRoot, "Cms", "Directory.Packages.props");
  if (!existsSync(props)) return null;
  const match = readFileSync(props, "utf8").match(
    /<PackageVersion\s+Include="Umbraco\.Cms"\s+Version="([^"]+)"/,
  );
  return match?.[1] ?? null;
}

function checkStatic() {
  section("Version pins");

  const cmsVersion = cmsPinnedVersion();
  if (!cmsVersion) {
    fail("Umbraco.Cms pin readable", "could not parse src/Cms/Directory.Packages.props");
    return null;
  }
  pass("Umbraco.Cms pin", `src/Cms/Directory.Packages.props pins ${cmsVersion}`);

  // Every other Umbraco.Cms.* pin in the props file should track the same version. uSync and the
  // ICU runtime have their own release lines and are deliberately excluded.
  const propsText = readFileSync(join(srcRoot, "Cms", "Directory.Packages.props"), "utf8");
  for (const [, name, version] of propsText.matchAll(
    /<PackageVersion\s+Include="(Umbraco\.Cms[^"]*)"\s+Version="([^"]+)"/g,
  )) {
    if (name === "Umbraco.Cms") continue;
    if (version === cmsVersion) pass(`pin ${name}`, version);
    else fail(`pin ${name}`, `${version} does not match Umbraco.Cms ${cmsVersion}`);
  }

  // The three packages reference the Umbraco assemblies directly, with literal versions.
  for (const { project } of PACKAGES) {
    const csproj = join(srcRoot, project, `${project}.csproj`);
    if (!existsSync(csproj)) {
      skip(`${project}.csproj`, "not found");
      continue;
    }
    const refs = [
      ...readFileSync(csproj, "utf8").matchAll(
        /<PackageReference\s+Include="(Umbraco\.Cms[^"]*)"\s+Version="([^"]+)"/g,
      ),
    ];
    if (refs.length === 0) {
      skip(`${project} Umbraco references`, "none with a literal version");
      continue;
    }
    // A range like "[18.2.0,19.0.0)" is compared on its lower bound - that is the version the package
    // compiles against, and the one the harness has to run.
    const floor = (v) => v.replace(/^[[(]/, "").split(",")[0].trim();
    const wrong = refs.filter(([, , v]) => floor(v) !== cmsVersion);
    if (wrong.length === 0) pass(`${project} Umbraco references`, `${refs.length} at ${cmsVersion}`);
    else fail(`${project} Umbraco references`, wrong.map(([, n, v]) => `${n}=${v}`).join(", ") + ` (expected ${cmsVersion})`);
  }

  section("Client dependencies");

  // The backoffice npm package must track the CMS, or `tsc` is checking our client code against a
  // different API surface than the one the browser will actually load.
  for (const { project } of PACKAGES) {
    const pkgPath = join(srcRoot, project, "Client", "package.json");
    if (!existsSync(pkgPath)) {
      skip(`${project} client`, "no Client/package.json");
      continue;
    }
    const pkg = JSON.parse(readFileSync(pkgPath, "utf8"));
    const declared = pkg.devDependencies?.["@umbraco-cms/backoffice"] ?? pkg.dependencies?.["@umbraco-cms/backoffice"];
    if (!declared) {
      skip(`${project} declares @umbraco-cms/backoffice`, "not a dependency");
      continue;
    }
    // "^18.2.0" -> "18.2.0"; only the major.minor need agree with the CMS.
    const declaredVersion = declared.replace(/^[^0-9]*/, "");
    const sameLine = (a, b) => a.split(".").slice(0, 2).join(".") === b.split(".").slice(0, 2).join(".");
    if (sameLine(declaredVersion, cmsVersion)) pass(`${project} declares backoffice`, declared);
    else fail(`${project} declares backoffice`, `${declared} is behind Umbraco.Cms ${cmsVersion}`);

    const installedPath = join(srcRoot, project, "Client", "node_modules", "@umbraco-cms", "backoffice", "package.json");
    if (!existsSync(installedPath)) {
      skip(`${project} installed backoffice`, "node_modules not present - run npm install");
      continue;
    }
    const installed = JSON.parse(readFileSync(installedPath, "utf8")).version;
    if (sameLine(installed, cmsVersion)) pass(`${project} installed backoffice`, installed);
    else fail(`${project} installed backoffice`, `${installed} installed but Umbraco.Cms is ${cmsVersion} - run npm install`);
  }

  return cmsVersion;
}

// ---------------------------------------------------------------------------
// Live checks - the running site.
// ---------------------------------------------------------------------------

function readEnv() {
  const envPath = join(repoRoot, ".env");
  if (!existsSync(envPath)) return {};
  return Object.fromEntries(
    readFileSync(envPath, "utf8")
      .split(/\r?\n/)
      .filter((line) => line && !line.startsWith("#") && line.includes("="))
      .map((line) => [line.slice(0, line.indexOf("=")).trim(), line.slice(line.indexOf("=") + 1).trim()]),
  );
}

async function getToken(env) {
  const response = await fetch(`${BASE}/umbraco/management/api/v1/security/back-office/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "client_credentials",
      client_id: env.UMBRACO_CLIENT_ID,
      client_secret: env.UMBRACO_CLIENT_SECRET,
    }),
  });
  if (!response.ok) throw new Error(`${response.status} ${(await response.text()).slice(0, 120)}`);
  return (await response.json()).access_token;
}

const getJson = async (url, auth) => {
  const response = await fetch(url, { headers: auth });
  const text = await response.text();
  let body = null;
  try {
    body = JSON.parse(text);
  } catch {
    /* not JSON - body stays null and the caller reports on status alone */
  }
  return { status: response.status, ok: response.ok, body, text };
};

async function checkLive() {
  section("Site");

  try {
    const response = await fetch(`${BASE}/umbraco`, { redirect: "manual" });
    if (response.status >= 500) {
      fail("backoffice reachable", `${BASE}/umbraco returned ${response.status}`);
      return;
    }
    pass("backoffice reachable", `${BASE}/umbraco -> ${response.status}`);
  } catch (error) {
    fail("backoffice reachable", `${BASE} - ${error.message}. Is the site running?`);
    return;
  }

  const env = readEnv();
  if (!env.UMBRACO_CLIENT_ID || !env.UMBRACO_CLIENT_SECRET) {
    skip("API token", ".env has no UMBRACO_CLIENT_ID/SECRET - skipping all authenticated checks");
    return;
  }

  let auth;
  try {
    auth = { Authorization: `Bearer ${await getToken(env)}` };
    pass("API token", "client credentials accepted");
  } catch (error) {
    fail("API token", error.message);
    return;
  }

  // --- OpenAPI documents. The highest-value probe in the whole script. ---
  section("OpenAPI documents");
  const docs = new Map();
  for (const { apiName } of PACKAGES) {
    const url = `${BASE}/umbraco/swagger/${apiName}/swagger.json`;
    const { status, body } = await getJson(url, auth);
    if (status !== 200) {
      fail(`swagger/${apiName}/swagger.json`, `HTTP ${status} - a 500 here usually means a duplicate ProducesResponseType(401/403)`);
      continue;
    }
    if (!body?.paths) {
      fail(`swagger/${apiName}/swagger.json`, "200 but no paths in the document");
      continue;
    }
    docs.set(apiName, body);
    pass(`swagger/${apiName}/swagger.json`, `${Object.keys(body.paths).length} path(s)`);
  }

  // --- Served client bundles. ---
  section("Client bundles");
  for (const { appPlugins } of PACKAGES) {
    const manifestUrl = `${BASE}/App_Plugins/${appPlugins}/umbraco-package.json`;
    const { status, body } = await getJson(manifestUrl, auth);
    if (status !== 200 || !body) {
      fail(`${appPlugins} manifest`, `HTTP ${status} at ${manifestUrl}`);
      continue;
    }
    const entry = body.extensions?.find((extension) => extension.js)?.js;
    if (!entry) {
      pass(`${appPlugins} manifest`, `v${body.version} (no bundle entry to check)`);
      continue;
    }
    const bundle = await fetch(`${BASE}${entry}`, { headers: auth });
    if (bundle.ok) pass(`${appPlugins} manifest`, `v${body.version}, bundle ${entry} -> 200`);
    else fail(`${appPlugins} bundle`, `${entry} -> ${bundle.status}. Rebuild the client and Cms, then restart.`);
  }

  // --- Every GET that needs no parameters. ---
  section("Endpoint sweep");
  for (const [apiName, doc] of docs) {
    let probed = 0;
    for (const [path, operations] of Object.entries(doc.paths)) {
      if (!operations.get || path.includes("{")) continue;
      const required = (operations.get.parameters ?? []).filter((p) => p.required && p.in === "query");
      if (required.length > 0) continue;
      const { status } = await getJson(`${BASE}${path}`, auth);
      probed += 1;
      if (status === 200) pass(`GET ${path}`);
      else if (status === 400) skip(`GET ${path}`, "400 - needs query parameters the sweep cannot infer");
      else fail(`GET ${path}`, `HTTP ${status}`);
    }
    if (probed === 0) skip(`${apiName} sweep`, "no parameterless GET endpoints (TrueCopy is POST-only - check it by hand)");
  }

  // --- Targeted probes: paging and the endpoints that need real parameters. ---
  section("Targeted probes");

  if (docs.has("contentdashboard")) {
    const owners = await getJson(`${BASE}/umbraco/contentdashboard/api/v1/owners`, auth);
    const owner = Array.isArray(owners.body) ? owners.body[0] : null;
    if (!owner) {
      skip("ContentDashboard paging", "no owners returned - nothing to page through");
    } else {
      const first = await getJson(`${BASE}/umbraco/contentdashboard/api/v1/documents?ownerId=${owner.id}&take=1`, auth);
      const total = first.body?.total ?? 0;
      if (first.status !== 200) {
        fail("ContentDashboard documents", `HTTP ${first.status}`);
      } else if (total > 1) {
        // A second page is where a hand-written paging clause breaks - see docs/upgrading.md.
        const second = await getJson(
          `${BASE}/umbraco/contentdashboard/api/v1/documents?ownerId=${owner.id}&skip=1&take=1`,
          auth,
        );
        const movedOn = second.body?.items?.[0]?.id && second.body.items[0].id !== first.body.items[0].id;
        if (second.status === 200 && movedOn) pass("ContentDashboard paging", `${total} documents, page 2 differs from page 1`);
        else fail("ContentDashboard paging", `skip=1 returned HTTP ${second.status} / same first row`);
      } else {
        skip("ContentDashboard paging", `only ${total} document(s) - no second page to assert`);
      }
    }
  }

  if (docs.has("errordashboard")) {
    const hosts = await getJson(`${BASE}/umbraco/errordashboard/api/v1/hosts`, auth);
    const host = Array.isArray(hosts.body) ? hosts.body[0] : null;
    if (!host) {
      skip("ErrorDashboard pages/referrers", "no hosts aggregated yet - nothing to query");
    } else {
      const pages = await getJson(
        `${BASE}/umbraco/errordashboard/api/v1/pages?host=${encodeURIComponent(host)}&days=30&take=1`,
        auth,
      );
      if (pages.status !== 200) {
        fail("ErrorDashboard pages", `HTTP ${pages.status}`);
      } else {
        pass("ErrorDashboard pages", `${pages.body?.total ?? 0} URL(s) for ${host}`);
        const path = pages.body?.items?.[0]?.path;
        if (!path) {
          skip("ErrorDashboard referrers", "no page rows to look up referrers for");
        } else {
          const referrers = await getJson(
            `${BASE}/umbraco/errordashboard/api/v1/referrers?host=${encodeURIComponent(host)}&path=${encodeURIComponent(path)}&days=30&take=5`,
            auth,
          );
          if (referrers.status === 200) pass("ErrorDashboard referrers", `${referrers.body?.items?.length ?? 0} referrer(s) for ${path}`);
          else fail("ErrorDashboard referrers", `HTTP ${referrers.status}`);
        }
      }
    }
  }

  // --- The log. An upgrade that half-worked usually says so here. ---
  section("Log");
  for (const level of ["Error", "Fatal"]) {
    const { status, body } = await getJson(
      `${BASE}/umbraco/management/api/v1/log-viewer/log?skip=0&take=5&orderDirection=Descending&logLevel=${level}`,
      auth,
    );
    if (status !== 200) {
      skip(`${level} log entries`, `log viewer returned HTTP ${status}`);
      continue;
    }
    const total = body?.total ?? 0;
    if (total === 0) pass(`${level} log entries`, "none");
    else fail(`${level} log entries`, `${total} - newest: ${(body.items?.[0]?.renderedMessage ?? "").slice(0, 120)}`);
  }
}

// ---------------------------------------------------------------------------

console.log(`Umbraco upgrade verification\nrepo: ${repoRoot}\nbase: ${staticOnly ? "(static only)" : BASE}`);

checkStatic();
if (!staticOnly) await checkLive();

section("Summary");
const counts = { pass: 0, fail: 0, skip: 0 };
for (const { status } of results) counts[status] += 1;
console.log(`${counts.pass} passed, ${counts.fail} failed, ${counts.skip} skipped`);

if (counts.fail > 0) {
  console.log("\nFailed:");
  for (const { status, name, detail } of results) {
    if (status === "fail") console.log(`  - ${name}${detail ? `: ${detail}` : ""}`);
  }
}

console.log("\nThis script does not cover the browser. See docs/upgrading.md for what still needs a human:");
console.log("  dashboards rendering, the TrueCopy entity action and its modal, and the rewriter fixture.");

process.exit(counts.fail > 0 ? 1 : 0);
