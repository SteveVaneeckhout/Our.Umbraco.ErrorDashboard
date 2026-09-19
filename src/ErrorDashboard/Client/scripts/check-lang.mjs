/**
 * Fails the build when a localization key referenced from an element or a manifest does not exist
 * in `src/lang/en.ts`.
 *
 * This is the half `tsc` cannot do. The dictionary type checks that every *language* is complete
 * (`Translation<ErrorDashboardLocalizations>` in each non-English file), but `localize.term()` takes a
 * plain string, so a typo there type-checks happily and then renders the raw key in the UI - the
 * kind of thing nobody spots until it is in front of an editor.
 *
 * Node reads the TypeScript dictionary directly (type stripping, Node 22.18+), so there is no
 * dependency to install and no build step to keep in sync.
 */
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const clientRoot = fileURLToPath(new URL("..", import.meta.url));
const srcRoot = join(clientRoot, "src");
const langRoot = join(srcRoot, "lang");

/** Areas are flattened into one `area_key` namespace by Umbraco's localization registry. */
function flatten(dictionary) {
  const keys = new Set();
  for (const [area, entries] of Object.entries(dictionary)) {
    for (const key of Object.keys(entries)) {
      keys.add(`${area}_${key}`);
    }
  }
  return keys;
}

/** Every hand-written source file. `src/api` is generated and `src/lang` is the dictionary itself. */
function sourceFiles(dir) {
  const files = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (full === langRoot || entry === "api") continue;
      files.push(...sourceFiles(full));
    } else if (entry.endsWith(".ts")) {
      files.push(full);
    }
  }
  return files;
}

// Any `area_key`-shaped string literal, with or without the `#` that manifest labels and
// `localize.string()` / `localize.htmlString()` use.
//
// Deliberately not anchored to `.term(`: keys are routinely chosen inline (`term(open ? "a" : "b")`)
// or through a lookup table, and a pattern that only matched the first argument position reported
// every one of those as unused. Matching the literal itself and then filtering on our own area
// prefixes catches all of those spellings, and the required underscore stops it matching a CSS
// colour inside a `css` template literal.
const KEY_PATTERN = /["'`]#?([A-Za-z0-9]+_[A-Za-z0-9_]+)["'`]/g;

const dictionary = (await import(pathToFileURL(join(langRoot, "en.ts")).href)).default;
const known = flatten(dictionary);
const areas = Object.keys(dictionary);
const used = new Set();
const problems = [];

/** Core's own keys are fair game and are not in our dictionary; only keys in one of our areas are ours. */
const isOurs = (key) => areas.some((area) => key.startsWith(`${area}_`));

for (const file of sourceFiles(srcRoot)) {
  const text = readFileSync(file, "utf8");
  const lines = text.split(/\r?\n/);

  for (const match of text.matchAll(KEY_PATTERN)) {
    const key = match[1];
    used.add(key);
    if (known.has(key) || !isOurs(key)) continue;

    const line = text.slice(0, match.index).split(/\r?\n/).length;
    problems.push(`  ${relative(clientRoot, file)}:${line}  ${key}\n    ${lines[line - 1].trim()}`);
  }
}

const unused = [...known].filter((key) => !used.has(key));

if (unused.length) {
  console.warn(`check-lang: ${unused.length} key(s) in en.ts are never used: ${unused.join(", ")}`);
}

if (problems.length) {
  console.error(`check-lang: ${problems.length} unknown localization key(s):\n${problems.join("\n")}`);
  process.exit(1);
}

console.log(`check-lang: ${used.size} reference(s) resolved against ${known.size} key(s) in en.ts.`);
