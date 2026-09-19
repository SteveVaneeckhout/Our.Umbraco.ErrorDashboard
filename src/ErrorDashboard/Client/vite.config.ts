import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: "src/bundle.manifests.ts", // Bundle registers one or more manifests
      formats: ["es"],
      fileName: "error-dashboard",
    },
    outDir: "../wwwroot/App_Plugins/ErrorDashboard", // your web component will be saved in this location
    emptyOutDir: true,
    sourcemap: false, // .js.map files would otherwise ship inside the NuGet package,
    rollupOptions: {
      external: [/^@umbraco/],
    },
  },
});
