# Online payments (task E8, module 15)

Covers module 15 in `docs/requirements.md`: Settings → Payments with one generic gateway connector (for example AjmanPay), mapping, test connection and sandbox; per-service fee rules and timing; the pay flow with server-side status confirmation and processing once; bilingual e-receipts; refunds; daily reconciliation and Excel export.

Product owner decisions (2026-11-25):

- **On completion**: the request is resolved and closes only once paid; no expiry. **On submission and after approval**: the request waits in Awaiting payment; at the expiry it is closed with the reason "payment not received".
- **Reconciliation**: each payment of the day is checked with the gateway's status API (works with any gateway); the gateway's settlement file (CSV or Excel) can be imported to find transactions Zelvo does not know.

## Code

| Piece | Where |
| --- | --- |
| Tables `payment_gateways`, `payments`, `payment_attempts`, `payment_refunds`, `payment_reconciliations` (row-level security; migration `Payments`); `service_requests.hold = Payment` | `src/api/Zelvo.Data/Payments/PaymentEntities.cs` |
| The generic connector: operations, templates, answer paths, HMAC signatures, the HTTP client through the outgoing-request policy | `src/api/Zelvo.Api/Payments/GatewayConnector.cs` |
| Fees, holds, tries, confirmation, receipts, refunds, expiry | `src/api/Zelvo.Api/Payments/PaymentService.cs` |
| API | `src/api/Zelvo.Api/Payments/PaymentEndpoints.cs` |
| Reconciliation and the payments job | `src/api/Zelvo.Api/Payments/PaymentJobs.cs` |
| The card's fee: timing, gateway, amounts added by answers | `src/api/Zelvo.Api/Catalog/ServiceCard.cs` (`ServiceFee`, `FeeRule`) |
| Notifications `payment.requested`, `payment.received` (the e-receipt), `payment.refunded`, `payment.expired` | `src/api/Zelvo.Notifications/NotificationEvents.cs`, `Texts/*.json` |
| Demo seeder `e8-payments`; development `payment-mock` in Compose | `src/api/Zelvo.Api/Demo/DemoPaymentsSeeder.cs`, `deploy/compose/payment-mock` |
| Settings → Payments, the request's fee panel, the return page, the receipt, finance's Payments page, the fee in the service editor, the gateway's callback route | `src/web/src/components/payments`, `src/web/src/app/[locale]/{settings/payments,payments}`, `src/web/src/app/payments/callback` |

## Settings → Payments

- **Switch** (`payments.enabled`, off by default). Off: fees are shown for information only and no payment step appears. It can be turned on only once a gateway is connected.
- **Unpaid requests wait** `payments.unpaidExpiryHours` (default 72, 1–2160).
- **Gateways** (`payments.manage`, tenant administrators; never for integrations, for the product owner's approval, as gateways hold credentials and an integration could point one at its own address):
  - name, merchant id, enabled, default;
  - the **connector** (below);
  - **credentials** as named values, encrypted with `Zelvo.Secrets` (purpose `payments.gateway.credentials`), never returned (only their names), audited without values; values left empty are kept; a connector pointing at another host needs them again;
  - **Test connection**: calls the status API with a test reference; any HTTP answer shows the address is right and reachable;
  - **test payment** in sandbox mode: 1.00 AED through the gateway's page; once confirmed, the gateway may go **live**. Changing the connector goes back to test mode and needs a new test payment.

### The generic connector

No gateway-specific templates (module 15): the administrator maps the gateway's API from its documentation.

| Part | What |
| --- | --- |
| `create` | Starts a payment. Answers `transactionId` and `redirectUrl` (the gateway's page). |
| `status` | The status API. Answers `status` and, when given, `amount` and `reference`. |
| `refund` | Optional refund API. Answers `refundId` and `status`; `refundSucceeded` lists statuses that mean done. |
| `statusValues` | Which statuses mean paid, failed or cancelled; anything else is pending. `statusAmountInMinorUnits` for amounts in fils. |
| `callback` | The gateway's direct notification: where the reference is and its HMAC signature (algorithm, credential, header, encoding, prefix). |
| `settlement` | Columns of the settlement file (reference, transaction, amount, status). |

Each operation has a method, an HTTPS address, a body format (JSON with dot paths for nesting, form, none), headers, body fields and answer paths (`data.id`, `items.0.status`), and an optional HMAC signature over the body in a header. Templates use `{{amount}}`, `{{amountMinor}}`, `{{currency}}`, `{{reference}}`, `{{transactionId}}`, `{{description}}`, `{{returnUrl}}`, `{{callbackUrl}}`, `{{merchantId}}`, `{{language}}`, `{{refundAmount}}`, `{{refundAmountMinor}}`, `{{refundReference}}` and `{{secret.NAME}}`; values are URL-encoded in addresses; a body field that is exactly an amount placeholder is sent as a JSON number. Unknown placeholders and credentials, missing answer fields and addresses the policy refuses are listed as problems.

Every call goes through the outgoing-request policy with its own operator settings (`Payments:AllowInsecureHttp`, `Payments:AllowedPrivateNetworks`): HTTPS only, no internal addresses unless allowed, checked after DNS; no redirects followed; answers over 1 MB refused; bodies never logged. Network access between the gateway and the customer's server is the customer's responsibility (module 15).

## Per-service settings

On the service card's fee (versioned, Fee section of the service editor), for paid services:

| Field | What |
| --- | --- |
| `amountAed` | The fixed amount |
| `rules` | `{ name, conditions, amountAed }`: amounts added when the answers match (C3 conditions), for example urgent delivery; each is a line of the receipt |
| `timing` | `OnSubmission` (default), `AfterApproval`, `OnCompletion` |
| `gatewayId` | A gateway; none: the default gateway |

A bundle has no fee; its services each have theirs (C5).

## The pay flow

1. **Due.** With payments on and a gateway enabled:
   - *on submission* (and *after approval* for a service without approvals before assignment): the request is made **held** (`tickets.held`, `hold = Payment`): not routed, clocks paused, no approvals yet;
   - *after approval*: once approved, the request is held instead of released;
   - *on completion*: when the request is resolved (through the API at once, otherwise within a minute by the payments job), the payment is made; the request is not held. It cannot be closed by hand (409 `payment-outstanding`) and the worker's auto-close skips it until paid.
   The amount is the fee plus the matching rules; `payment.requested` tells the requester.
2. **Pay now** (`POST /payments/{id}/pay`, the requester or who made the request): a new try (`payment_attempts`) with a random reference (`ZP` + 24 letters and digits, unique in the installation) is sent to the gateway with the amount; the browser goes to the gateway's page. Card details are entered only there.
3. **Back and notified.** The gateway sends the payer to `/{lang}/payments/return/{reference}` and notifies `/payments/callback/{reference}` (the web app forwards the body and signature headers to the API; it always answers 200).
4. **Confirmed with the status API.** Whatever the return or the notification says, Zelvo asks the status API, with the payment's row locked: the status must be a paid one, and the amount and reference, where given, must be Zelvo's (otherwise it stays awaiting and the mismatch is audited). A notification whose signature does not verify is ignored (audited). A try already paid, or a payment already paid by another try, is never counted again (`payments.paid-again` is audited for refund).
5. **Paid.** The payment gets a receipt number (`RCT-000123`, a tenant series under a lock, never reused), `payment.received` sends the e-receipt (its email cannot be turned off), and the request moves on: its approvals, or the service desk (after approval: the service desk).
6. **Failed or cancelled**: the request stays awaiting payment and the requester can pay again. Tries whose payer never came back are asked of the gateway by the job from two minutes on, for two days.
7. **Expiry** (on submission and after approval): the job closes the request with "Closed: the fee was not paid in time." in the requester's language, the payment becomes Expired, `payment.expired` tells the requester; a bundle moves on (C5).

Staff can close a request that waits for its fee; the payment is cancelled.

## Receipts and refunds

- `GET /payments/{id}/receipt`: the bilingual e-receipt (organisation, receipt number, request, service, payer, lines, total, refunded, transaction id, paid on, gateway) for the requester, staff and finance; the portal page shows both languages side by side and prints.
- `POST /payments/{id}/refunds` (`payments.refund`): `{ amountAed?, reason }` (all that is left by default). Through the refund API when the connector has one (Completed or Failed; nothing is refunded on failure), otherwise **Manual** (recorded as refunded outside Zelvo). The payment becomes PartlyRefunded or Refunded; `payment.refunded` tells the requester.

## Finance

`payments.read` (tenant administrators and executives): the **Payments** page (`/payments`) with the period's payments, export to Excel or CSV (`GET /payments/export`, audited) and the daily reconciliation.

**Reconciliation** (`payment_reconciliations`, one per gateway and day): every try of the day with a transaction is asked of the status API and compared with Zelvo (Matched, Mismatched, NotFoundAtGateway); the job reconciles yesterday once per gateway; finance can run a day again. Importing the gateway's **settlement file** (CSV or Excel, at most 10 MB) adds the lines Zelvo does not know (UnknownToZelvo) and marks known ones whose amount differs. A reconciliation's lines export to Excel or CSV.

## API

| Endpoint | Permission |
| --- | --- |
| `GET/PUT /settings/payments` | signed in (whether on) / `payments.manage` |
| `GET/POST /payments/gateways`, `PUT/DELETE /payments/gateways/{id}`, `POST …/test-connection`, `POST …/test-payment` | `payments.manage` |
| `GET /service-requests/{id}/payments` | who may see the request |
| `GET /payments/{id}`, `GET /payments/{id}/receipt` | the requester, staff, `payments.read` |
| `POST /payments/{id}/pay` | the requester or who made the request |
| `POST /payments/attempts/{reference}/confirm` | who started the try, the requester, `payments.read` |
| `POST /payments/callback/{reference}` | anonymous; signature and status API |
| `POST /payments/{id}/refunds` | `payments.refund` |
| `GET /payments`, `GET /payments/export`, `GET/POST /payments/reconciliations`, `POST …/{id}/settlement-file`, `GET …/{id}/export` | `payments.read` |

Every payment event is in the audit log (`payments.*`).

## The payments job

`PaymentJobs` runs in the API every minute (`Payments:Jobs:Interval`; `Payments:Jobs:Enabled=false` turns it off, as the tests do), for tenants with gateways (`zelvo_payment_job_tenants()`), under an advisory lock per tenant: tries to ask again, expired requests, fees due on completion, yesterday's reconciliation.

## Development and demo

- `payment-mock` in Compose (`http://payment-mock:8098`, payer page on `http://localhost:8098`): creates payments, a page with Pay and Decline, notifies Zelvo through the web app, answers its status API, refunds and gives a settlement CSV. No real money or card data. Compose allows plain HTTP and private networks for payments in development only.
- Seeder `e8-payments` (order 240): a "Demo pay (sandbox)" gateway for the mock in test mode and payments on, so the sample paid services ask for their fee.

## Tests

- **Integration against PostgreSQL** (`PaymentApiTests`, with a gateway in memory): credentials never returned or stored or audited in clear; a broken connector's problems; a new host needs credentials again; test connection; live mode only after a sandbox test payment; a fee on submission with an amount added by an answer; holding and releasing the request; paying only by the requester; a wrong amount not accepted; a forged notification ignored and a signed one confirmed once (one receipt, one notification); refunds through the gateway and limits; finance's list, Excel export, reconciliation and settlement file; expiry closing a request; a fee on completion blocking the close until paid and confirmed by the job; payments off; a fee after approval; templates, answer paths and signatures.
- **Web** (`src/web/src/components/payments/payments.test.tsx`): pay now to the gateway's page, the receipt link and refund in Arabic, the fee editor, the gateway editor with write-only credentials and the API's problems.

## Not in E8

- Gateway-specific templates (decided: none).
- A PDF receipt (the receipt page prints; F1's PDF export can add it).
- Payments of anonymous requests (service requests need sign-in).
