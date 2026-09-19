import type { UmbLocalizationController } from "@umbraco-cms/backoffice/localization-api";

const RELATIVE_UNITS: Array<[Intl.RelativeTimeFormatUnit, number]> = [
  ["year", 365 * 24 * 60 * 60 * 1000],
  ["month", 30 * 24 * 60 * 60 * 1000],
  ["week", 7 * 24 * 60 * 60 * 1000],
  ["day", 24 * 60 * 60 * 1000],
  ["hour", 60 * 60 * 1000],
  ["minute", 60 * 1000],
];

/**
 * Both helpers take the calling element's `this.localize` rather than a locale string.
 *
 * The controller formats against the *backoffice* language, which is the one the rest of the page is
 * in. Reaching for `Intl` directly formats against the browser's instead - routinely a different
 * language, and nothing in the UI would show you that it had happened.
 */

/**
 * "3 weeks ago" / "in 2 days" - how stale (or how imminent) something is, at a glance.
 * The API returns UTC; the browser renders in the editor's own timezone.
 */
export function formatRelative(localize: UmbLocalizationController, isoDate: string): string {
  const timestamp = Date.parse(isoDate);
  if (Number.isNaN(timestamp)) return "";

  const deltaMs = timestamp - Date.now();

  for (const [unit, unitMs] of RELATIVE_UNITS) {
    if (Math.abs(deltaMs) >= unitMs) {
      return localize.relativeTime(Math.round(deltaMs / unitMs), unit, { numeric: "auto" });
    }
  }

  return localize.relativeTime(Math.round(deltaMs / 1000), "second", { numeric: "auto" });
}

/** Full local date and time, for the title tooltip behind the relative label. */
export function formatAbsolute(localize: UmbLocalizationController, isoDate: string): string {
  const timestamp = Date.parse(isoDate);
  if (Number.isNaN(timestamp)) return "";

  return localize.date(new Date(timestamp), { dateStyle: "medium", timeStyle: "short" });
}
