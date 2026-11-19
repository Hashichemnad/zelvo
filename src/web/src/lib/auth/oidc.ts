import "server-only";
import * as oidc from "openid-client";
import { getAuthConfig, type AuthConfig } from "./config";

/**
 * OpenID Connect client for the tenant's Keycloak realm. Browsers use Keycloak's public address;
 * this server reaches the same realm over the internal address, so requests to the public
 * address are sent to the internal one (the issuer stays the public address).
 */
const cached = new Map<string, Promise<oidc.Configuration>>();

function backchannelFetch(config: AuthConfig): oidc.CustomFetch {
  return (url, options) => {
    const target = url.startsWith(config.keycloakPublicUrl)
      ? config.keycloakInternalUrl + url.slice(config.keycloakPublicUrl.length)
      : url;
    return fetch(target, { ...options, signal: AbortSignal.timeout(10_000) } as RequestInit);
  };
}

export function getOidcConfiguration(config: AuthConfig = getAuthConfig()): Promise<oidc.Configuration> {
  // One entry per realm: a demo installation serves a realm per sample organisation.
  const key = `${config.issuer.href}|${config.clientId}`;
  let configuration = cached.get(key);
  if (!configuration) {
    const customFetch = backchannelFetch(config);
    configuration = oidc
      .discovery(config.issuer, config.clientId, undefined, oidc.ClientSecretPost(config.clientSecret), {
        execute: config.allowHttpIdentityProvider && config.issuer.protocol === "http:" ? [oidc.allowInsecureRequests] : [],
        [oidc.customFetch]: customFetch,
      })
      .then((discovered) => {
        discovered[oidc.customFetch] = customFetch;
        return discovered;
      });
    // Try again on the next request when discovery fails (for example Keycloak still starting).
    configuration.catch(() => {
      cached.delete(key);
    });
    cached.set(key, configuration);
  }
  return configuration;
}

export { oidc };
