import {
  PublicClientApplication,
  type Configuration,
  type PopupRequest,
} from "@azure/msal-browser";
import { config } from "../config";

export const msalEnabled =
  config.auth.enabled === true &&
  typeof config.auth.tenantId === "string" &&
  config.auth.tenantId.length > 0 &&
  typeof config.auth.clientId === "string" &&
  config.auth.clientId.length > 0;

const authority = msalEnabled
  ? `https://login.microsoftonline.com/${config.auth.tenantId}`
  : undefined;

const msalConfig: Configuration | undefined = msalEnabled
  ? {
      auth: {
        clientId: config.auth.clientId!,
        authority,
        redirectUri: window.location.origin,
      },
      cache: {
        cacheLocation: "sessionStorage",
      },
    }
  : undefined;

export const msalInstance = msalConfig
  ? new PublicClientApplication(msalConfig)
  : undefined;

export const loginRequest: PopupRequest = {
  scopes: [
    "openid",
    "profile",
    "email",
    ...(config.auth.apiScope ? [config.auth.apiScope] : []),
  ],
};
