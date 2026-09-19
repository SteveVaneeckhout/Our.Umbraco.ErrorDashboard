import { getAlerts, getSettings, getSubscribers, postRecompute } from "../api/index.js";
import type {
  AlertResponseModel,
  SettingsResponseModel,
  SubscriberResponseModel,
} from "../api/index.js";
import { formatAbsolute, formatRelative } from "../utils/format.js";
import { PAGE_SIZE, sharedDashboardStyles } from "./shared.js";
import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";

@customElement("error-dashboard-alerts")
export class ErrorDashboardAlertsElement extends UmbLitElement {
  @state() private _alerts: Array<AlertResponseModel> = [];
  @state() private _total = 0;
  @state() private _page = 1;
  @state() private _subscribers: Array<SubscriberResponseModel> = [];
  @state() private _settings?: SettingsResponseModel;
  @state() private _loading = true;
  @state() private _recomputing = false;

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
  }

  async #load() {
    this._loading = true;
    await Promise.all([this.#loadAlerts(), this.#loadSubscribers(), this.#loadSettings()]);
    this._loading = false;
  }

  async #loadAlerts() {
    const { data } = await tryExecute(
      this,
      getAlerts({ query: { skip: (this._page - 1) * PAGE_SIZE, take: PAGE_SIZE } }),
      { disableNotifications: false },
    );

    this._alerts = data?.items ? [...data.items] : [];
    this._total = data?.total ?? 0;
  }

  async #loadSubscribers() {
    const { data } = await tryExecute(this, getSubscribers());
    this._subscribers = data ? [...data] : [];
  }

  async #loadSettings() {
    const { data } = await tryExecute(this, getSettings());
    this._settings = data;
  }

  async #onRecompute() {
    this._recomputing = true;

    const { data } = await tryExecute(this, postRecompute(), { disableNotifications: false });

    this._recomputing = false;
    if (!data) return;

    this.#notificationContext?.peek(data.alertRaised ? "warning" : "positive", {
      data: {
        headline: this.localize.term("errorDashboardAlerts_recomputedHeadline"),
        // Composed here rather than sent as prose: the server has the three figures, the browser has
        // the language.
        message: this.localize.term(
          data.alertRaised
            ? "errorDashboardAlerts_recomputedWithAlert"
            : "errorDashboardAlerts_recomputedNoAlert",
          data.rowsAggregated,
          data.rowsPruned,
        ),
      },
    });

    await this.#load();
  }

  #onPageChange = (event: Event) => {
    this._page = (event.target as HTMLElement & { current: number }).current;
    this.#loadAlerts();
  };

  #renderAlerts() {
    if (this._loading) {
      return html`<div class="state"><uui-loader></uui-loader></div>`;
    }

    if (!this._alerts.length) {
      return html`<div class="state">
        <span>${this.localize.term("errorDashboardAlerts_noAlerts")}</span>
      </div>`;
    }

    return html`
      <div class="alert-list">
        ${this._alerts.map(
          (alert) => html`
            <div class="alert">
              <div class="alert-head">
                <strong>${alert.host}</strong>
                <span class="muted" title=${formatAbsolute(this.localize, alert.raisedUtc)}>
                  ${formatRelative(this.localize, alert.raisedUtc)}
                </span>
              </div>
              <div class="alert-body">
                <!-- htmlString, because the figures are called out mid-sentence and a translation
                     needs to be free to reorder them. Arguments are escaped before interpolation. -->
                ${this.localize.htmlString(
                  "#errorDashboardAlerts_alertBody",
                  this.localize.number(alert.observed),
                  this.localize.number(alert.median, { maximumFractionDigits: 1 }),
                  this.localize.number(alert.score, { maximumFractionDigits: 1 }),
                )}
                ${this.localize.term("errorDashboardAlerts_alertEmailed", alert.recipientCount)}
              </div>
              ${alert.topPaths.length
                ? html`<ol class="alert-paths">
                    ${alert.topPaths.map((path) => html`<li class="path">${path}</li>`)}
                  </ol>`
                : nothing}
            </div>
          `,
        )}
      </div>
      ${this.#renderPagination()}
    `;
  }

  #renderPagination() {
    const totalPages = Math.ceil(this._total / PAGE_SIZE);
    if (totalPages <= 1) return nothing;

    return html`<div class="footer">
      <uui-pagination .current=${this._page} .total=${totalPages} @change=${this.#onPageChange}></uui-pagination>
    </div>`;
  }

  #renderSubscribers() {
    if (!this._subscribers.length) {
      return html`<div class="state">
        <span>${this.localize.term("errorDashboardAlerts_noSubscribers")}</span>
      </div>`;
    }

    return html`
      <ul class="subscribers">
        ${this._subscribers.map(
          (subscriber) => html`
            <li>
              <span>${subscriber.name}</span>
              <span class="muted">${subscriber.email}</span>
              <span class="muted">
                ${subscriber.lastNotifiedUtc
                  ? html`${this.localize.term("errorDashboardAlerts_lastEmailed")}
                      <span title=${formatAbsolute(this.localize, subscriber.lastNotifiedUtc)}>
                        ${formatRelative(this.localize, subscriber.lastNotifiedUtc)}
                      </span>`
                  : this.localize.term("errorDashboardAlerts_neverEmailed")}
              </span>
            </li>
          `,
        )}
      </ul>
    `;
  }

  /**
   * The thresholds, stated rather than implied.
   *
   * Whether an alert is tuned right is impossible to judge from the alert history alone - you need
   * to know what would have had to happen for one to fire.
   */
  #renderSettings() {
    const settings = this._settings;
    if (!settings) return nothing;

    // The two notes carry <code> around configuration keys, so they go through htmlString. The keys
    // inside them are literal identifiers and stay put in every language.
    return html`
      ${settings.canSendEmail
        ? nothing
        : html`<uui-tag color="danger" look="primary"
              >${this.localize.term("errorDashboardAlerts_emailNotConfigured")}</uui-tag
            >
            <p class="note">
              ${this.localize.htmlString("#errorDashboardAlerts_emailNotConfiguredNote")}
            </p>`}

      <dl class="settings">
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingAlerting")}</dt>
          <dd>
            ${this.localize.term(
              settings.alertsEnabled ? "errorDashboardAlerts_on" : "errorDashboardAlerts_off",
            )}
          </dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingCollectorPath")}</dt>
          <dd class="path">${settings.reportPath}</dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingBaseline")}</dt>
          <dd>
            ${this.localize.term(
              "errorDashboardAlerts_baselineValue",
              settings.baselineDays,
              settings.minBaselineDays,
            )}
          </dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingTriggerScore")}</dt>
          <dd>${this.localize.number(settings.scoreThreshold)}</dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingMinimumErrors")}</dt>
          <dd>${this.localize.number(settings.minObserved)}</dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingMinimumMultiple")}</dt>
          <dd>
            ${this.localize.term(
              "errorDashboardAlerts_minimumMultipleValue",
              this.localize.number(settings.minRatio),
            )}
          </dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingCooldown")}</dt>
          <dd>${this.localize.term("errorDashboardAlerts_cooldownValue", settings.cooldownHours)}</dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingRawRetention")}</dt>
          <dd>
            ${this.localize.term("errorDashboardAlerts_rawRetentionValue", settings.rawRetentionDays)}
          </dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingRateLimit")}</dt>
          <dd>
            ${this.localize.term("errorDashboardAlerts_rateLimitValue", settings.rateLimitPerMinute)}
          </dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingAcceptedHosts")}</dt>
          <dd>
            ${settings.acceptedHosts.length
              ? this.localize.list(settings.acceptedHosts)
              : html`<uui-tag color="warning" look="secondary"
                  >${this.localize.term("errorDashboardAlerts_requestHostOnly")}</uui-tag
                >`}
          </dd>
        </div>
        <div>
          <dt>${this.localize.term("errorDashboardAlerts_settingSuccessSampling")}</dt>
          <dd>
            ${settings.successFraction > 0
              ? this.localize.term(
                  "errorDashboardAlerts_successSamplingValue",
                  this.localize.number(settings.successFraction * 100, { maximumFractionDigits: 1 }),
                )
              : this.localize.term("errorDashboardAlerts_successSamplingOff")}
          </dd>
        </div>
      </dl>

      <p class="note">${this.localize.term("errorDashboardAlerts_thresholdNote")}</p>

      ${settings.acceptedHosts.length
        ? nothing
        : html`<p class="note">${this.localize.htmlString("#errorDashboardAlerts_noDomainsNote")}</p>`}
    `;
  }

  override render() {
    const recomputeLabel = this.localize.term("errorDashboardAlerts_recomputeNow");

    return html`
      <uui-box headline=${this.localize.term("errorDashboardAlerts_alertHistory")}>
        <div slot="header-actions">
          <uui-button
            look="secondary"
            label=${recomputeLabel}
            ?disabled=${this._recomputing}
            @click=${this.#onRecompute}>
            ${this._recomputing ? this.localize.term("errorDashboardAlerts_recomputing") : recomputeLabel}
          </uui-button>
        </div>
        ${this.#renderAlerts()}
      </uui-box>

      <uui-box headline=${this.localize.term("errorDashboardAlerts_subscribers")}>
        ${this.#renderSubscribers()}
      </uui-box>

      <uui-box headline=${this.localize.term("errorDashboardAlerts_settings")}>
        ${this.#renderSettings()}
      </uui-box>
    `;
  }

  static override styles = [
    UmbTextStyles,
    sharedDashboardStyles,
    css`
      .alert-list {
        display: flex;
        flex-direction: column;
        gap: var(--uui-size-space-4);
      }

      .alert {
        border-left: 3px solid var(--uui-color-danger);
        padding-left: var(--uui-size-space-4);
      }

      .alert-head {
        display: flex;
        justify-content: space-between;
        gap: var(--uui-size-space-3);
      }

      .alert-body {
        margin-top: var(--uui-size-space-1);
      }

      .alert-paths {
        margin: var(--uui-size-space-2) 0 0 0;
        padding-left: var(--uui-size-space-5);
        font-size: var(--uui-type-small-size);
        color: var(--uui-color-text-alt);
      }

      .subscribers {
        list-style: none;
        margin: 0;
        padding: 0;
      }

      .subscribers li {
        display: flex;
        flex-wrap: wrap;
        gap: var(--uui-size-space-3);
        padding: var(--uui-size-space-2) 0;
        border-bottom: 1px solid var(--uui-color-divider);
      }

      .subscribers li:last-child {
        border-bottom: none;
      }

      .settings {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
        gap: var(--uui-size-space-3);
        margin: 0 0 var(--uui-size-space-4) 0;
      }

      .settings dt {
        font-size: var(--uui-type-small-size);
        color: var(--uui-color-text-alt);
      }

      .settings dd {
        margin: 0;
      }

      p.note {
        margin: var(--uui-size-space-3) 0 0 0;
      }

      code {
        font-family: var(--uui-font-monospace, monospace);
      }
    `,
  ];
}

export default ErrorDashboardAlertsElement;

declare global {
  interface HTMLElementTagNameMap {
    "error-dashboard-alerts": ErrorDashboardAlertsElement;
  }
}
