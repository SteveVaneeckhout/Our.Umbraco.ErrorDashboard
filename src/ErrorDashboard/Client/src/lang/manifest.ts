/**
 * Every language this package ships.
 *
 * Adding one is this file plus a sibling dictionary - nothing else in the package changes. Umbraco
 * loads the entry whose culture matches the editor's backoffice language (regional first, then the
 * bare language), and always loads `en` alongside it as the per-key fallback.
 *
 * Each `js` import becomes its own chunk in the Vite build, so a language nobody selects is never
 * downloaded.
 */
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "localization",
    alias: "ErrorDashboard.Localization.En",
    name: "Error Dashboard English",
    meta: { culture: "en" },
    js: () => import("./en.js"),
  },
  {
    type: "localization",
    alias: "ErrorDashboard.Localization.NlNl",
    name: "Error Dashboard Dutch (NL)",
    // Matches the culture core's own Dutch pack registers, so the two dedupe to one entry in the
    // backoffice language picker rather than showing Dutch twice.
    meta: { culture: "nl-nl" },
    js: () => import("./nl-nl.js"),
  },
];
