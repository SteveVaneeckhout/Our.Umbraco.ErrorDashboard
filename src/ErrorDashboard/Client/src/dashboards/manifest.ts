// These are operational statistics about the site rather than editorial data, so they belong in
// Settings. The API enforces the matching SectionAccessSettings policy server-side.
const settingsSectionCondition = {
  alias: "Umb.Condition.SectionAlias",
  match: "Umb.Section.Settings",
} as const;

// Umbraco renders every dashboard registered against a section as a tab, so three manifests give
// three tabs with no routing of our own. Higher weight sorts first.
export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Error Dashboard Overview",
    alias: "ErrorDashboard.Overview",
    type: "dashboard",
    js: () => import("./overview.element.js"),
    weight: 100,
    meta: {
      label: "#errorDashboardOverview_label",
      pathname: "errors",
    },
    conditions: [settingsSectionCondition],
  },
  {
    name: "Error Dashboard Pages",
    alias: "ErrorDashboard.Pages",
    type: "dashboard",
    js: () => import("./pages.element.js"),
    weight: 90,
    meta: {
      label: "#errorDashboardPages_label",
      pathname: "error-pages",
    },
    conditions: [settingsSectionCondition],
  },
  {
    name: "Error Dashboard Alerts",
    alias: "ErrorDashboard.Alerts",
    type: "dashboard",
    js: () => import("./alerts.element.js"),
    weight: 80,
    meta: {
      label: "#errorDashboardAlerts_label",
      pathname: "error-alerts",
    },
    // Shows who else is subscribed and can trigger a run that sends mail, so admins only. The
    // subscribers and recompute endpoints enforce the same rule server-side.
    conditions: [settingsSectionCondition, { alias: "Umb.Condition.CurrentUser.IsAdmin" }],
  },
];
