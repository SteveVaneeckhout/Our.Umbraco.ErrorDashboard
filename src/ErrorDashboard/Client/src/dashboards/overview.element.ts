import { getOverview, getSubscription, putSubscription } from "../api/index.js";
import type {
  AnomalyExplanation,
  ErrorTypeCountResponseModel,
  OverviewResponseModel,
  StatusCountResponseModel,
  SubscriptionResponseModel,
} from "../api/index.js";
import { formatAbsolute, formatRelative } from "../utils/format.js";
import {
  ANY_OPTION,
  describeErrorType,
  fromSelectValue,
  renderDailyChart,
  renderStat,
  sharedDashboardStyles,
  statusColor,
  toSelectValue,
} from "./shared.js";
import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";

/** Ranges the chart offers. Anything longer than a quarter stops being readable as daily bars. */
const RANGE_OPTIONS = [7, 14, 30, 90];

@customElement("error-dashboard-overview")
export class ErrorDashboardOverviewElement extends UmbLitElement {
  @state() private _overview?: OverviewResponseModel;
  @state() private _subscription?: SubscriptionResponseModel;
  @state() private _days = 30;
  @state() private _host?: string;
  @state() private _loading = true;
  @state() private _savingSubscription = false;

  #notificationContext?: typeof UMB_NOTIFICATION_CONTEXT.TYPE;

  constructor() {
    super();

    this.consumeContext(UMB_NOTIFICATION_CONTEXT, (context) => {
      this.#notificationContext = context;
    });
  }

  override connectedCallback() {
    super.connectedCallback();
    this.#load();
    this.#loadSubscription();
  }

  async #load() {
    this._loading = true;

    const { data } = await tryExecute(
      this,
      getOverview({ query: { days: this._days, host: this._host } }),
      { disableNotifications: false },
    );

    this._overview = data;
    this._loading = false;
  }

  async #loadSubscription() {
    const { data } = await tryExecute(this, getSubscription());
    this._subscription = data;
  }

  async #onSubscriptionToggle(event: Event) {
    const enabled = (event.target as HTMLInputElement).checked;
    this._savingSubscription = true;

    const { data } = await tryExecute(
      this,
      putSubscription({ body: { enabled } }),
      { disableNotifications: false },
    );

    this._savingSubscription = false;
    if (!data) return;

    this._subscription = data;

    // Switching this on when Umbraco cannot send mail would otherwise look like it worked, and the
    // user would find out only by never receiving anything.
    if (enabled && !data.canReceiveEmail) {
      this.#notificationContext?.peek("warning", {
        data: {
          headline: this.localize.term("errorDashboardOverview_subscribedNoEmailHeadline"),
          message: this.localize.term("errorDashboardOverview_subscribedNoEmailMessage"),
        },
      });
      return;
    }

    this.#notificationContext?.peek("positive", {
      data: {
        headline: this.localize.term(
          enabled ? "errorDashboardOverview_alertsOnHeadline" : "errorDashboardOverview_alertsOffHeadline",
        ),
        message: this.localize.term(
          enabled ? "errorDashboardOverview_alertsOnMessage" : "errorDashboardOverview_alertsOffMessage",
        ),
      },
    });
  }

  #onRangeChange = (event: Event) => {
    this._days = Number((event.target as HTMLSelectElement).value);
    this.#load();
  };

  #onHostChange = (event: Event) => {
    this._host = fromSelectValue((event.target as HTMLSelectElement).value);
    this.#load();
  };

  #renderToolbar() {
    const hosts = this._overview?.hosts ?? [];

    return html`
      <div class="toolbar">
        <label for="range">${this.localize.term("errorDashboardShared_period")}</label>
        <uui-select
          id="range"
          label=${this.localize.term("errorDashboardShared_period")}
          .value=${String(this._days)}
          .options=${RANGE_OPTIONS.map((days) => ({
            name: this.localize.term("errorDashboardShared_lastDays", days),
            value: String(days),
            selected: days === this._days,
          }))}
          @change=${this.#onRangeChange}></uui-select>

        ${hosts.length > 1 ? this.#renderHostFilter(hosts) : nothing}

        <span class="spacer"></span>
        ${this.#renderSubscriptionToggle()}
      </div>
    `;
  }

  #renderHostFilter(hosts: Array<string>) {
    return html`
      <label for="host">${this.localize.term("errorDashboardShared_host")}</label>
      <uui-select
        id="host"
        label=${this.localize.term("errorDashboardShared_filterByHost")}
        .value=${toSelectValue(this._host)}
        .options=${[
          {
            name: this.localize.term("errorDashboardShared_allHosts"),
            value: ANY_OPTION,
            selected: !this._host,
          },
          ...hosts.map((host) => ({ name: host, value: host, selected: host === this._host })),
        ]}
        @change=${this.#onHostChange}></uui-select>
    `;
  }

  #renderSubscriptionToggle() {
    if (!this._subscription) return nothing;

    return html`
      <uui-toggle
        label=${this.localize.term("errorDashboardOverview_emailMeAboutSpikes")}
        .checked=${this._subscription.enabled}
        ?disabled=${this._savingSubscription}
        @change=${this.#onSubscriptionToggle}></uui-toggle>
    `;
  }

  #renderStats() {
    const overview = this._overview;
    if (!overview) return nothing;

    const total = overview.series.reduce((sum, point) => sum + point.count, 0);
    const estimatedRequests = overview.series.reduce((sum, point) => sum + point.estimatedRequests, 0);

    return html`
      <div class="stats">
        ${renderStat(
          this.localize.term("errorDashboardOverview_statLast24Hours"),
          this.localize.number(overview.observedLast24Hours),
        )}
        ${renderStat(
          this.localize.term("errorDashboardShared_lastDays", this._days),
          this.localize.number(total),
        )}
        ${estimatedRequests > 0
          ? renderStat(
              this.localize.term("errorDashboardOverview_statErrorRate"),
              this.localize.number(total / estimatedRequests, {
                style: "percent",
                minimumFractionDigits: 2,
                maximumFractionDigits: 2,
              }),
              this.localize.term(
                "errorDashboardOverview_statErrorRateHint",
                this.localize.number(Math.round(estimatedRequests)),
              ),
            )
          : nothing}
        ${overview.pendingRawReports > 0
          ? renderStat(
              this.localize.term("errorDashboardOverview_statNotYetCounted"),
              this.localize.number(overview.pendingRawReports),
              this.localize.term("errorDashboardOverview_statNotYetCountedHint"),
            )
          : nothing}
      </div>
    `;
  }

  /**
   * The detector's reason, assembled here rather than on the server.
   *
   * The server sends which gate held and the raw figures behind it; only the browser knows the
   * editor's language, and only it can render 4.7 as 4,7 for someone who reads it that way. The map
   * is keyed on the API's own enum, so a new gate on the server is a build error here until it has
   * a translation.
   */
  #renderExplanation(anomaly: NonNullable<OverviewResponseModel["anomaly"]>) {
    const keys: Record<AnomalyExplanation, string> = {
      InsufficientHistory: "errorDashboardOverview_explanationInsufficientHistory",
      BelowFloor: "errorDashboardOverview_explanationBelowFloor",
      BelowRatio: "errorDashboardOverview_explanationBelowRatio",
      WithinSpread: "errorDashboardOverview_explanationWithinSpread",
      Anomalous: "errorDashboardOverview_explanationAnomalous",
    };

    const args = anomaly.explanationArgs.map((value) =>
      this.localize.number(value, { maximumFractionDigits: 2 }),
    );

    return this.localize.term(keys[anomaly.explanation], ...args);
  }

  /**
   * The detector's verdict, in words.
   *
   * Showing the reasoning rather than just a red or green light is deliberate: without the "usual for
   * this site" figure nobody can tell whether a quiet result means healthy or means the thresholds
   * are set too high.
   */
  #renderAnomaly() {
    const anomaly = this._overview?.anomaly;
    if (!anomaly) {
      return html`<p class="note">${this.localize.term("errorDashboardOverview_noAssessment")}</p>`;
    }

    return html`
      <div class="stats">
        <uui-tag color=${anomaly.isAnomalous ? "danger" : "positive"} look="primary">
          ${this.localize.term(
            anomaly.isAnomalous ? "errorDashboardOverview_aboveNormal" : "errorDashboardOverview_normal",
          )}
        </uui-tag>
      </div>
      <p class="note">${this.#renderExplanation(anomaly)}</p>
      <p class="note">
        ${this.localize.term("errorDashboardOverview_howJudged", anomaly.baselineDays)}
      </p>
    `;
  }

  #renderBreakdown(byType: Array<ErrorTypeCountResponseModel>, byStatus: Array<StatusCountResponseModel>) {
    if (!byType.length && !byStatus.length) {
      return html`<div class="state">
        <span>${this.localize.term("errorDashboardOverview_nothingReported")}</span>
      </div>`;
    }

    return html`
      <div class="breakdown">
        ${byStatus.map(
          (entry) => html`<uui-tag color=${statusColor(entry.statusCode)} look="secondary">
            HTTP ${entry.statusCode} &middot; ${this.localize.number(entry.count)}
          </uui-tag>`,
        )}
        ${byType
          .filter((entry) => entry.type !== "http.error")
          .map(
            (entry) => html`<uui-tag color="warning" look="secondary">
              ${describeErrorType(this.localize, entry.type)} &middot; ${this.localize.number(entry.count)}
            </uui-tag>`,
          )}
      </div>
    `;
  }

  #renderLastAggregated() {
    const last = this._overview?.lastAggregatedUtc;
    if (!last) {
      return html`<span class="muted">${this.localize.term("errorDashboardOverview_notYetAggregated")}</span>`;
    }

    return html`<span class="muted" title=${formatAbsolute(this.localize, last)}>
      ${this.localize.term("errorDashboardOverview_updatedAgo", formatRelative(this.localize, last))}
    </span>`;
  }

  override render() {
    if (this._loading && !this._overview) {
      return html`<uui-box headline=${this.localize.term("errorDashboardOverview_label")}>
        <div class="state"><uui-loader></uui-loader></div>
      </uui-box>`;
    }

    const overview = this._overview;

    return html`
      <uui-box headline=${this.localize.term("errorDashboardOverview_headline")}>
        <div slot="header-actions">${this.#renderLastAggregated()}</div>
        ${this.#renderToolbar()} ${this.#renderStats()}
        ${overview ? renderDailyChart(this.localize, [...overview.series]) : nothing}
      </uui-box>

      <uui-box headline=${this.localize.term("errorDashboardOverview_isThisUnusual")}>
        ${this.#renderAnomaly()}
      </uui-box>

      <uui-box headline=${this.localize.term("errorDashboardOverview_whatIsFailing")}>
        ${overview ? this.#renderBreakdown([...overview.byType], [...overview.byStatus]) : nothing}
      </uui-box>

      <uui-box headline=${this.localize.term("errorDashboardShared_aboutTheseNumbers")}>
        <p class="note">${this.localize.term("errorDashboardShared_coverageNote")}</p>
      </uui-box>
    `;
  }

  static override styles = [
    UmbTextStyles,
    sharedDashboardStyles,
    css`
      uui-select {
        min-width: 160px;
      }

      p.note {
        margin: 0 0 var(--uui-size-space-3) 0;
      }

      p.note:last-child {
        margin-bottom: 0;
      }
    `,
  ];
}

export default ErrorDashboardOverviewElement;

declare global {
  interface HTMLElementTagNameMap {
    "error-dashboard-overview": ErrorDashboardOverviewElement;
  }
}
