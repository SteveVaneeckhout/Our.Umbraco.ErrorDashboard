import type {
  UmbEntryPointOnInit,
  UmbEntryPointOnUnload,
} from "@umbraco-cms/backoffice/extension-api";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { client } from "../api/client.gen.js";

export const onInit: UmbEntryPointOnInit = async (host, _extensionRegistry) => {
  // Wire the generated API client into the backoffice auth context. configureClient() sets baseUrl +
  // credentials, attaches the cookie-based auth callback with automatic token refresh, and binds the
  // default response interceptors. The framework awaits onInit, so resolving the context here
  // guarantees the client is configured before any dashboard element can call it.
  const authContext = await host.getContext(UMB_AUTH_CONTEXT);
  if (!authContext) {
    console.warn("UMB_AUTH_CONTEXT not available - Error Dashboard API client will not be authenticated");
    return;
  }

  authContext.configureClient(client);
};

export const onUnload: UmbEntryPointOnUnload = (_host, _extensionRegistry) => {
  // Nothing to tear down - the client is owned by the auth context.
};
