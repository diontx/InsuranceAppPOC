# API Reference

Base URL (local Docker): `http://localhost:8081`
Through the SPA's nginx proxy: `http://localhost:8080/api`
OpenAPI (Development only): `http://localhost:8081/swagger`

> All premiums, carriers and rating factors are **fictional**.

---

## Conventions

### Headers

| Header | Direction | Notes |
|---|---|---|
| `X-Correlation-Id` | request, optional | Generated when omitted. **Echoed in every response.** Must be ≤64 chars of `[A-Za-z0-9._:-]`; anything else is replaced with a generated GUID. Flows into SQL, the queue and worker logs. |
| `Idempotency-Key` | request, **required on payments** | One value per payment *intent*, reused on every retry of that intent. |
| `Authorization: Bearer <jwt>` | request | Required when `REQUIRE_AUTHENTICATION=true`. |

### Errors

Every failure returns RFC 9457 `ProblemDetails`:

```json
{
  "type": "https://policyflow.example/problems/business-rule",
  "title": "Business rule violation",
  "status": 422,
  "detail": "Quote Q-2026-000001 is Pending; only a Rated quote can be bound to a policy.",
  "instance": "/api/policies/from-quote/...",
  "correlationId": "9f1b7c42-0e52-4a6b-8f3d-1c0a4e6b2d91",
  "traceId": "0HNOGT316UNCC"
}
```

Validation failures add `errors` with per-field messages. Stack traces are never
returned — see `docs/SECURITY.md`.

| Status | Meaning |
|---|---|
| 400 | Malformed request; missing `Idempotency-Key` |
| 404 | Resource does not exist |
| 409 | Concurrency conflict; duplicate business operation |
| 422 | Well-formed but violates a business rule |
| 429 | Rate limited (`Retry-After: 60`) |
| 503 | External dependency unavailable |
| 500 | Unexpected — details in logs, quote the `correlationId` |

---

## Quotes

### `POST /api/quotes`

Submits a quote for asynchronous underwriting.

**Policy** `quotes:write` · **Returns** `202 Accepted`

Returns 202, not 201: the quote exists but its price does not. Underwriting calls
a verification bureau and five carrier gateways, so the API does not wait. Poll
`statusUrl`.

```http
POST /api/quotes
Content-Type: application/json
X-Correlation-Id: my-trace-id
```

```json
{
  "customerName": "Alex Johnson",
  "state": "GA",
  "vehicleYear": 2024,
  "vehicleMake": "Toyota",
  "vehicleModel": "Camry",
  "annualMileage": 12000,
  "priorInsuranceMonths": 36
}
```

```http
HTTP/1.1 202 Accepted
Location: /api/quotes/42174544-85c3-4c29-9211-0dbf8836ec6d
X-Correlation-Id: my-trace-id
```

```json
{
  "quoteId": "42174544-85c3-4c29-9211-0dbf8836ec6d",
  "quoteNumber": "Q-2026-000002",
  "status": "Pending",
  "statusUrl": "/api/quotes/42174544-85c3-4c29-9211-0dbf8836ec6d",
  "correlationId": "my-trace-id"
}
```

**Fields**

| Field | Type | Constraint |
|---|---|---|
| `customerName` | string | 2–120 chars; letters, spaces, `-`, `'`, `.` |
| `state` | string | One of `GA FL TX NC SC TN AL VA MS` |
| `vehicleYear` | int | 1981 … current year + 1 |
| `vehicleMake` | string | 1–50 chars |
| `vehicleModel` | string | 1–50 chars |
| `annualMileage` | int | 0 … 100,000 |
| `priorInsuranceMonths` | int | 0 … 600 (0 forces a referral) |

---

### `GET /api/quotes/{quoteId}`

Reads underwriting status. **Safe to poll** — a single indexed primary-key read.

**Policy** `quotes:read`

```json
{
  "quoteId": "42174544-85c3-4c29-9211-0dbf8836ec6d",
  "quoteNumber": "Q-2026-000002",
  "status": "Rated",
  "isTerminal": true,
  "customerName": "Alex Johnson",
  "state": "GA",
  "vehicleDescription": "2024 Toyota Camry",
  "annualMileage": 12000,
  "priorInsuranceMonths": 36,
  "annualPremium": 595.60,
  "bestCarrierId": "ironclad-direct",
  "bestCoverageTier": "Basic",
  "offerCount": 15,
  "channel": "Modern",
  "correlationId": "my-trace-id",
  "createdUtc": "2026-09-12T16:26:23Z",
  "updatedUtc": "2026-09-12T16:26:28Z",
  "offersUrl": "/api/quotes/42174544-85c3-4c29-9211-0dbf8836ec6d/offers"
}
```

**Stop polling when `isTerminal` is `true`.** Use the server's flag rather than
re-deriving which statuses are final on the client.

| `status` | Terminal | Meaning |
|---|---|---|
| `Pending` | no | Accepted; underwriting not started |
| `Processing` | no | Claimed by the worker |
| `Rated` | **yes** | Offers available |
| `Referral` | **yes** | Needs a human underwriter; `decisionReason` explains |
| `Declined` | **yes** | Ineligible; `decisionReason` explains |

`annualPremium`, `bestCarrierId` and `bestCoverageTier` are absent unless
`Rated`. `decisionReason` is present on `Referral` and `Declined`.

---

### `GET /api/quotes/{quoteId}/offers`

The five-carrier comparison. Served from the MongoDB read model as one document;
falls back to a SQL projection if the read model is unavailable.

**Policy** `quotes:read`

```json
{
  "quoteId": "44cb49b1-4db3-4b82-8a90-dfe79096c4d7",
  "quoteNumber": "Q-2026-000003",
  "customerName": "Dana Reyes",
  "state": "FL",
  "vehicleDescription": "2024 Honda Accord",
  "status": "Rated",
  "baselineAnnualPremium": 1375.00,
  "totalRatingMs": 1916,
  "offers": [
    {
      "offerId": "8f3a...",
      "carrierId": "ironclad-direct",
      "carrierName": "Ironclad Direct",
      "tagline": "No agents, no overhead, no surprises.",
      "brandColor": "#14477D",
      "financialStrengthRating": "A",
      "coverageTier": "Basic",
      "annualPremium": 955.31,
      "monthlyPremium": 82.61,
      "deductible": 1000.00,
      "limitsSummary": "25/50/25",
      "bodilyInjuryPerPerson": 25000,
      "bodilyInjuryPerAccident": 50000,
      "propertyDamage": 25000,
      "includesRoadside": false,
      "includesRentalReimbursement": false,
      "discountAmount": 301.68,
      "appliedDiscounts": [
        "Continuous coverage (18+ months)",
        "New vehicle safety (3 model years)",
        "Paperless"
      ],
      "ratingTrace": [
        "Ironclad Direct — Basic tier",
        "Base annual premium: $795.00.",
        "State factor FL x1.35 -> $1,073.25.",
        "New vehicle (2024) x1.22 -> $1,309.37.",
        "High mileage (16,000) x1.20 -> $1,571.24.",
        "Basic tier factor x0.80 -> $1,256.99.",
        "Discounts 24% (…) -> $955.31.",
        "Final annual premium: $955.31; monthly $82.61 (includes $3.00 instalment fee)."
      ],
      "isBestPrice": true
    }
  ],
  "declinations": [
    {
      "carrierId": "blueridge-assurance",
      "carrierName": "BlueRidge Assurance",
      "reason": "BlueRidge Assurance is not licensed in FL.",
      "wasUnavailable": false
    }
  ],
  "generatedUtc": "2026-09-12T16:30:45Z",
  "correlationId": "e2e-1789230640"
}
```

Offers are **cheapest first**, and exactly one carries `isBestPrice: true`.

`ratingTrace` is the auditable step-by-step calculation, shown in an expandable
panel in the UI.

`wasUnavailable` distinguishes a gateway *failure* from a carrier *declining* —
the UI shows "not available" versus the appetite reason.

A rated quote may carry fewer than 15 offers; see the appetite table in
`docs/ARCHITECTURE.md`.

---

### `GET /api/quotes/lookup`

Mobile self-service lookup. Returns status **and** the full comparison in one
round trip, because a second request on a slow connection is a visible delay.

**Policy** `AllowAnonymous` — see the trade-off analysis in `docs/SECURITY.md`

```http
GET /api/quotes/lookup?quoteNumber=Q-2026-000002&lastName=Johnson
```

```json
{
  "quote": { "...QuoteStatusView..." },
  "comparison": { "...QuoteComparison, or absent if not rated..." }
}
```

Requires **both** parameters. A wrong surname returns a **byte-identical 404** to
an unknown quote number, so the endpoint cannot enumerate valid quote numbers.

---

## Policies

### `POST /api/policies/from-quote/{quoteId}`

Binds a rated quote into a 12-month policy.

**Policy** `policies:write` (Agent only) · **Returns** `201 Created`

```json
{
  "offerId": "8f3a...",
  "effectiveDate": "2026-10-01"
}
```

Both fields optional: omit `offerId` to bind the **cheapest** offer, omit
`effectiveDate` for tomorrow.

```json
{
  "policyId": "831cf43f-89a0-479f-abdf-ad89b282fd84",
  "policyNumber": "POL-2026-000001",
  "quoteId": "44cb49b1-...",
  "quoteNumber": "Q-2026-000003",
  "status": "Active",
  "insuredName": "Dana Reyes",
  "state": "FL",
  "carrierId": "ironclad-direct",
  "carrierName": "Ironclad Direct",
  "coverageTier": "Basic",
  "annualPremium": 955.31,
  "monthlyPremium": 82.61,
  "deductible": 1000.00,
  "totalPaid": 0.00,
  "balanceDue": 955.31,
  "effectiveDate": "2026-09-13",
  "expirationDate": "2027-09-13",
  "payments": [],
  "correlationId": "e2e-1789230640",
  "createdUtc": "2026-09-12T16:30:59Z"
}
```

| Status | When |
|---|---|
| 404 | No such quote |
| **409** | **This quote is already bound to a policy** |
| 422 | Quote is not `Rated`, or the offer belongs to another quote |

The 409 is guaranteed by `UX_Policies_QuoteId` in SQL Server, so it holds under
genuine concurrency. `ConcurrentPolicyBinding_StillCreatesOnlyOnePolicy` fires
ten simultaneous requests and asserts exactly one `201` and one row.

The bound premium is the one the customer was **shown**, not the cheapest.

---

### `GET /api/policies/{policyId}`

Reads a policy with its payment history. **Policy** `policies:read`

---

### `POST /api/policies/{policyId}/payments`

Takes a premium payment. **Requires `Idempotency-Key`.**

**Policy** `payments:write` (Agent only) · **Returns** `200 OK`

```http
POST /api/policies/831cf43f-.../payments
Content-Type: application/json
Idempotency-Key: 3f2504e0-4f89-11d3-9a0c-0305e82c3301
```

```json
{ "amount": 125.00 }
```

```json
{
  "paymentId": "df184904-01ef-4e49-bac5-38f152e2920d",
  "amount": 125.00,
  "status": "Approved",
  "externalTransactionId": "TXN-SIM-06E70945",
  "createdUtc": "2026-09-12T16:30:59Z",
  "wasReplayed": false
}
```

Submitting the **same key again** returns the original transaction unchanged with
`wasReplayed: true`, and creates nothing:

```json
{
  "paymentId": "df184904-01ef-4e49-bac5-38f152e2920d",
  "amount": 125.00,
  "status": "Approved",
  "externalTransactionId": "TXN-SIM-06E70945",
  "wasReplayed": true
}
```

**200 on both, deliberately.** A retrying client must not be able to tell the
difference between the original and the replay.

**A changed amount on the same key is ignored.** The key identifies the *intent*,
so the original amount stands — charging an amount nobody confirmed would be
worse.

**Scope is per policy.** Keys are client-chosen, so two customers can pick the
same one; `(PolicyId, IdempotencyKey)` is the unique constraint.

| Status | When |
|---|---|
| **400** | **`Idempotency-Key` header missing** |
| 404 | No such policy |
| 422 | Policy is not `Active`, or amount ≤ 0 or > 100,000 |

### Client guidance

Generate **one key per payment intent** and reuse it on every retry of that
intent:

```typescript
// CORRECT — one key per intent, reused on retry
if (!keyRef.current) {
  keyRef.current = crypto.randomUUID();
}
await api.submitPayment(policyId, amount, keyRef.current);

// WRONG — a new key per attempt defeats the protection entirely.
// The server sees every retry as a new payment.
await api.submitPayment(policyId, amount, crypto.randomUUID());
```

The key is **not** cleared after a failure. A failed attempt may still have been
processed server-side — a lost response is indistinguishable from a lost request
— so the retry must carry the same key. See `usePaymentIntent.ts` and its tests.

---

## Carriers

### `GET /api/carriers`

Lists the active carrier panel from the NoSQL catalog. **`AllowAnonymous`** —
this is public brochure information.

Deliberately **omits** internal rating configuration: `baseAnnualPremium`,
`stateFactors`, the tier multipliers and the simulated latency bands are not
exposed. `GetCarriers_DoesNotLeakInternalRatingFactors` asserts this.

```json
[
  {
    "carrierId": "liberty-crest",
    "displayName": "Liberty Crest Insurance",
    "tagline": "Nationwide coverage with a clean-driver advantage.",
    "brandColor": "#1F3C88",
    "financialStrengthRating": "A+",
    "statesWritten": ["AL","FL","GA","NC","SC","TN","TX","VA"],
    "minimumPriorInsuranceMonths": 0,
    "maximumAnnualMileage": 30000,
    "coverageTiers": [
      {
        "tier": "Basic",
        "deductible": 1000.00,
        "limitsSummary": "25/50/25",
        "bodilyInjuryPerPerson": 25000,
        "bodilyInjuryPerAccident": 50000,
        "propertyDamage": 25000,
        "includesRoadside": false,
        "includesRentalReimbursement": false
      }
    ],
    "availableDiscounts": [
      "Continuous coverage (24+ months)",
      "Low mileage (under 8,000)",
      "Paperless"
    ]
  }
]
```

### `GET /api/carriers/{carrierId}`

One carrier. 404 if unknown or inactive.

---

## Underwriting

The underwriter work queue. Consumed by the Angular module that the legacy
portal hosts in `Underwriting.aspx` — but these are **modern** endpoints
(camelCase, ProblemDetails), not the legacy seam: a new screen is built against
the platform it will outlive the portal on.

**Policy** `referrals:review` on all three (the `Underwriter` role, or that
scope). Separate from `quotes:read` because the queue lists every referred
applicant, not one quote the caller already holds the id of.

A quote is in the queue while its status is `Referral` (the pipeline referred it)
or `FollowUp` (an underwriter asked for more information). Both are terminal for
the worker: nothing automated changes them.

### `GET /api/underwriting/queue`

Oldest submission first, capped at 200 (`isTruncated` says when there are more).

```json
{
  "items": [
    {
      "quoteId": "4a0f5c1e-...",
      "quoteNumber": "Q-2026-000006",
      "customerName": "Riley Morgan",
      "state": "GA",
      "vehicleDescription": "2023 Honda Civic",
      "status": "Referral",
      "reason": "No prior insurance on record. A licensed underwriter must review this application.",
      "channel": "Modern",
      "submittedUtc": "2026-09-13T16:45:02.117",
      "updatedUtc": "2026-09-13T16:45:03.912"
    }
  ],
  "referralCount": 4,
  "followUpCount": 1,
  "isTruncated": false,
  "generatedUtc": "2026-09-13T16:52:40.031"
}
```

Timestamps are UTC but carry no `Z` (SQL Server `datetime2` stores no offset).
Clients should read the `Utc` suffix as the contract; the Angular module does.

### `GET /api/underwriting/quotes/{quoteId}`

The application plus what the underwriter needs to decide. The eligibility trace
and carrier appetite are computed by the domain services, not by the client.

```json
{
  "quoteNumber": "Q-2026-000006",
  "status": "Referral",
  "isAwaitingReview": true,
  "customerName": "Riley Morgan",
  "state": "GA",
  "vehicleYear": 2023, "vehicleMake": "Honda", "vehicleModel": "Civic",
  "annualMileage": 12000,
  "priorInsuranceMonths": 0,
  "decisionReason": "No prior insurance on record. ...",
  "eligibilityOutcome": "Refer",
  "eligibilityTrace": ["REFER: no verifiable prior insurance; manual underwriting required."],
  "carrierAppetite": [
    { "carrierId": "liberty-crest", "carrierName": "Liberty Crest Insurance", "willWrite": true },
    { "carrierId": "summit-shield", "carrierName": "Summit Shield Mutual", "willWrite": false,
      "reason": "Summit Shield Mutual requires 6 months of prior coverage; quote has 0." }
  ],
  "offerCount": 0
}
```

After a decision it also carries `reviewedBy`, `reviewedUtc`, `reviewNote`, and,
once approved, `annualPremium`, `bestCarrierId` and `offerCount`.

### `POST /api/underwriting/quotes/{quoteId}/decision`

```json
{ "decision": "FollowUp", "note": "Upload the declarations page from your last policy." }
```

| `decision` | `note` | Effect |
|---|---|---|
| `Approve` | optional | Shops the carrier panel (the same fan-out the worker uses), status → `Rated`. Bindable like any rated quote. |
| `FollowUp` | required, 10–500 chars | Status → `FollowUp`; stays in the queue. The note becomes the customer-visible `decisionReason`. |
| `Decline` | required, 10–500 chars | Status → `Declined`; leaves the queue. |

Returns **200** with the updated review. Who decided is taken from the token's
`name` claim — there is no underwriter field in the body, so it cannot be spoofed.
With `REQUIRE_AUTHENTICATION=false` it is recorded as
`demo-underwriter (unauthenticated)`, so demo decisions are never mistaken for a
real person's.

| Status | When |
|---|---|
| 400 | Unknown `decision`, or a missing/short note for `FollowUp`/`Decline` (`errors.Note`) |
| 404 | No such quote |
| 409 | The quote is no longer awaiting review — typically another underwriter decided first. Concurrent decisions are resolved by the `rowversion` on `Quotes`: exactly one wins. |
| 422 | `Approve` on a risk no carrier will write. Follow up or decline instead. |

Approval calls the carrier gateways synchronously — unlike `POST /api/quotes`,
which never does. The caller is an internal user who needs to know immediately
whether any carrier will write the risk, the fan-out is concurrent (roughly the
slowest carrier), and no transaction is open while it runs.

---

## Legacy compatibility

The modernization seam. Same application logic as `/api/quotes`; different wire
contract. See `docs/LEGACY-MODERNIZATION.md`.

### `POST /legacy/quotes`

**Policy** `legacy:integrate` · **Always returns `200 OK`**

```json
{
  "InsuredName": "Sam Carter",
  "RiskState": "GA",
  "VehYear": "2023",
  "VehMake": "Toyota",
  "VehModel": "Camry",
  "Mileage": "12,000",
  "PriorCovMonths": "36"
}
```

PascalCase, every value a string — this is what the existing Web Forms
application already posts. `Mileage` may carry a thousands separator.

```json
{
  "returnCode": 0,
  "returnMessage": "QUOTE ACCEPTED FOR UNDERWRITING",
  "quoteNbr": "Q-2026-000004",
  "quoteGuid": "2916def3-c055-49e1-a1a5-76126871ca4b",
  "quoteStatus": "PENDING",
  "statusUrl": "/api/quotes/2916def3-...",
  "correlationId": "cfe15f6a-..."
}
```

**200 with a return code, never a 4xx.** The legacy client predates
`ProblemDetails` and cannot interpret an error body. A deliberate concession,
documented so nobody later "fixes" it.

| `returnCode` | Meaning |
|---|---|
| `0` | Accepted |
| `1` | Validation failure — `returnMessage` explains, uppercase |
| `2` | System error |

### `GET /legacy/quotes/{quoteNbr}`

Status **by quote number**, because the legacy database only ever stored the
printed number.

```json
{
  "returnCode": 0,
  "returnMessage": "OK",
  "quoteNbr": "Q-2026-000004",
  "quoteStatus": "RATED",
  "annualPrem": "488.19",
  "bestCarrier": "ironclad-direct",
  "offerCnt": 15,
  "correlationId": "cfe15f6a-..."
}
```

`annualPrem` is a fixed-decimal **string**, because the old VB.NET code does
`CDec()` on whatever it receives. `returnCode: 1` with `"QUOTE NOT FOUND"` for an
unknown number — still HTTP 200.

---

## Auth (Development only)

### `POST /api/auth/dev-token`

Mints a bearer token **with no credential check**. Available only when
`ENABLE_DEV_TOKEN_ENDPOINT=true` **and** the environment is Development;
otherwise **404**.

```json
{ "role": "Agent" }
```

Roles: `Customer`, `Agent` (default), `Underwriter`, `LegacySystem`.

```json
{
  "accessToken": "eyJhbGci...",
  "tokenType": "Bearer",
  "expiresInSeconds": 3600,
  "role": "Agent",
  "scopes": ["quotes:write","quotes:read","policies:write","policies:read","payments:write"],
  "warning": "Development token. Issued with no credential check. Never use outside local development."
}
```

### `GET /api/auth/whoami`

Reports the claims on the presented token. The fastest way to diagnose a 403.

---

## Health

### `GET /health/live`

Liveness. Checks **nothing** — a liveness probe that checks the database
restarts a healthy container during a database blip. `AllowAnonymous`.

### `GET /health/ready`

Readiness. Checks SQL Server, MongoDB and Service Bus. `AllowAnonymous`.

```json
{
  "status": "Healthy",
  "totalDurationMs": 6.4174,
  "checks": [
    { "name": "sql-server",  "status": "Healthy", "durationMs": 1.969,  "description": null },
    { "name": "mongodb",     "status": "Healthy", "durationMs": 1.541,  "description": "MongoDB is reachable." },
    { "name": "service-bus", "status": "Healthy", "durationMs": 5.981,  "description": "Service Bus queue 'quote-submitted' is reachable." }
  ]
}
```

MongoDB reports **Degraded** rather than Unhealthy when unreachable, because
there is a fallback. Service Bus reports **Unhealthy** — without it, quotes are
accepted but never underwritten.

---

## Demo triggers

Inputs that deterministically exercise specific architectural behaviour:

| Input | Behaviour |
|---|---|
| `priorInsuranceMonths: 0` | **Referral** — no carrier is shopped |
| `priorInsuranceMonths: 3` | **Partial panel** — only Patriot Vale and Liberty Crest quote |
| `state: "FL"` or `"TX"` | BlueRidge drops off (not licensed) |
| `vehicleYear` < current − 25 | **Declined** |
| `annualMileage: 95000` | Referral — no carrier has appetite |
| `customerName: "FAIL-DEPENDENCY"` | Verification fails → message abandoned and retried (`DeliveryCount` 1→5, then dead-lettered) |
| `customerName: "FAIL-CARRIER"` | Every carrier gateway fails → all marked `wasUnavailable` |
