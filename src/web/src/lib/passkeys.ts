/** Passkeys (module 19, task J3), as the API answers them. */
export type PasskeyMode = "Off" | "Allowed" | "RequiredForStaff";

export interface Passkey {
  id: string;
  label: string | null;
  createdAt: string | null;
}

export interface MyPasskeys {
  mode: PasskeyMode;
  /** Whether the person's roles need a second factor, which a passkey sign-in gives. */
  secondFactor: boolean;
  passkeys: Passkey[];
}

/** The address that asks Keycloak to add a passkey to the signed-in account and comes back to `returnTo`. */
export function addPasskeyHref(locale: string, returnTo: string, secondFactor: boolean): string {
  const params = new URLSearchParams({ action: "passkey", locale, returnTo });
  if (secondFactor) params.set("mfa", "1");
  return `/auth/login?${params}`;
}
