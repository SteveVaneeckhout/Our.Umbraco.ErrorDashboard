export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Error Dashboard Entrypoint",
    alias: "ErrorDashboard.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint.js"),
  },
];
