# Security

What is implemented, what is simulated, and what a production deployment would
need to change.

> **Threat-model scope.** This is a proof of concept. It handles no real customer
> data, stores no card data, and is not internet-facing. The controls below are
> real and testable; the identity provider is simulated. Section
> [Production gaps](#production-gaps) lists every item that would block a
> production deployment.

---

## Summary

| Control | Status | Where |
|---|---|---|
| Bearer token authentication | ✅ real JWT validation, **simulated issuer** | `SecurityExtensions.cs` |
| Authorization policies | ✅ 6 scoped policies | `SecurityOptions.cs` |
| CORS allowlist | ✅ explicit origins, no wildcard | `SecurityExtensions.cs` |
| Rate limiting | ✅ fixed window, partitioned | `SecurityExtensions.cs` |
| Security response headers | ✅ API and nginx | `SecurityHeadersMiddleware.cs`, `nginx-security-headers.conf` |
| Input validation | ✅ three layers | `SubmitQuoteCommandValidator.cs`, domain constructors |
| SQL injection | ✅ not reachable | EF Core parameterisation throughout |
| Secrets management | ✅ environment only, nothing committed | `.env.example`, `.dockerignore` |
| Error handling | ✅ no stack traces to clients | `GlobalExceptionHandler.cs` |
| Safe logging | ✅ no tokens, keys or bodies | throughout |
| Container hardening | ✅ non-root, multi-stage, no SDK in runtime | Dockerfiles |
| Idempotent money movement | ✅ DB-enforced | `UX_Payments_PolicyId_IdempotencyKey` |
| HTTPS / HSTS | ⚠️ configured, off in local Development | `Program.cs` |
| Real identity provider | ❌ simulated | see below |
| Audit trail | ⚠️ structured logs only | — |

---

## Authentication

### What is real

JWT bearer validation is genuine and strict:

```csharp
ValidateIssuer          = true
ValidateAudience        = true
ValidateLifetime        = true      // non-negotiable
ValidateIssuerSigningKey = true
ClockSkew               = 2 minutes // tighter than the 5-minute default
ValidAlgorithms         = [HmacSha256]  // or [RsaSha256] with an authority
```

**Algorithm pinning matters.** Without `ValidAlgorithms`, a token presenting
`"alg": "none"` or a confused-algorithm variant can bypass signature verification
entirely. This is one of the most commonly missed JWT settings.

**Lifetime validation is never disabled.** Turning it off — occasionally done to
"fix" a clock-skew problem — makes every token ever issued valid forever.

`OnChallenge` strips the default `WWW-Authenticate` detail, which otherwise
describes exactly *why* a token was rejected and helps an attacker iterate.

### What is simulated

`POST /api/auth/dev-token` mints tokens **with no credential check**. It exists
so the authorization architecture — tokens, scopes, roles, policies, 401 vs 403 —
is demonstrable and testable without provisioning an Entra ID tenant.

Three independent controls keep it out of a deployed environment:

1. The route is only mapped when `ENABLE_DEV_TOKEN_ENDPOINT=true`.
2. `SecurityExtensions` **throws at startup** if that flag is true while the
   environment is not Development. A misconfigured deployment fails to boot
   rather than serving an open token endpoint.
3. The controller re-checks all three conditions and returns **404** (not 403 —
   do not confirm the endpoint exists) if any fails.

A second startup guard rejects a local symmetric signing key outside Development:

> A local symmetric signing key is configured outside Development. Symmetric keys
> mean the API can mint the tokens it accepts, so a leaked key forges any
> identity. Configure `Security:Authority` (Entra ID) and remove
> `JWT_SIGNING_KEY`.

A third rejects an HS256 key under 32 bytes — shorter than the hash output is
brute-forceable.

### Migrating to real Entra ID

A configuration change, not a code change:

```bash
JWT_AUTHORITY=https://login.microsoftonline.com/<tenant-id>/v2.0
JWT_AUDIENCE=api://<app-id>
JWT_SIGNING_KEY=           # delete
ENABLE_DEV_TOKEN_ENDPOINT=false
REQUIRE_AUTHENTICATION=true
```

Validation switches to RS256 against the authority's published JWKS, so keys
rotate without a redeploy. Then delete `AuthController.cs`. Every
`[Authorize(Policy = ...)]` attribute and every policy stays exactly as it is —
they are already the ones Entra ID would drive.

---

## Authorization

Six policies, named by constant so a typo is a compile error rather than a silent
runtime fail-closed:

| Policy | Roles | Guards |
|---|---|---|
| `quotes:write` | Customer, Agent | `POST /api/quotes` |
| `quotes:read` | Customer, Agent, Underwriter | `GET /api/quotes/{id}`, `/offers` |
| `policies:write` | **Agent only** | `POST /api/policies/from-quote/{id}` |
| `policies:read` | Agent, Underwriter | `GET /api/policies/{id}` |
| `payments:write` | **Agent only** | `POST /api/policies/{id}/payments` |
| `legacy:integrate` | LegacySystem | `/legacy/*` |

Design decisions worth defending:

**Binding is agent-only.** A self-service customer token must not be able to
create a financial obligation.

**Payments are a separate policy from binding.** Money movement deserves its own
authorization decision and its own audit trail, so the ability to charge can be
revoked independently of the ability to read or bind a policy.

**The legacy channel has its own role.** It authenticates as a service principal,
not a user. That keeps retirement of the legacy channel a one-line revocation.

**Scopes are checked, not just roles.** `HasScope` reads the OAuth 2.0 `scope`
claim (a single space-delimited string, not one claim per scope), so a delegated
token can be narrower than the user's full role. Least privilege per role is in
`AuthController.ScopesForRole`.

### The demo default, stated plainly

`REQUIRE_AUTHENTICATION=false` by default, which makes **every policy
permissive**. This is so the demo and the integration tests can walk the full
flow without a token step.

The policies still exist with their real names, still attach to the same
`[Authorize]` attributes, and are still enforced when a token *is* present. The
startup log says so unambiguously:

> `REQUIRE_AUTHENTICATION is false: endpoint authorization is not enforced. This
> is intended for the local demo only.`

To enforce:

```bash
REQUIRE_AUTHENTICATION=true
```

Then obtain a token:

```bash
TOKEN=$(curl -s -X POST http://localhost:8081/api/auth/dev-token \
  -H "Content-Type: application/json" -d '{"role":"Agent"}' \
  | python -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")

curl -H "Authorization: Bearer $TOKEN" http://localhost:8081/api/quotes/{id}
```

`GET /api/auth/whoami` reports the claims on a presented token, which is the
fastest way to diagnose an unexpected 403.

---

## Input validation

Three layers, each with a different job.

**1. Data annotations** on the API contract (`SubmitQuoteRequest`) — fast
per-field 400s with a `ValidationProblemDetails` body.

**2. FluentValidation** in the application layer — fuller rules, including a
state allowlist and character-class regexes:

```csharp
.Matches(@"^[\p{L}\p{M}\s'\-\.]+$")
```

Letters, marks, spaces, hyphens, apostrophes and periods. That covers real names
(O'Brien, Smith-Jones, Jr.) while excluding markup and control characters. It
also admits `FAIL-DEPENDENCY` and `FAIL-CARRIER`, because uppercase and hyphens
are legitimate in names too.

**3. Domain constructors** (`Quote.Submit`) — the authoritative layer. These hold
no matter which entry point the data arrived through, including the legacy adapter
and the worker. `QuoteStateTransitionTests` covers every rejection.

Validation is also a capacity control: bounded lengths keep oversized payloads
out of the persistence layer.

### SQL injection is not reachable

Every query is LINQ, which EF Core parameterises. There is **no
string-concatenated SQL anywhere in the solution.** The one raw statement — the
number allocator's `UPDLOCK` read — uses `FromSqlInterpolated`, where interpolated
values become SQL parameters rather than concatenated text:

```csharp
db.NumberSequences.FromSqlInterpolated($@"
    SELECT Id, Name, Year, LastValue
    FROM NumberSequences WITH (UPDLOCK, HOLDLOCK)
    WHERE Name = {sequenceName} AND Year = {year}")
```

The single place an identifier *is* interpolated is the integration-test database
creator, where the name is GUID-derived and guarded by an explicit
`AssertSafeIdentifier` check that throws on anything outside `[A-Za-z0-9_]`.

---

## The mobile lookup trade-off

`GET /api/quotes/lookup?quoteNumber=&lastName=` is `[AllowAnonymous]`, and that
is a deliberate, bounded decision worth stating rather than hiding.

**Why anonymous.** A customer standing outside a dealership with a text message
containing a quote number has no session. Requiring registration to see a quote
they just requested is the kind of friction that loses the sale.

**Why it is not wide open.**

- **Two factors required.** Quote numbers are sequential and therefore guessable,
  so the surname acts as a shared secret.
- **Identical 404s.** A wrong surname and an unknown quote number return byte-
  identical responses, so the endpoint cannot enumerate valid quote numbers.
  `Lookup_ReturnsTheSame404ForAWrongSurname` asserts the bodies match.
- **Rate limited** like every other endpoint.
- **Read-only.** No `onBind` is passed to the comparison component on this path;
  binding a policy is an authenticated agent action.

**What production would need.** A one-time link sent to the customer's verified
phone or email, or full customer authentication. The surname is a weak second
factor — it is often public — and it is adequate only because this system holds
no real customer data. This is recorded as a production gap below rather than
presented as sufficient.

---

## Rate limiting

Fixed window, 120 requests/minute, queue 10, returning 429 with `Retry-After`.

Partitioned by **authenticated identity when present, remote IP otherwise.**
Partitioning by IP alone would let one user behind a shared NAT exhaust the limit
for everyone on it.

For the partition to be meaningful, `UseForwardedHeaders` must run first —
otherwise every request appears to come from the nginx container. The API is
configured with `KnownIPNetworks.Clear()` because the Compose bridge address is
not known ahead of time; **a real deployment must list its actual proxies**, or a
client can spoof `X-Forwarded-For` and evade the limiter. Noted as a gap.

---

## Response headers

Set in **both** the API middleware and nginx, because the nginx-served responses
(the HTML shell, the JS bundle) never reach ASP.NET Core middleware.

| Header | Value | Closes |
|---|---|---|
| `X-Content-Type-Options` | `nosniff` | MIME sniffing turning JSON into executed HTML |
| `X-Frame-Options` | `DENY` | Clickjacking (older browsers) |
| `Referrer-Policy` | `no-referrer` | Quote ids leaking to third parties in `Referer` |
| `Permissions-Policy` | all denied | Unnecessary device API access |
| `Content-Security-Policy` | see below | Injected script execution |
| `Cache-Control` | `no-store` on API | Per-customer data cached by a shared proxy |

The API's CSP is maximally restrictive, because nothing should ever be loaded
from a JSON response:

```
default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'
```

The SPA's CSP permits `'unsafe-inline'` for **styles only** — Vite emits a small
inline style block and React sets an inline style for the per-carrier brand
colour. `script-src` is `'self'` with no exemption, which is the directive that
actually blocks injected JavaScript.

### An nginx gotcha worth knowing

If a `location` block defines **any** `add_header`, it **discards every**
`add_header` inherited from the enclosing `server`. Setting the headers once at
server level and then adding `Cache-Control` inside `location /` silently drops
all of them from exactly the responses that matter most.

This was a live bug in this repository, caught by `curl -D -`. The fix is
`nginx-security-headers.conf`, included in every block that adds headers of its
own.

---

## Error handling

`GlobalExceptionHandler` maps every exception to RFC 9457 `ProblemDetails`. Its
security job outranks its usability job: **never leak internals to a caller.**

Stack traces, SQL text, connection strings and type names all reveal the shape of
the system. The full exception goes to the structured log; the client gets a
generic message and the correlation id to quote to support.

Even in Development the unrecognised-exception path returns the exception
*message* only, never the stack trace.

| Status | Cause |
|---|---|
| 400 | Malformed request; missing `Idempotency-Key` |
| 404 | Quote or policy id does not exist |
| 409 | Concurrency conflict; duplicate business operation |
| 422 | Well-formed but violates a business rule |
| 429 | Rate limited |
| 503 | External dependency unavailable |
| 500 | Anything unrecognised — details in logs only |

`ErrorResponses_DoNotLeakStackTraces` asserts no response body contains
`at PolicyFlow.`, `StackTrace` or `Microsoft.EntityFrameworkCore`.

Provider-specific errors are translated in `UnitOfWork`, so a SQL Server error
number never reaches the application layer and a duplicate payment never surfaces
as an opaque 500.

---

## Secrets

**Nothing is committed.** Every credential is an environment variable.

- `.env.example` holds `<placeholder>` values only
- `.env` is in `.gitignore` **and** `.dockerignore`
- `appsettings.json` has no connection string and no key
- `docker-compose.yml` uses `${VAR:?error}` so a missing secret fails fast with
  a named variable rather than starting with a silent default
- `EnableSensitiveDataLogging(false)` and `EnableDetailedErrors(false)` on the
  `DbContext` — detailed errors can include parameter values, which may contain
  customer data
- The integration-test factory reads `MSSQL_SA_PASSWORD` from the environment and
  **throws if absent** rather than defaulting to a literal, because a hard-coded
  test credential is how one ends up committed

### Never logged

Bearer tokens, the `Idempotency-Key` (client-chosen, could contain anything),
request bodies, connection strings, or the rejected value of a malformed
`X-Correlation-Id` — logging that would *be* the injection the check prevents.

`PaymentResponse_DoesNotLeakTheIdempotencyKey` asserts the key never appears in a
response body.

---

## PCI DSS

**This system has no PCI DSS scope**, by design rather than by exemption.

`SimulatedPaymentGateway` returns a synthetic reference and nothing else. There
is no card number, no token, no merchant credential and no network call anywhere
in the payment path. `Payment` stores an amount, a status, an idempotency key and
a synthetic transaction id.

A real integration would tokenise on the client and never let raw card data reach
this service at all, which keeps the scope where it is.

---

## Container hardening

- **Multi-stage builds.** The SDK image compiles; only published output reaches
  the runtime image. Shipping the SDK would mean shipping a compiler, NuGet
  credentials and the full source tree.
- **Non-root.** `USER app` (uid 1654). A compromised root process is a host
  escalation attempt; an unprivileged one is not.
- **Port 8080, not 80.** Ports below 1024 require elevated privileges.
- **`DOTNET_EnableDiagnostics=0`.** Closes the diagnostic IPC socket.
- **The worker uses `runtime`, not `aspnet`.** It has no HTTP surface, so
  shipping ASP.NET Core would be dead weight and extra attack surface. This drove
  swapping `Serilog.AspNetCore` for `Serilog.Extensions.Hosting`.
- **`server_tokens off`** and `X-Powered-By`/`Server` removed. Telling an
  attacker the exact runtime build is free reconnaissance.
- **`.dockerignore` excludes `.env`, keys and certificates** so nothing that
  could carry a credential enters the build context.
- **Service Bus config mounted read-only.** The container has no business
  rewriting its own configuration.

---

## Production gaps

Everything that would block a production deployment, stated plainly.

| Gap | Impact | Fix |
|---|---|---|
| **Simulated identity provider** | The dev token endpoint issues credentials without verification | Configure `JWT_AUTHORITY` for Entra ID, delete `AuthController` |
| **`REQUIRE_AUTHENTICATION=false`** | Endpoints are unauthenticated by default | Set `true`; policies already exist |
| **Anonymous mobile lookup** | Surname is a weak second factor and is often public | One-time link to a verified channel, or customer auth |
| **HTTPS disabled in Development** | Plaintext inside the Docker network | Already enabled outside Development; terminate TLS at the gateway |
| **`KnownIPNetworks.Clear()`** | `X-Forwarded-For` can be spoofed to evade rate limiting | List the actual proxy addresses |
| **Migrations applied at startup** | Restricted to Development, but the pattern is present | Controlled pipeline step with a rollback plan |
| **No audit trail** | Structured logs are not a tamper-evident audit record | Append-only audit store for binding and payment events |
| **No secret rotation** | Keys are static environment values | Azure Key Vault with managed identity |
| **No WAF or DDoS protection** | Application-level rate limiting only | Azure Front Door / App Gateway WAF |
| **No PII encryption at rest** | Customer names in plaintext columns | SQL Server Always Encrypted or TDE plus column encryption |
| **No dependency scanning in CI** | Vulnerable transitive packages could ship | `dotnet list package --vulnerable`, `npm audit`, Dependabot |
| **Mongo transactions disabled** | Multi-document writes are not atomic | Single-node replica set, if the store ever holds invariants |
| **Single Mongo/SQL credential** | The app uses `sa` locally | Least-privilege application logins |

The local `sa` account deserves emphasis: it exists because it is the path of
least resistance for a container demo. A production deployment needs an
application login with `db_datareader`/`db_datawriter` plus `EXECUTE`, and
nothing else.

---

## Verifying the controls

```bash
# Security headers on the SPA and the API
curl -sD - -o /dev/null http://localhost:8080/ | grep -iE "content-security|x-frame|x-content"
curl -sD - -o /dev/null http://localhost:8081/api/carriers | grep -iE "content-security|cache-control"

# No stack trace on an error
curl -s http://localhost:8081/api/quotes/00000000-0000-0000-0000-000000000000

# Payment without an idempotency key is refused
curl -s -o /dev/null -w "%{http_code}\n" -X POST \
  http://localhost:8081/api/policies/$POLICY_ID/payments \
  -H "Content-Type: application/json" -d '{"amount":50}'    # → 400

# Validation rejects bad input with per-field errors
curl -s -X POST http://localhost:8081/api/quotes \
  -H "Content-Type: application/json" \
  -d '{"customerName":"<script>","state":"ZZ","vehicleYear":1850,"vehicleMake":"","vehicleModel":"","annualMileage":-1,"priorInsuranceMonths":-1}'

# A malformed correlation id is replaced, not echoed
curl -sD - -o /dev/null -X POST http://localhost:8081/api/quotes \
  -H "Content-Type: application/json" \
  -H "X-Correlation-Id: $(python -c 'print("a"*500)')" \
  -d '{"customerName":"Test User","state":"GA","vehicleYear":2023,"vehicleMake":"Toyota","vehicleModel":"Camry","annualMileage":12000,"priorInsuranceMonths":36}' \
  | grep -i x-correlation-id

# Containers run as non-root
docker exec policyflow-api id
```

The security-relevant assertions also run in CI as part of
`PolicyFlow.Api.IntegrationTests`.
