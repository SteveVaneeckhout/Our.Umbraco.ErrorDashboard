import { css, html, nothing, svg } from "@umbraco-cms/backoffice/external/lit";
import type { TemplateResult } from "@umbraco-cms/backoffice/external/lit";
import type { UmbLocalizationController } from "@umbraco-cms/backoffice/localization-api";
import type { DailyPointResponseModel } from "../api/index.js";

/** How many rows each dashboard requests per page. */
export const PAGE_SIZE = 50;

/**
 * Stand-in value for a filter's "no filter" option.
 *
 * `uui-select` matches its `value` against each option's value to decide what to display, and an
 * empty string does not survive that round trip - picking "All hosts" left the control looking blank
 * even though the filter itself had applied. Any non-empty sentinel fixes it. It is mapped back to
 * `undefined` before the value reaches the API, and is deliberately not a string a real host or
 * status code could ever be.
 */
export const ANY_OPTION = "__any__";

/** Maps a select's raw value back to a filter value, collapsing the sentinel to undefined. */
export function fromSelectValue(value: string): string | undefined {
  return !value || value === ANY_OPTION ? undefined : value;
}

/** Maps a filter value to what the select should display. */
export function toSelectValue(value: string | number | undefined): string {
  return value === undefined || value === null || value === "" ? ANY_OPTION : String(value);
}

/**
 * Groups the NEL error types into something an editor can reason about.
 *
 * Takes the calling element's `this.localize` because these are display labels, and a module-level
 * function has no host of its own to get one from. The raw type is returned untranslated when it
 * matches no group - it is a protocol token at that point, not a word.
 */
export function describeErrorType(localize: UmbLocalizationController, type: string): string {
  if (type.startsWith("dns.")) return localize.term("errorDashboardShared_errorTypeDns");
  if (type.startsWith("tcp.")) return localize.term("errorDashboardShared_errorTypeConnection");
  if (type.startsWith("tls.")) return localize.term("errorDashboardShared_errorTypeCertificate");
  if (type === "http.error") return localize.term("errorDashboardShared_errorTypeHttpError");
  if (type.startsWith("http.")) return localize.term("errorDashboardShared_errorTypeHttpProtocol");
  return type;
}

/** Colour token for a status code, matching the backoffice's own severity language. */
export function statusColor(statusCode: number | null | undefined): string {
  if (statusCode === null || statusCode === undefined) return "warning";
  if (statusCode >= 500) return "danger";
  if (statusCode >= 400) return "warning";
  return "default";
}

/**
 * A bar chart of daily error counts, drawn as inline SVG.
 *
 * Hand-rolled rather than pulling in a charting library: the bundle externalises `@umbraco/*` only,
 * so any dependency ships in full, and this is one series of plain counts. Everything is sized in
 * `uui` custom properties so it follows the backoffice theme, light or dark.
 */
export function renderDailyChart(
  localize: UmbLocalizationController,
  series: Array<DailyPointResponseModel>,
): TemplateResult {
  if (!series.length) {
    return html`<div class="state">
      <span>${localize.term("errorDashboardShared_noDataForPeriod")}</span>
    </div>`;
  }

  const width = 100;
  const height = 32;
  const gap = 0.25;
  const max = Math.max(...series.map((point) => point.count), 1);
  const barWidth = width / series.length;

  // Through the controller, so the axis labels follow the backoffice language rather than the
  // browser's - "12 mrt" for a Dutch editor, not "Mar 12".
  const formatDay = (iso: string) => localize.date(new Date(iso), { month: "short", day: "numeric" });

  const bars = series.map((point, index) => {
    // A day with errors always gets a visible sliver, so "one error" never renders identically to
    // "no errors" - the distinction is the entire point of the chart.
    const ratio = point.count === 0 ? 0 : Math.max(point.count / max, 0.03);
    const barHeight = ratio * height;

    return svg`<rect
      class=${point.count === 0 ? "bar bar--empty" : "bar"}
      x=${index * barWidth + gap / 2}
      y=${height - barHeight}
      width=${Math.max(barWidth - gap, 0.1)}
      height=${barHeight}
      ><title>
        ${localize.term("errorDashboardShared_chartTooltip", formatDay(point.date), point.count)}
      </title></rect>`;
  });

  const first = series[0];
  const last = series[series.length - 1];

  return html`
    <div class="chart">
      <svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img"
           aria-label=${localize.term("errorDashboardShared_chartAriaLabel")}>
        ${bars}
      </svg>
      <div class="chart-axis">
        <span>${formatDay(first.date)}</span>
        <span class="chart-peak"
          >${localize.term("errorDashboardShared_chartPeak", localize.number(max))}</span
        >
        <span>${formatDay(last.date)}</span>
      </div>
    </div>
  `;
}

/** Renders a "N of M" style stat tile. */
export function renderStat(label: string, value: string, hint?: string): TemplateResult {
  return html`
    <div class="stat">
      <span class="stat-value">${value}</span>
      <span class="stat-label">${label}</span>
      ${hint ? html`<span class="stat-hint">${hint}</span>` : nothing}
    </div>
  `;
}

/**
 * Layout shared by all three Error Dashboard tabs.
 *
 * Structured like the Examine management dashboard: the uui-box keeps its default padding and the
 * table sits inside that padding rather than bleeding to the container edges. The scroll container
 * is what stops a wide table pushing the box out at narrow widths.
 */
export const sharedDashboardStyles = css`
  :host {
    display: block;
    padding: var(--uui-size-layout-1);
  }

  uui-box {
    margin-bottom: var(--uui-size-layout-1);
  }

  .toolbar {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--uui-size-space-3);
    margin-bottom: var(--uui-size-space-5);
  }

  .toolbar .spacer {
    flex: 1 1 auto;
  }

  .toolbar label {
    font-size: var(--uui-type-small-size);
    color: var(--uui-color-text-alt);
  }

  .table-container {
    display: flex;
    align-items: flex-start;
  }

  .table-container uui-scroll-container {
    flex: 1;
    max-width: 100%;
    overflow-x: auto;
  }

  umb-table {
    display: block;
  }

  .state {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: var(--uui-size-space-3);
    padding: var(--uui-size-layout-2) 0;
    color: var(--uui-color-text-alt);
    text-align: center;
  }

  .footer {
    display: flex;
    justify-content: center;
    margin-top: var(--uui-size-space-5);
  }

  .muted {
    color: var(--uui-color-text-alt);
  }

  .count {
    font-variant-numeric: tabular-nums;
  }

  /* --- stats --- */

  .stats {
    display: flex;
    flex-wrap: wrap;
    gap: var(--uui-size-layout-1);
    margin-bottom: var(--uui-size-space-5);
  }

  .stat {
    display: flex;
    flex-direction: column;
    gap: var(--uui-size-space-1);
    min-width: 140px;
  }

  .stat-value {
    font-size: var(--uui-type-h4-size);
    font-weight: bold;
    font-variant-numeric: tabular-nums;
    line-height: 1.1;
  }

  .stat-label {
    font-size: var(--uui-type-small-size);
    color: var(--uui-color-text-alt);
  }

  .stat-hint {
    font-size: var(--uui-type-small-size);
    color: var(--uui-color-text-alt);
    opacity: 0.8;
  }

  /* --- chart --- */

  .chart svg {
    display: block;
    width: 100%;
    height: 140px;
  }

  .chart .bar {
    fill: var(--uui-color-danger);
  }

  .chart .bar--empty {
    fill: var(--uui-color-divider-emphasis);
  }

  .chart-axis {
    display: flex;
    justify-content: space-between;
    margin-top: var(--uui-size-space-2);
    font-size: var(--uui-type-small-size);
    color: var(--uui-color-text-alt);
  }

  .chart-peak {
    font-variant-numeric: tabular-nums;
  }

  /* --- misc --- */

  .note {
    font-size: var(--uui-type-small-size);
    color: var(--uui-color-text-alt);
    line-height: 1.5;
  }

  .breakdown {
    display: flex;
    flex-wrap: wrap;
    gap: var(--uui-size-space-2);
  }

  .path {
    font-family: var(--uui-font-monospace, monospace);
    word-break: break-all;
  }
`;
