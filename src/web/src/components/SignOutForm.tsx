"use client";

import { useRef, type FormEvent, type ReactNode } from "react";
import { forgetThisDevice } from "@/lib/pwa/push";

/**
 * The sign-out form (a POST with the CSRF token). Task J2: before the form goes, this device stops
 * receiving web push for the person signing out (at most a second and a half; without scripts the
 * form works as before, and the device is removed when the push service reports it gone).
 */
export function SignOutForm({ csrfToken, locale, children }: { csrfToken: string; locale: string; children: ReactNode }) {
  const sending = useRef(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    if (sending.current) return;
    event.preventDefault();
    sending.current = true;
    const form = event.currentTarget;
    await forgetThisDevice(csrfToken);
    form.submit();
  }

  return (
    <form action="/auth/logout" method="post" onSubmit={submit}>
      <input type="hidden" name="csrf" value={csrfToken} />
      <input type="hidden" name="locale" value={locale} />
      {children}
    </form>
  );
}
