import { getHosts, getPages, getReferrers } from "../api/index.js";
import type {
  OffendingPageResponseModel,
  PageSortField,
  ReferrersResponseModel,
  SortDirection,
} from "../api/index.js";
import { formatAbsolute, formatRelative } from "../utils/format.js";
import {
  ANY_OPTION,
  PAGE_SIZE,
  describeErrorType,
  fromSelectValue,
  sharedDashboardStyles,
  statusColor,
  toSelectValue,
} from "./shared.js";
import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { UMB_EDIT_DOCUMENT_WORKSPACE_PATH_PATTERN } from "@umbraco-cms/backoffice/document";

/** Maps a table column alias onto the sort field the API understands. */
const SORT_FIELD_BY_ALIAS: Record<string, PageSortField> = {
  path: "Path",
  count: "Count",
  lastSeen: "LastSeen",
};

const RANGE_OPTIONS = [7, 14, 30, 90];

/**
 * The path the aggregator folds a day's long tail into once the per-day cap is hit. Must match
 * StatUrlDailyDto.OtherPath on the server, which is what is actually stored.
 */
const OTHER_PATH = "(other)";

/** The status filters worth offering. Anything else is rare enough to reach via the type filter. */
const STATUS_OPTIONS = [404, 500, 403, 400, 502, 503];

@customElement("error-dashboard-pages")
export class ErrorDashboardPagesElement extends UmbLitElement {
  @state() private _pages: Array<OffendingPageResponseModel> = [];
  @state() private _total = 0;
  @state() private _page = 1;
  @state() private _days = 30;
  @state() private _host?: string;
  @state() private _statusCode?: number;
  @state() private _hosts: Array<string> = [];
  @state() private _orderingColumn = "count";
  @state() private _orderingDesc = true;
  @state() private _loading = true;

  /** The row whose referrers are open, keyed by {@link ErrorDashboardPagesElement.rowKey}. */
  @state() private _openRow?: string;
  @state() private _referrers?: ReferrersResponseModel;
  @state() private _referrersLoading = false;

  /**
   * Identifies one row of the table.
   *
   * A URL appears once per problem it produced - the same path can be both a 404 and a certificate
   * error - so the path alone does not identify a row, and keying the open state on it expanded
   * every row sharing that URL at once. Referrers themselves are still a property of the URL, not of
   * the problem, so all of those rows do show the same list; they just open one at a time now.
   */
  static rowKey(page: OffendingPageResponseModel): string {
    return `${page.host}|${page.path}|${page.type}|${page.statusCode ?? ""}`;
  }

  override connectedCallback() {
    super.connectedCallback();
    this.#loadHosts();
    this.#load();
  }

  async #loadHosts() {
    const { data } = await tryExecute(this, getHosts());
    this._hosts = data ? [...data] : [];
  }

  async #load() {
    this._loading = true;

    const { data } = await tryExecute(
      this,
      getPages({
        query: {
          days: this._days,
          host: this._host,
          statusCode: this._statusCode,
          skip: (this._page - 1) * PAGE_SIZE,
          take: PAGE_SIZE,
          orderBy: SORT_FIELD_BY_ALIAS[this._orderingColumn] ?? "Count",
          direction: (this._orderingDesc ? "Descending" : "Ascending") satisfies SortDirection,
        },
      }),
      { disableNotifications: false },
    );

    this._pages = data?.items ? [...data.items] : [];
    this._total = data?.total ?? 0;
    this._loading = false;

    // The open row may not exist in the new result set, and a detail panel for a row you can no
    // longer see is just confusing.
    this.#closeReferrers();
  }

  #closeReferrers() {
    this._openRow = undefined;
    this._referrers = undefined;
  }

  async #showReferrers(page: OffendingPageResponseModel) {
    const key = ErrorDashboardPagesElement.rowKey(page);

    if (this._openRow === key) {
      this.#closeReferrers();
      return;
    }

    this._openRow = key;
    this._referrers = undefined;
    this._referrersLoading = true;

    const { data } = await tryExecute(
      this,
      getReferrers({ query: { host: page.host, path: page.path, days: this._days } }),
      { disableNotifications: false },
    );

    // Guard against a slow response for a row the user has since closed or replaced.
    if (this._openRow === key) {
      this._referrers = data;
    }

    this._referrersLoading = false;
  }

  #onRangeChange = (event: Event) => {
    this._days = Number((event.target as HTMLSelectElement).value);
    this._page = 1;
    this.#load();
  };

  #onHostChange = (event: Event) => {
    this._host = fromSelectValue((event.target as HTMLSelectElement).value);
    this._page = 1;
    this.#load();
  };

  #onStatusChange = (event: Event) => {
    const value = fromSelectValue((event.target as HTMLSelectElement).value);
    this._statusCode = value ? Number(value) : undefined;
    this._page = 1;
    this.#load();
  };

  #onPageChange = (event: Event) => {
    this._page = (event.target as HTMLElement & { current: number }).current;
    this.#load();
  };

  #onSort(alias: string) {
    // Clicking the active column flips direction; a new column starts descending, which for counts
    // and dates is the order anyone actually wants first.
    if (this._orderingColumn === alias) {
      this._orderingDesc = !this._orderingDesc;
    } else {
      this._orderingColumn = alias;
      this._orderingDesc = true;
    }

    this._page = 1;
    this.#load();
  }

  #renderHeadCell(label: string, alias?: string) {
    if (!alias) {
      return html`<uui-table-head-cell>${label}</uui-table-head-cell>`;
    }

    const active = this._orderingColumn === alias;

    return html`
      <uui-table-head-cell>
        <button
          class="sort"
          aria-sort=${active ? (this._orderingDesc ? "descending" : "ascending") : "none"}
          @click=${() => this.#onSort(alias)}>
          ${label}
          <uui-symbol-sort ?active=${active} ?descending=${this._orderingDesc}></uui-symbol-sort>
        </button>
      </uui-table-head-cell>
    `;
  }

  #renderRow(page: OffendingPageResponseModel) {
    const isOpen = this._openRow === ErrorDashboardPagesElement.rowKey(page);

    return html`
      <uui-table-row class=${isOpen ? "is-open" : ""}>
        <uui-table-cell>${this.#renderPathCell(page, isOpen)}</uui-table-cell>
        <uui-table-cell>
          ${page.statusCode
            ? html`<uui-tag color=${statusColor(page.statusCode)} look="secondary">
                HTTP ${page.statusCode}
              </uui-tag>`
            : html`<uui-tag color="warning" look="secondary" title=${page.type}>
                ${describeErrorType(this.localize, page.type)}
              </uui-tag>`}
        </uui-table-cell>
        <uui-table-cell><span class="count">${this.localize.number(page.count)}</span></uui-table-cell>
        <uui-table-cell>
          <span title=${formatAbsolute(this.localize, page.lastSeenUtc)}
            >${formatRelative(this.localize, page.lastSeenUtc)}</span
          >
        </uui-table-cell>
      </uui-table-row>

      ${isOpen
        ? html`<uui-table-row class="detail-row">
            <uui-table-cell aria-colspan="4">${this.#renderReferrers(page.path)}</uui-table-cell>
          </uui-table-row>`
        : nothing}
    `;
  }

  /**
   * The path cell: a disclosure arrow, then the path itself.
   *
   * The two are separated deliberately. Expanding referrers and opening the page are different
   * intentions, and making the whole path a toggle meant there was no way to reach the content node
   * at all. Where the path resolves to a document the path becomes a link; where it does not - the
   * usual case for a genuine 404 - it stays plain text, which is itself the answer.
   *
   * The arrow itself is only there when there is something behind it: the server says whether
   * anything is known to link to the URL, over the same window and raw table the panel reads from.
   */
  #renderPathCell(page: OffendingPageResponseModel, isOpen: boolean) {
    const path = this.#displayPath(page.path);

    const label = page.documentKey
      ? html`<a
          class="path"
          href=${UMB_EDIT_DOCUMENT_WORKSPACE_PATH_PATTERN.generateAbsolute({ unique: page.documentKey })}
          title=${this.localize.term("errorDashboardPages_editTitle", `${page.host}${page.path}`)}>
          ${path}
        </a>`
      : html`<span class="path" title=${`${page.host}${page.path}`}>${path}</span>`;

    return html`
      <div class="path-cell">
        ${page.hasReferrers
          ? html`<button
              class="disclosure"
              aria-expanded=${isOpen}
              aria-label=${this.localize.term(
                isOpen ? "errorDashboardPages_hideLinksAria" : "errorDashboardPages_showLinksAria",
                page.path,
              )}
              title=${this.localize.term(
                isOpen ? "errorDashboardPages_hideLinksTitle" : "errorDashboardPages_showLinksTitle",
              )}
              @click=${() => this.#showReferrers(page)}>
              <uui-symbol-expand ?open=${isOpen}></uui-symbol-expand>
            </button>`
          : // An arrow onto an empty panel is a promise the row cannot keep, so a row nothing is
            // known to link to gets an invisible one of the same size instead - dropping it outright
            // would unalign every path in the column.
            html`<span class="disclosure disclosure--none" aria-hidden="true">
              <uui-symbol-expand></uui-symbol-expand>
            </span>`}
        ${label}
        ${page.isUnpublished
          ? html`<uui-tag
              color="warning"
              look="secondary"
              title=${this.localize.term("errorDashboardPages_unpublishedTitle")}>
              ${this.localize.term("errorDashboardPages_unpublished")}
            </uui-tag>`
          : nothing}
      </div>
    `;
  }

  /**
   * The one "path" that is not a path.
   *
   * Once a day has as many distinct paths as the aggregator allows, the long tail is folded into a
   * single row stored under the literal `(other)`. That literal is *data* - it is in the database and
   * in the API response - so it is translated only on the way to the screen, never on the way in.
   */
  #displayPath(path: string) {
    return path === OTHER_PATH ? this.localize.term("errorDashboardPages_otherPaths") : path;
  }

  #renderToolbar() {
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

        <label for="status">${this.localize.term("errorDashboardPages_status")}</label>
        <uui-select
          id="status"
          label=${this.localize.term("errorDashboardPages_filterByStatus")}
          .value=${toSelectValue(this._statusCode)}
          .options=${[
            {
              name: this.localize.term("errorDashboardPages_anyStatus"),
              value: ANY_OPTION,
              selected: !this._statusCode,
            },
            ...STATUS_OPTIONS.map((status) => ({
              name: String(status),
              value: String(status),
              selected: status === this._statusCode,
            })),
          ]}
          @change=${this.#onStatusChange}></uui-select>

        ${this._hosts.length > 1 ? this.#renderHostFilter() : nothing}

        <span class="spacer"></span>
        <span class="muted count">${this.localize.term("errorDashboardPages_totalUrls", this._total)}</span>
      </div>
    `;
  }

  #renderHostFilter() {
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
          ...this._hosts.map((host) => ({ name: host, value: host, selected: host === this._host })),
        ]}
        @change=${this.#onHostChange}></uui-select>
    `;
  }

  #renderBody() {
    if (this._loading) {
      return html`<div class="state"><uui-loader></uui-loader></div>`;
    }

    if (!this._pages.length) {
      return html`<div class="state">
        <span>${this.localize.term("errorDashboardPages_empty")}</span>
      </div>`;
    }

    // Built on uui-table rather than umb-table so a detail row can sit directly beneath the row it
    // belongs to. umb-table renders its own rows from a flat item list and has nowhere to put one.
    return html`
      <div class="table-container">
        <uui-scroll-container>
          <uui-table>
            <uui-table-head>
              ${this.#renderHeadCell(this.localize.term("errorDashboardPages_columnPath"), "path")}
              ${this.#renderHeadCell(this.localize.term("errorDashboardPages_columnProblem"))}
              ${this.#renderHeadCell(this.localize.term("errorDashboardPages_columnErrors"), "count")}
              ${this.#renderHeadCell(this.localize.term("errorDashboardPages_columnLastSeen"), "lastSeen")}
            </uui-table-head>
            ${this._pages.map((page) => this.#renderRow(page))}
          </uui-table>
        </uui-scroll-container>
      </div>
    `;
  }

  /**
   * What still links to the selected broken URL.
   *
   * Referrers are not aggregated — a broken URL can be linked from anywhere, so the cardinality is
   * unbounded in a way the per-path totals are not. That makes this a raw-table lookup, which is why
   * it is fetched on demand and why the window can be shorter than the one the table is showing.
   */
  #renderReferrers(path: string) {
    // Two nested elements on purpose: the outer one holds the panel's place in the table without
    // taking part in how the columns are sized, the inner one is the panel. See the styles.
    return html`
      <div class="referrers">
        <div class="referrers-panel">
          <div class="referrers-head">
            <!-- htmlString, because the path is monospaced mid-sentence and a translation needs to
                 be able to move it within the phrase. Arguments are escaped before interpolation. -->
            <strong
              >${this.localize.htmlString("#errorDashboardPages_linkingTo", this.#displayPath(path))}</strong
            >
          </div>
          ${this.#renderReferrerBody()}
        </div>
      </div>
    `;
  }

  #renderReferrerBody() {
    if (this._referrersLoading) {
      return html`<div class="state"><uui-loader></uui-loader></div>`;
    }

    const referrers = this._referrers;
    if (!referrers?.items.length) {
      return html`<p class="note">
        ${this.localize.term(
          "errorDashboardPages_noReferrers",
          referrers?.availableDays ?? this._days,
        )}
      </p>`;
    }

    const windowNote =
      referrers.availableDays < this._days
        ? html`<p class="note">
            ${this.localize.term(
              "errorDashboardPages_referrerWindowNote",
              referrers.availableDays,
              this._days,
            )}
          </p>`
        : nothing;

    return html`
      <ul class="referrer-list">
        ${referrers.items.map(
          (entry) => html`
            <li>
              <span class="count"
                >${this.localize.term(
                  "errorDashboardPages_referrerCount",
                  this.localize.number(entry.count),
                )}</span
              >
              ${entry.referrer
                ? html`<a href=${entry.referrer} target="_blank" rel="noopener noreferrer" class="path">
                    ${entry.referrer}
                  </a>`
                : html`<span class="muted">${this.localize.term("errorDashboardPages_noReferrer")}</span>`}
              <span class="muted" title=${formatAbsolute(this.localize, entry.lastSeenUtc)}>
                ${formatRelative(this.localize, entry.lastSeenUtc)}
              </span>
            </li>
          `,
        )}
      </ul>
      ${windowNote}
    `;
  }

  #renderPagination() {
    const totalPages = Math.ceil(this._total / PAGE_SIZE);
    if (totalPages <= 1) return nothing;

    return html`<div class="footer">
      <uui-pagination .current=${this._page} .total=${totalPages} @change=${this.#onPageChange}></uui-pagination>
    </div>`;
  }

  override render() {
    return html`
      <uui-box headline=${this.localize.term("errorDashboardPages_headline")}>
        <div slot="header-actions" class="muted">
          ${this.localize.term("errorDashboardPages_description")}
        </div>
        ${this.#renderToolbar()} ${this.#renderBody()} ${this.#renderPagination()}
      </uui-box>

      <uui-box headline=${this.localize.term("errorDashboardShared_aboutTheseNumbers")}>
        <p class="note">${this.localize.term("errorDashboardShared_coverageNote")}</p>
        <p class="note">${this.localize.term("errorDashboardPages_missingAssetsNote")}</p>
      </uui-box>
    `;
  }

  static override styles = [
    UmbTextStyles,
    sharedDashboardStyles,
    css`
      uui-select {
        min-width: 140px;
      }

      /* The width the referrer panel measures itself against; see .referrers below. */
      .table-container {
        container-type: inline-size;
      }

      .path-cell {
        display: flex;
        align-items: center;
        gap: var(--uui-size-space-2);
      }

      /* A bare button so the arrow is keyboard reachable and announces its state. */
      .disclosure {
        display: inline-flex;
        align-items: center;
        background: none;
        border: none;
        padding: 0;
        margin: 0;
        color: var(--uui-color-text-alt);
        cursor: pointer;
      }

      .disclosure:hover {
        color: var(--uui-color-text);
      }

      /* Keeps the column aligned without offering anything to click. */
      .disclosure--none {
        visibility: hidden;
        cursor: default;
      }

      a.path {
        color: var(--uui-color-interactive-emphasis);
        text-decoration: none;
      }

      a.path:hover {
        text-decoration: underline;
      }

      /* Header sort control, matching what umb-table gives you for free. */
      .sort {
        display: inline-flex;
        align-items: center;
        gap: var(--uui-size-space-2);
        background: none;
        border: none;
        padding: 0;
        font: inherit;
        font-weight: bold;
        color: inherit;
        cursor: pointer;
      }

      uui-table-row.is-open {
        background: var(--uui-color-surface-alt);
      }

      /* The detail row belongs to the row above it, so it carries no top border of its own. */
      uui-table-row.detail-row uui-table-cell {
        border-top: none;
        padding-top: 0;
      }

      /*
       * The panel spans the table the hard way, because colspan cannot do it here: browsers only
       * honour colspan on a real td, not on a custom element with display: table-cell, so the detail
       * cell is an ordinary cell in the first column. Left to itself, uui-table's default auto
       * layout widened that column to fit a panel full of long referrer URLs and squeezed the other
       * three - the columns visibly jumped on every expand.
       *
       * The fix is to sever the panel's width from the column's. A cell whose content has a definite
       * width contributes exactly that width, so a zero-width outer box takes the table out of the
       * argument entirely - and what overflows it never reaches the column calculation, while still
       * giving the row its height. The panel itself then measures against the table container, which
       * is a query container for exactly this purpose.
       */
      .referrers {
        width: 0;
      }

      .referrers-panel {
        box-sizing: border-box;
        width: calc(100cqw - 2 * var(--uui-size-5) - 2px);
        padding: var(--uui-size-space-3) 0 var(--uui-size-space-2) var(--uui-size-space-6);
        border-left: 2px solid var(--uui-color-divider-emphasis);
      }

      .referrers-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--uui-size-space-3);
        margin-bottom: var(--uui-size-space-3);
      }

      .referrer-list {
        list-style: none;
        margin: 0;
        padding: 0;
      }

      .referrer-list li {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--uui-size-space-3);
        padding: var(--uui-size-space-2) 0;
        border-bottom: 1px solid var(--uui-color-divider);
      }

      .referrer-list li:last-child {
        border-bottom: none;
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

export default ErrorDashboardPagesElement;

declare global {
  interface HTMLElementTagNameMap {
    "error-dashboard-pages": ErrorDashboardPagesElement;
  }
}
