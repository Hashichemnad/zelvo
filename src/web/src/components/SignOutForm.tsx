"use client";

import type { FormEvent, ReactNode } from "react";
import { takeRememberedEndpoint } from "@/lib/pwa/push";

/**
 * The sign-out form (a POST with the CSRF token). Task J2: it also sends this device's push address,
 * so the server removes the device for the person signing out; the form is never held up (without
 * scripts it works as before, and the device goes when the push service reports it gone).
 */
export function SignOutForm({ csrfToken, locale, children }: { csrfToken: string; locale: string; children: ReactNode }) {
  function submit(event: FormEvent<HTMLFormElement>) {
    const endpoint = takeRememberedEndpoint();
    const field = event.currentTarget.elements.namedItem("pushEndpoint");
    if (endpoint && field instanceof HTMLInputElement) field.value = endpoint;
  }

  return (
    <form action="/auth/logout" method="post" onSubmit={submit}>
      <input type="hidden" name="csrf" value={csrfToken} />
      <input type="hidden" name="locale" value={locale} />
      <input type="hidden" name="pushEndpoint" defaultValue="" />
      {children}
    </form>
  );
}
