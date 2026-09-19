/**
 * Language Alias: en
 * Language Int Name: English
 * Language Local Name: English
 * Language Culture: en
 *
 * The source of truth for every user-facing string in this package, and the per-key fallback for
 * every other language: culture `en` is Umbraco's `UMB_DEFAULT_LOCALIZATION_CULTURE`, so the
 * registry loads it whatever the backoffice is set to. Never translate this file - copy it.
 *
 * One area per dashboard, plus a `Shared` area for the text that genuinely appears on more than one
 * of them. Every area is prefixed with the package name, because Umbraco flattens areas into a
 * single `area_key` namespace shared with core and with every other installed package.
 *
 * Entries whose value contains markup are rendered with `localize.htmlString()`, which escapes the
 * arguments before interpolating them. Every other entry goes through `localize.term()` and is
 * escaped by Lit as normal text.
 */
import type { UmbLocalizationDictionary } from "@umbraco-cms/backoffice/localization-api";

const en = {
  errorDashboardShared: {
    /**
     * The caveat that governs how every number on these dashboards should be read. Prominent rather
     * than tucked into a tooltip: someone comparing these figures against server logs will otherwise
     * conclude the package is broken.
     */
    coverageNote:
      "Reported by visitors' browsers using Network Error Logging, which only Chromium-based browsers " +
      "support. Firefox, Safari and non-browser traffic are not represented, so real totals are higher.",
    aboutTheseNumbers: "About these numbers",

    period: "Period",
    lastDays: (days: number) => `Last ${days} days`,
    host: "Host",
    filterByHost: "Filter by host",
    allHosts: "All hosts",

    noDataForPeriod: "No data for this period.",
    chartAriaLabel: "Errors per day over the selected period",
    chartTooltip: (day: string, count: number) =>
      count === 1 ? `${day}: 1 error` : `${day}: ${count} errors`,
    chartPeak: (peak: string) => `peak ${peak}/day`,

    // The NEL error types, grouped into something an editor can reason about.
    errorTypeDns: "DNS",
    errorTypeConnection: "Connection",
    errorTypeCertificate: "Certificate",
    errorTypeHttpError: "HTTP error",
    errorTypeHttpProtocol: "HTTP protocol",
  },

  errorDashboardOverview: {
    label: "Errors",
    headline: "Errors reported by visitors",

    emailMeAboutSpikes: "Email me about error spikes",
    subscribedNoEmailHeadline: "Subscribed, but email is not configured",
    subscribedNoEmailMessage:
      "Umbraco has no SMTP configuration, so alert emails cannot be delivered. " +
      "Set Umbraco:CMS:Global:Smtp (a From address plus either a Host or a PickupDirectoryLocation).",
    alertsOnHeadline: "Alerts on",
    alertsOnMessage: "You will be emailed when this site's error rate looks abnormal.",
    alertsOffHeadline: "Alerts off",
    alertsOffMessage: "You will no longer receive error alerts.",

    statLast24Hours: "Last 24 hours",
    statErrorRate: "Estimated error rate",
    statErrorRateHint: (requests: string) => `of ~${requests} sampled requests`,
    statNotYetCounted: "Not yet counted",
    statNotYetCountedHint: "rolled up hourly",

    isThisUnusual: "Is this unusual?",
    noAssessment: "No assessment available.",
    aboveNormal: "Above normal",
    normal: "Normal",
    howJudged: (baselineDays: number) =>
      `Judged against the ${baselineDays} days before today, using the median and its absolute ` +
      "deviation - so the threshold adapts to how noisy this particular site normally is, rather " +
      "than to a fixed number of errors.",

    /*
     * The detector's verdict, one entry per gate. The server sends which gate held plus the raw
     * numbers behind it; the figures arrive here already formatted for this language, which is why
     * every argument is a string.
     */
    explanationInsufficientHistory: (days: string, required: string) =>
      `Only ${days} days of history; ${required} are needed before anything is judged.`,
    explanationBelowFloor: (observed: string, floor: string) =>
      `${observed} errors is below the floor of ${floor}.`,
    explanationBelowRatio: (observed: string, ratio: string, median: string) =>
      `${observed} is less than ${ratio}x the usual ${median}.`,
    explanationWithinSpread: (score: string, threshold: string) =>
      `Score ${score} is within the normal spread for this site (threshold ${threshold}).`,
    explanationAnomalous: (observed: string, median: string, score: string) =>
      `${observed} errors against a usual ${median} - ${score} times this site's normal variation.`,

    whatIsFailing: "What is failing",
    nothingReported: "Nothing reported in this period.",

    notYetAggregated: "Not yet aggregated",
    updatedAgo: (when: string) => `Updated ${when}`,
  },

  errorDashboardPages: {
    label: "Error Pages",
    headline: "URLs visitors are hitting errors on",
    description: "Worst first",

    columnPath: "Path",
    columnProblem: "Problem",
    columnErrors: "Errors",
    columnLastSeen: "Last seen",

    status: "Status",
    filterByStatus: "Filter by status code",
    anyStatus: "Any",
    totalUrls: (count: number) => (count === 1 ? "1 URL" : `${count} URLs`),
    empty: "No errors reported for this period.",

    editTitle: (url: string) => `Edit ${url}`,
    showLinksAria: (path: string) => `Show what links to ${path}`,
    hideLinksAria: (path: string) => `Hide what links to ${path}`,
    showLinksTitle: "Show what links here",
    hideLinksTitle: "Hide what links here",

    unpublished: "Unpublished",
    unpublishedTitle: "The page exists but is not published",

    /**
     * The path is monospaced mid-sentence, so this entry carries the markup and is rendered with
     * `htmlString`. A translation may move the placeholder, but should keep the span around it.
     */
    linkingTo: (path: string) => `Linking to <span class="path">${path}</span>`,
    noReferrers: (days: number) => `No referrers recorded in the last ${days} days.`,
    referrerWindowNote: (retentionDays: number, rangeDays: number) =>
      `Referrers come from raw reports, which are kept for ${retentionDays} days — shorter than the ` +
      `${rangeDays}-day range above.`,
    referrerCount: (count: string) => `${count}x`,
    noReferrer: "No referrer — a direct hit, a bookmark, or stripped by a referrer policy",

    /** The bucket the aggregator folds a day's long tail of paths into, once the per-day cap is hit. */
    otherPaths: "(other paths)",

    missingAssetsNote:
      "Missing assets - old image paths, a favicon, a stale script - usually make up most of the " +
      "volume here, not pages. Sort by path to spot them quickly.",
  },

  errorDashboardAlerts: {
    label: "Error Alerts",

    alertHistory: "Alert history",
    recomputeNow: "Recompute now",
    recomputing: "Recomputing…",
    recomputedHeadline: "Recomputed",
    recomputedWithAlert: (aggregated: number, pruned: number) =>
      `Aggregated ${aggregated} report(s), pruned ${pruned} row(s), and raised an alert.`,
    recomputedNoAlert: (aggregated: number, pruned: number) =>
      `Aggregated ${aggregated} report(s) and pruned ${pruned} row(s). No new alert was raised.`,

    noAlerts: "No alerts have been raised. Nothing has looked abnormal.",
    /** Numbers are called out mid-sentence, so this one carries its markup. Rendered with `htmlString`. */
    alertBody: (observed: string, median: string, score: string) =>
      `<span class="count">${observed}</span> errors in 24h against a usual ` +
      `<span class="count">${median}</span> per day ` +
      `(<span class="count">${score}</span>x this site's normal variation).`,
    alertEmailed: (count: number) =>
      count === 1 ? "Emailed 1 subscriber." : `Emailed ${count} subscribers.`,

    subscribers: "Subscribers",
    noSubscribers: "Nobody is subscribed. Users opt in from the toggle on the Errors tab.",
    lastEmailed: "last emailed",
    neverEmailed: "never emailed",

    settings: "Settings",
    emailNotConfigured: "Email is not configured",
    /** Carries `<code>` around configuration keys. Rendered with `htmlString`; the keys are literal. */
    emailNotConfiguredNote:
      "Umbraco has no SMTP settings, so alerts would be silently discarded. Set " +
      "<code>Umbraco:CMS:Global:Smtp</code> with a <code>From</code> address and either a " +
      "<code>Host</code> or a <code>PickupDirectoryLocation</code>.",

    settingAlerting: "Alerting",
    settingCollectorPath: "Collector path",
    settingBaseline: "Baseline",
    settingTriggerScore: "Trigger score",
    settingMinimumErrors: "Minimum errors",
    settingMinimumMultiple: "Minimum multiple",
    settingCooldown: "Cooldown",
    settingRawRetention: "Raw retention",
    settingRateLimit: "Collector rate limit",
    settingAcceptedHosts: "Accepted hosts",
    settingSuccessSampling: "Success sampling",

    on: "On",
    off: "Off",
    baselineValue: (days: number, minimum: number) => `${days} days (min ${minimum})`,
    minimumMultipleValue: (ratio: string) => `${ratio}x the usual`,
    cooldownValue: (hours: number) => `${hours} hours`,
    rawRetentionValue: (days: number) => `${days} days`,
    rateLimitValue: (perMinute: number) => `${perMinute} payloads/min per IP`,
    requestHostOnly: "Request host only",
    successSamplingValue: (percentage: string) => `${percentage}% of successful requests`,
    successSamplingOff: "Off - no traffic denominator",

    thresholdNote:
      "An alert fires when the last 24 hours exceed all of the thresholds above at once. The score " +
      "is the distance from the median of the baseline days, measured in median absolute deviations, " +
      "so a site with a naturally noisy error count needs a bigger jump than a quiet one.",
    /** Carries `<code>` around a header name and a configuration key. Rendered with `htmlString`. */
    noDomainsNote:
      "No Umbraco domains are configured, so the collector can only fall back to comparing a report " +
      "against the request's own <code>Host</code> header — which the caller supplies. Configure a " +
      "domain in Umbraco, or list hostnames under <code>ErrorDashboard:AllowedHosts</code>, to close " +
      "that gap.",
  },
} as const;

export default en satisfies UmbLocalizationDictionary;

/** The shape every other language file must match. See `types.ts`. */
export type ErrorDashboardLocalizations = typeof en;
