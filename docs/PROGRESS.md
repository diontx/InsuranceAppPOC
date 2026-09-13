# PolicyFlow POC — Build Progress

Phase-by-phase record against `POC-INSTRUCTIONS.md` §32, plus the marketplace,
NoSQL, React and legacy-Web-Forms extensions requested for this build.

| Phase | Name | Status |
|-------|------|--------|
| 0 | Repository bootstrap | ✅ Complete |
| 1 | Domain + rating rules + 5-carrier marketplace | ✅ Complete |
| 2 | Application layer (use cases and ports) | ✅ Complete |
| 3 | SQL Server persistence (EF Core) | ✅ Complete |
| 4 | NoSQL carrier catalog + read models (EF Core / MongoDB) | ✅ Complete |
| 5 | Quote REST API | ✅ Complete |
| 6 | Service Bus + underwriting worker | ✅ Complete |
| 7 | Policy + idempotent payment | ✅ Complete |
| 8 | Security simulation | ✅ Complete |
| 9 | React SPA (responsive, red/white/blue) | ✅ Complete |
| 10 | Legacy Web Forms project + compatibility seam | ✅ Complete |
| 11 | Docker Compose end-to-end | ✅ Complete |
| 12 | CI pipeline + documentation | ✅ Complete |
| 13 | Underwriter work queue — Angular module in the legacy portal | ✅ Complete |
| 14 | Underwriter work queue — React page in the modern SPA (contrast) | ✅ Complete |
| 15 | Agent workspace — submit quotes and track your own | ✅ Complete |

**Final verification** (from empty volumes, `docker compose down -v` →
`up --build`):

```
dotnet build          0 warnings, 0 errors
dotnet test           291 passed (130 domain, 90 application, 71 integration)
npm run lint          clean
npm run test:ci       41 passed
docker compose up     6 containers, all healthy
end-to-end            Pending → Rated (4s), 15 offers, policy bound,
                      2 payment requests → 1 row, legacy seam returnCode=0
```

---

## Phase 0 — Repository Bootstrap

Status: **Complete**

### Completed
- `PolicyFlow.slnx` with `src/` and `tests/` solution folders.
- Nine .NET 10 projects (SDK 10.0.301) with dependencies pointing inward.
- React 19 + Vite 8 + TypeScript 6 SPA with `react-router-dom`, Vitest and
  Testing Library.
- `NuGet.config`, `Directory.Build.props`, `.editorconfig`, `.gitignore`,
  `.env.example`.

### Verification
- `dotnet build` → **PASS** (0 warnings, 0 errors)
- `npm install && npm run build` → **PASS**

### Important decisions
- **`NuGet.config` was required.** No package sources were registered on this
  machine, so every `dotnet add package` failed with "no versions available".
  Committing a repo-local source makes the build reproducible.
- **`PolicyFlow.Infrastructure.NoSql` is a separate project.** The two stores have
  different consistency models and different EF Core providers; keeping them apart
  prevents the SQL `DbContext` from acquiring Mongo types.
- **React instead of Angular.** `POC-INSTRUCTIONS.md` §20 specifies Angular; this
  build was explicitly requested in React. The architectural point the document
  cares about (SPA → REST → async → SQL → queue → worker) is unchanged.
- **`FluentAssertions` removed.** v8 ships under a commercial licence; xUnit's
  built-in assertions avoid the ambiguity.
- **`InvariantGlobalization` disabled.** `Microsoft.Data.SqlClient` refuses to run
  in invariant mode, and the rating traces format currency.

---

## Phase 1 — Domain and Rating Rules

Status: **Complete**

### Completed
- `Quote` aggregate owning its own state machine — callers cannot assign
  `Status`, only request a transition.
- `CarrierOffer`, `Policy`, `Payment`, `OutboxMessage`, `ProcessedMessage`.
- `QuoteRatingService` — the flat-surcharge baseline from §9, plus eligibility
  gates (zero prior coverage → Referral; over 25 model years → Declined).
- `CarrierRatingService` — multiplicative per-carrier factors per tier, with
  discounts capped at 30%.
- `CarrierPanel` — five fictional carriers with deliberately differing appetite.

### Verification
- `dotnet test tests/PolicyFlow.Domain.Tests` → **PASS, 130 tests, 130 ms**

Coverage includes every surcharge boundary (mileage exactly 15,000 must *not*
surcharge; prior coverage of exactly 6 months is clean), every legal and illegal
state transition, and determinism.

### Important decisions
- **Two disagreeing rating models on purpose.** The house baseline is a reference
  figure; the carrier panel is what the customer can buy. A comparison screen
  where everything agrees has nothing to compare.
- **A `Declined` path was added** beyond the instruction document, so the status
  is reachable and testable rather than dead code.
- **Rating is fully deterministic**, including simulated latency (derived from the
  quote id). Random jitter would make the demo irreproducible.

---

## Phase 2 — Application Layer

Status: **Complete**

### Completed
- Use cases: `SubmitQuote`, `GetQuote`, `GetQuoteOffers`, `UnderwriteQuote`,
  `CreatePolicyFromQuote`, `GetPolicy`, `SubmitPayment`, `LegacyQuoteAdapter`.
- Ports: repositories, `IUnitOfWork`, `IEventPublisher`, `ICarrierCatalog`,
  `ICarrierRatingGateway`, `IPriorInsuranceVerifier`, `IPaymentGateway`,
  `IQuoteComparisonStore`, `ICorrelationContext`.
- Simulated external dependencies, each documented as POC-only in its own file.

### Verification
- `dotnet build src/PolicyFlow.Application` → **PASS**

### Important decisions
- **The carrier fan-out is `Task.WhenAll`, not sequential.** Five carriers
  averaging 400 ms would take two seconds in series. Measured live:
  `FanOutMs=564` against a 1,969 ms serial total.
- **`Task.Delay`, never `Thread.Sleep`.** A blocking sleep consumes a thread-pool
  thread per carrier; under load with a five-way fan-out that starves the service.
- **A failed read-model write is swallowed.** The relational record has committed;
  throwing would retry the message and re-underwrite the quote — trading a cache
  miss for real data corruption.

---

## Phase 3 — SQL Server Persistence

Status: **Complete**

### Completed
- `PolicyFlowDbContext` with per-entity `IEntityTypeConfiguration` classes.
- `InitialCreate` migration: 7 tables, 6 unique indexes, 1 filtered index,
  `rowversion` on `Quotes` and `Policies`.
- Repositories, `UnitOfWork` with provider-error translation.
- `DesignTimeDbContextFactory` so `dotnet ef` needs no API host.

### Verification
- `dotnet ef database update` against the SQL Server container → **PASS**
- Schema inspected in real SQL Server → **PASS**, all 6 unique indexes present
  including `UX_Payments_PolicyId_IdempotencyKey` and `UX_Policies_QuoteId`

### Important decisions
- **Idempotency scope is `(PolicyId, IdempotencyKey)`**, not the key alone. Keys
  are client-chosen; a globally unique index would reject a legitimate second
  payment from a different customer who picked the same value.
- **The outbox index is filtered** (`WHERE ProcessedUtc IS NULL`). A processed
  message leaves the index entirely, so the publisher's hot query stays fast at
  volume.
- **`UPDLOCK, HOLDLOCK` on number allocation.** Without it two concurrent
  submissions claim the same number and the loser gets an unexplainable 500.
- **`Migrate()`, never `EnsureCreated()`.** `EnsureCreated` bypasses the migration
  history table, so the schema it builds can never be upgraded.

### Bug found and fixed
**`EnableRetryOnFailure` forbids user-initiated transactions.** Every quote
submission returned 500: *"SqlServerRetryingExecutionStrategy does not support
user-initiated transactions."* Fixed by wrapping the transaction in
`Database.CreateExecutionStrategy()` — which is also the correct semantics, since
a transient fault then retries the whole operation rather than a commit on a
transaction that already rolled back.

---

## Phase 4 — NoSQL Carrier Catalog and Read Models

Status: **Complete**

### Completed
- `CarrierCatalogDbContext` over MongoDB via `MongoDB.EntityFrameworkCore` 10.0.3.
- `MongoCarrierCatalog` with a compiled-in fallback panel.
- `MongoQuoteComparisonStore` — denormalised comparison documents.
- `MongoIndexInitializer` — unique, covering and TTL indexes created at startup.

### Verification
- Catalog seeded → **PASS**, 5 carriers
- Indexes → **PASS**: `ux_carriers_carrierId`, `ix_carriers_active_sortOrder`,
  `ux_quote_comparisons_quoteId`, `ix_quote_comparisons_quoteNumber`,
  `ttl_quote_comparisons_generatedUtc` (30 days)
- Comparison document served with `baseline: 1075, ratingMs: 2011` → **PASS**

### Important decisions
- **MongoDB is a cache, not a source of truth.** Failed writes are logged and
  swallowed; failed reads fall back to a SQL projection; money is re-read from SQL
  before anything binds. Both fallbacks have tests.
- **A TTL index on comparison documents.** Nothing in the application deletes a
  read model, so without it the collection grows without bound.

### Two bugs found and fixed
**The EF Mongo provider cannot map `Dictionary<string, ComplexType>`.** It reads
it as an unresolvable navigation: *"Unable to determine the relationship
represented by navigation 'CarrierDocument.Coverages'."* Restructured to
`List<T>` of owned entities declared with `OwnsMany`, carrying the former key as
a field. Dictionaries with *primitive* values map fine, which is why
`StateFactors` stayed a dictionary.

**MongoDB transactions require a replica set.** The provider wraps `SaveChanges`
in a transaction by default: *"Standalone servers do not support transactions."*
Set `AutoTransactionBehavior.Never`, which is correct here because every
`SaveChanges` on this context writes exactly one document — already atomic — and
seeding is idempotent by carrier id.

---

## Phase 5 — Quote REST API

Status: **Complete**

### Completed
- `POST /api/quotes` → 202 with `Location`; `GET /api/quotes/{id}`;
  `GET /api/quotes/{id}/offers`; `GET /api/quotes/lookup`;
  `GET /api/carriers`.
- Three-layer validation, `ProblemDetails` on every failure path.
- `CorrelationIdMiddleware` with header validation.
- `/health/live` and `/health/ready`, OpenAPI in Development.

### Verification
- `dotnet test tests/PolicyFlow.Api.IntegrationTests` → **PASS, 71 tests**
- Manual `curl` → 202 Accepted with `Location` and echoed correlation id

### Important decisions
- **202, not 201.** The quote exists; its price does not. Underwriting is 1.5–3 s
  of third-party latency, and holding the request open would couple a page load
  to the slowest carrier.
- **The supplied correlation id is validated before use.** It reaches log output
  and a response header, so an unvalidated value is a log-injection vector. Bad
  values are replaced with a generated GUID rather than rejected — a bad trace id
  is no reason to fail a quote.
- **Liveness checks nothing.** A liveness probe that checks the database restarts
  a healthy container during a database blip.

### Bug found and fixed
**Swashbuckle 10 uses OpenAPI.NET v2**, which flattened `Microsoft.OpenApi.Models`
into `Microsoft.OpenApi`, replaced `OpenApiString` with `JsonNode`, changed
`OpenApiSchema.Type` to the `JsonSchemaType` flags enum, and replaced inline
`Reference` population with `OpenApiSecuritySchemeReference`.

---

## Phase 6 — Service Bus and Underwriting Worker

Status: **Complete**

### Completed
- `infra/servicebus/config.json` — `sbemulatorns` namespace, `quote-submitted`
  queue, 5-minute lock, `MaxDeliveryCount` 5.
- `ServiceBusEventPublisher`, `OutboxPublisherService` (send → ack → mark).
- `NoOpEventPublisher` that **throws** rather than pretending to succeed.
- `UnderwritingWorker` with three dedup layers and transient/permanent triage.

### Verification
- `docker compose up --build` → **PASS**
- Live run: **Pending → Processing → Rated**, 15 offers, 5 carriers
- Correlation id traced across API *and* worker logs → **PASS**
- `FAIL-DEPENDENCY`: `DeliveryCount` 1→5 then dead-lettered → **PASS**
- Redelivery of a rated quote is a no-op → **PASS** (application tests)

### Important decisions
- **The outbox ordering is the whole point.** Send, wait for the broker
  acknowledgement, *then* mark processed. Reversing it reintroduces exactly the
  gap the outbox closes. In this order a crash causes a re-send, which is safe
  because the consumer deduplicates.
- **At-least-once, never exactly-once.** Not achievable across a database and a
  broker without distributed transactions.
- **`ProcessedMessage` PK is composite** `(MessageId, Consumer)`. Keying on
  `MessageId` alone would stop a second subscriber ever processing the event.
- **Transient and permanent failures get opposite treatment.** Dependency outage →
  abandon and retry. Domain violation → dead-letter immediately.
- **The API owns the outbox sweep, not the worker.** Both would compete for the
  same rows.

### Two bugs found and fixed
**`ServiceBusEventPublisher` only implements `IAsyncDisposable`**, which a
synchronous `CreateScope()` cannot dispose — the sweep failed every 2 seconds.
Fixed by registering it as a singleton (it owns a `ServiceBusSender` that should
be long-lived anyway) and switching to `CreateAsyncScope()`.

**The Service Bus emulator has no management API.**
`ServiceBusAdministrationClient.QueueExistsAsync` failed against a perfectly
healthy queue, leaving readiness Unhealthy for 5 seconds per check. Switched to a
data-plane `PeekMessageAsync`, which also does not consume from the queue the
worker reads.

---

## Phase 7 — Policy and Idempotent Payment

Status: **Complete**

### Completed
- `POST /api/policies/from-quote/{quoteId}`, `GET /api/policies/{id}`,
  `POST /api/policies/{id}/payments`.
- Three-layer idempotency: unique index, pre-read, constraint-violation catch.

### Verification
- Live: two identical payment requests → **one** row, second marked replayed
- Missing `Idempotency-Key` → **400** with nothing persisted
- **20 concurrent payments with the same key → 1 row** → **PASS**
- **10 concurrent binds of one quote → 1 policy, 9 × 409** → **PASS**

### Important decisions
- **The unique index is the guarantee; the pre-read is an optimisation.** The
  pre-read cannot survive two concurrent requests on two servers.
- **200 on both the original and the replay.** A retrying client must not be able
  to tell them apart.
- **A changed amount on the same key is ignored.** The key identifies the intent;
  charging an unconfirmed amount would be worse.
- **`payments:write` is a separate policy from `policies:write`**, so the ability
  to charge can be revoked independently of the ability to bind.

---

## Phase 8 — Security

Status: **Complete**

### Completed
- JWT bearer validation with **algorithm pinning**, issuer/audience/lifetime
  validation and a 2-minute clock skew.
- Simulated Entra ID issuer (`POST /api/auth/dev-token`) behind three independent
  guards.
- Six authorization policies; CORS allowlist; rate limiting partitioned by
  identity then IP; security headers in the API *and* nginx.
- `docs/SECURITY.md` with all thirteen production gaps listed.

### Verification
- Security headers present on `/` and `/api/*` → **PASS**
- `ErrorResponses_DoNotLeakStackTraces` → **PASS**
- Overlong and newline-bearing correlation ids replaced → **PASS**
- Containers run as non-root (`USER app`) → **PASS**

### Important decisions
- **Startup throws on unsafe configuration**, rather than warning. A dev token
  endpoint enabled outside Development, or a symmetric signing key outside
  Development, fails the boot.
- **`REQUIRE_AUTHENTICATION=false` by default, and the startup log says so.** The
  policies exist with real names on the same attributes, so enforcing is a config
  change.
- **The anonymous mobile lookup is a bounded decision.** Two factors, identical
  404s for a wrong surname and an unknown number, rate limited, read-only — and
  recorded as a production gap because a surname is a weak second factor.
- **No PCI DSS scope by design.** No card data, token or merchant credential
  exists anywhere in the payment path.

### Bug found and fixed
**Authorization was 401-ing despite `REQUIRE_AUTHENTICATION=false`.** `Program.cs`
logged that authorization was relaxed but never implemented it, so every
`[Authorize]` endpoint returned 401 — which would also have made the Docker demo
unusable. Fixed in `SecurityExtensions` by making each policy permissive when
relaxed, and setting a permissive `DefaultPolicy` for bare `[Authorize]`.

---

## Phase 9 — React SPA

Status: **Complete**

### Completed
- Five routes: landing, new quote, quote status + comparison, policy + payment,
  mobile lookup.
- `useQuotePolling` (stops at terminal, caps at 30 attempts) and
  `usePaymentIntent` (one key per intent, reused on retry).
- Corporate red/white/blue design tokens; mobile-first throughout.
- nginx with SPA fallback, `/api` reverse proxy, immutable asset caching.

### Verification
- `npm run lint` → **PASS** (clean)
- `npm run test:ci` → **PASS, 41 tests**
- `npm run build` → **PASS**
- Deep link `/quotes/new` through nginx → **HTTP 200**
- Security headers from nginx → **PASS**

### Important decisions
- **Polling, not websockets.** Right-sized for a 2-second workflow: no connection
  to keep alive, no reconnection logic, no sticky sessions, and the status
  endpoint is one indexed read.
- **The stop condition is the server's `isTerminal` flag**, not a client-side list
  of final statuses.
- **The idempotency key is held in a ref and is not cleared after a failure.** A
  lost response is indistinguishable from a lost request, so the retry must carry
  the same key. There is a test asserting this.
- **Mobile-first specifics:** 44px touch targets, 16px minimum input font (below
  that iOS Safari zooms the page), `100dvh` not `100vh`, pinch-zoom not blocked,
  and the lookup returns status *and* offers in one round trip.

### Two bugs found and fixed
**The web container was `unhealthy` while serving fine.** `localhost` resolves to
IPv6 `::1` first in the nginx image while nginx listens on IPv4 only, so the probe
got "connection refused". Both Dockerfiles now use `127.0.0.1`.

**nginx discarded every security header from the HTML shell.** If a `location`
declares any `add_header`, it drops *all* inherited ones — and `location /` had a
`Cache-Control` header. Caught with `curl -D -`; fixed with
`nginx-security-headers.conf` included in every such block.

---

## Phase 10 — Legacy Web Forms and the Compatibility Seam

Status: **Complete**

### Completed
- `POST /legacy/quotes` and `GET /legacy/quotes/{quoteNbr}`.
- `LegacyQuoteAdapter` — translation only, no business logic.
- A real Web Forms application: `Default.aspx`, `QuoteEntry.aspx`,
  `QuoteStatus.aspx` (`UpdatePanel` + `Timer` polling), `Site.master`,
  `Global.asax`, `Web.config`, `PolicyFlowApiClient`.
- `src/legacy/PolicyFlow.Legacy.sln`, separate from the main solution.
- `docs/LEGACY-MODERNIZATION.md`.

### Verification
- Live: `returnCode=0`, `Q-2026-000002`, `"12,000"` parsed correctly → **PASS**
- `GET /legacy/quotes/{nbr}` → `RATED`, `annualPrem` as a fixed-decimal string
- **`LegacyAndModernSubmissions_ProduceIdenticalPricing`** → **PASS**
- **`LegacyOfferSets_MatchTheModernOfferSetsExactly`** → **PASS**
- `msbuild src\legacy\PolicyFlow.Legacy.sln /p:Configuration=Debug` → **PASS**
- `msbuild ... /p:Configuration=Release /t:Rebuild` → **PASS**
- Compiled assembly verified: `PolicyFlow.Legacy.WebForms.dll` v2.4.1.0,
  `.NETFramework,Version=v4.8`, referencing `System.Web 4.0.0.0` and
  `System.Web.Extensions`
- **`aspnet_compiler -p . -v / <out>` → PASS (exit 0)** — full ASP.NET
  precompilation, which validates the `.aspx` markup, every `Inherits=`
  attribute against its code-behind class, every server-control ID against its
  designer field, the master-page content placeholders, `Global.asax` and
  `Web.config`. Produced `App_Web_*.dll` for all three pages plus the master
  page, and `App_global.asax.dll`.

### Important decisions
- **Excluded from `PolicyFlow.slnx`, Docker and CI.** Web Forms needs `System.Web`,
  which is Windows-only; `POC-INSTRUCTIONS.md` §14 says explicitly not to attempt
  containerizing it. Excluding it keeps `dotnet build` green everywhere.
- **The legacy solution lives in `src/legacy/`, not the repo root**, so bare
  `dotnet build` stays unambiguous.
- **HTTP 200 with a return code, never a 4xx.** The legacy client predates
  `ProblemDetails`. Documented so nobody "fixes" it later.
- **Status is exposed by quote number, not GUID.** The legacy database only stored
  the printed number; requiring the GUID would force a schema change on the side
  we are trying not to touch.
- **The project file reports missing prerequisites explicitly.** The template's
  default `Condition="'$(VSToolsPath)' != ''"` is wrong — MSBuild sets a fallback
  path even without the web workload, so the import fails with an opaque MSB4226.

### Two bugs found by actually building it

**The repository-root `Directory.Build.props` was leaking into the legacy tree.**
MSBuild walks up the directory tree, so a .NET Framework 4.8 project was
inheriting `<Nullable>enable</Nullable>` with `WarningsAsErrors=CS8618` — twenty
errors on DTOs that are nullable *by design*, since every field arrives from an
old client as a possibly-absent string. It was also inheriting
`<TargetFramework>net10.0</TargetFramework>`, because the root file sets that when
the property is empty and legacy projects use the *old* property name
(`TargetFrameworkVersion`), so the condition matched.

Fixed with `src/legacy/Directory.Build.props`, which deliberately shadows the
root file (MSBuild stops at the first one found and this one does not import the
parent) and pins `LangVersion` to 7.3 so no modern language feature can creep
into code meant to demonstrate the "before" side.

**`Web.config` declared a namespace the project does not reference.** The Web
Forms project template adds `<add namespace="System.Web.Optimization" />` under
`<pages><namespaces>`, but that type lives in the
`Microsoft.AspNet.Web.Optimization` NuGet package. This project has **zero** NuGet
dependencies — a consequence of the business logic having moved to the API — so
`aspnet_compiler` failed with `CS0234` while `msbuild` on the project alone
succeeded. Exactly the kind of defect that only surfaces at deploy time. Removed.

### Remaining limitation
`Microsoft.WebApplication.targets` is absent (it ships with Visual Studio's
*ASP.NET and web development* workload, not with the Framework or BuildTools), so
`msbuild` cannot run the project's **publish** target. The project emits a clear
warning saying so rather than failing. Compilation and full `.aspx`
precompilation both work without it, which is everything needed to verify the
code is correct.

---

## Phase 11 — Docker Compose

Status: **Complete**

### Completed
- Six services: `sql`, `mongo`, `servicebus-emulator`, `api`, `worker`, `web`.
- Health-check-gated startup ordering.
- Multi-stage, non-root Dockerfiles; `.dockerignore`.
- `${VAR:?error}` on every secret so a missing value fails fast by name.

### Verification
From a completely clean state (`docker compose down -v`):

```
docker compose up -d --build   → 6 containers, all healthy
/health/ready                  → Healthy (sql-server, mongodb, service-bus)
POST /api/quotes               → Pending  Q-2026-000001
async underwriting             → Rated after 4s
GET /offers                    → 15 offers, baseline $1075, ratingMs 2011
POST /policies/from-quote      → POL-2026-000001  Ironclad Direct  $595.60
payment ×2 same key            → 1 row persisted, second marked replayed
POST /legacy/quotes            → returnCode=0  Q-2026-000002
SPA + nginx proxy              → HTTP 200, 5 carriers
```

### Important decisions
- **The Service Bus emulator shares the main SQL container** rather than running a
  second Azure SQL Edge instance, saving ~1.5 GB and one more startup dependency.
- **`servicebus-emulator` uses `service_started`, not `service_healthy`.** The
  image ships no health check, so requiring one would wait forever; the API's own
  readiness probe verifies the queue is reachable.
- **The worker waits on `api: service_healthy`**, which also guarantees migrations
  have been applied before it touches the schema.
- **The worker uses the `runtime` base image, not `aspnet`.** It has no HTTP
  surface.

### Bug found and fixed
**`.dockerignore` was missing**, so the host's `bin/` and `obj/` were copied into
the image, overwriting the container's restore output with `project.assets.json`
files referencing Windows NuGet paths. The worker build failed with
`NETSDK1064`. This is not a build-speed optimisation — it is what makes the build
work at all.

Also: `Serilog.AspNetCore` in the worker pulled in a framework reference to
`Microsoft.AspNetCore.App`, which the `runtime` image lacks, so the container
restart-looped. Replaced with `Serilog.Extensions.Hosting` — the right dependency
for a process that serves no HTTP.

---

## Phase 12 — CI and Documentation

Status: **Complete**

### Completed
- `azure-pipelines.yml`: Backend and Frontend in parallel → Containers →
  Package, with an explicit secret check and a commented-out manual deployment
  stage.
- `infra/azure/main.bicep` and `infra/azure/README.md` with cost warnings.
- Ten documents: `README.md`, `ARCHITECTURE.md`, `API.md`,
  `LEGACY-MODERNIZATION.md`, `SECURITY.md`, `DEMO-SCRIPT.md`,
  `INTERVIEW-TALKING-POINTS.md`, `TROUBLESHOOTING.md`, `DEVOPS.md`,
  `PROGRESS.md`.

### Verification
- `azure-pipelines.yml` parses as valid YAML; 4 active stages → **PASS**
- `docker compose config` → **PASS**

### Important decisions
- **No `pr:` trigger, deliberately.** For Azure Repos, PR validation is a *branch
  policy*; a `pr:` trigger is the GitHub convention and is silently ignored — a
  good way to believe PRs are validated when they are not.
- **Integration tests run against a real SQL Server service container in CI.**
  Against an in-memory provider they would pass without the unique constraints
  existing at all, which is worse than no test.
- **The secret check is code, not a comment.** The Package stage fails the
  pipeline if a `.env` reaches the artifact.
- **Deployment is commented out and gated on manual approval.** Container Apps has
  a free grant but ancillary resources still cost money.

---

## Phase 13 — Underwriter Work Queue (Angular in the Legacy Portal)

Status: **Complete**

### Completed
- **Domain:** `QuoteStatus.FollowUp`; `Quote.ApproveReferral`, `RequestFollowUp`,
  `DeclineReferral`, legal only from `Referral`/`FollowUp`, each recording
  `ReviewedBy`, `ReviewedUtc`, `ReviewNote`.
- **Persistence:** `AddUnderwriterReview` migration (three nullable columns); queue
  read served by the existing `IX_Quotes_Status_CreatedUtc`.
- **Application:** `CarrierMarketplace` extracted from `UnderwriteQuoteHandler` so
  approval shops the panel through the same code; `GetUnderwritingQueueHandler`,
  `GetUnderwritingReviewHandler`, `ReviewReferralHandler` and its validator.
- **API:** `GET /api/underwriting/queue`, `GET /api/underwriting/quotes/{id}`,
  `POST /api/underwriting/quotes/{id}/decision`, behind a new `referrals:review`
  policy granted to the `Underwriter` role.
- **Angular 22 module** (`src/legacy/PolicyFlow.Legacy.Underwriting`): standalone
  components, signals store, reactive form with decision-dependent validators,
  functional HTTP interceptor, zoneless; builds into the Web Forms project.
- **Web Forms host:** `Underwriting.aspx` plus nav and home-page links; the legacy
  status page treats `FOLLOWUP` as terminal.
- **React SPA:** shows a customer the underwriter's follow-up request.
- **CI:** an Angular job in the Frontend stage (test, production build, artifact).

### Verification
- `dotnet build` → **PASS**, 0 warnings
- `dotnet test` → **PASS, 366** (161 domain, 119 application, 86 integration on real SQL Server)
- `npm run test:ci` (Angular module) → **PASS, 67**; `npm run build` → **PASS**, 250 kB
- `npm run lint`, `npm run test:ci` (React) → **PASS, 41**
- `msbuild src\legacy\PolicyFlow.Legacy.sln` → **PASS**
- `aspnet_compiler` on the Web Forms project, including `Underwriting.aspx` → **PASS (exit 0)**
- Rebuilt containers; migration applied on startup
- Live: queue lists referrals from both channels; follow-up without a note → 400;
  approving a risk no carrier will write → 422 with its reason; follow-up → 200 and
  the customer status endpoint shows `FollowUp` with the note
- **Legacy portal under IIS Express** (`http://localhost:8095`), driven in Chrome:
  `QuoteEntry.aspx` postback → `Q-2026-000013` PENDING → `QuoteStatus.aspx`
  UpdatePanel polling → REFERRAL → `Underwriting.aspx` loads the Angular module and
  the queue cross-origin, Enter in its search box causes **no** postback, follow-up
  recorded → `QuoteStatus.aspx` shows FOLLOWUP with the note and stops polling;
  approval from `Underwriting.aspx` → Rated with 9 offers

### Important decisions
- **Angular module in a Web Forms host, not a Web Forms page.** New code is written
  against the platform that outlives the shell. See `LEGACY-MODERNIZATION.md`.
- **Modern endpoints, not the legacy seam.** The seam exists for code that cannot
  change.
- **Approval overrides the house rule, never carrier appetite.** A risk no carrier
  will write cannot be approved; the review screen shows appetite up front so the
  underwriter knows before pressing Approve.
- **Approval calls carriers synchronously**, unlike quote submission: an internal
  user needs the answer now, the fan-out is concurrent, and no transaction is open
  during it.
- **The decision-maker comes from the token**, never the request body; relaxed-auth
  decisions are recorded as `demo-underwriter (unauthenticated)`.
- **`FollowUp` is terminal** for the worker and for polling clients.

### Bugs found and fixed
- **FluentValidation applies a trailing `.When()` to every validator in the chain.**
  `Must(required).Must(minLength).When(notBlank)` silently switched off the
  required check for blank notes — caught by the application and integration tests
  (a blank follow-up reached the domain and returned 422 instead of 400).
- **`formControlName` discards errors set with `setErrors()`.** When the directive
  attaches it recomputes validity, so a server 400 applied that way vanished. The
  server's message is now a validator tied to the note text it was about.
- **Two effects touching one form control have no guaranteed order.** The reset
  effect could wipe the server error. Merged into one ordered effect.
- **Enter in the queue search box would post back the Web Forms page**, because the
  master page wraps content in `<form runat="server">`. Prevented, with a spec.
- **API timestamps have no `Z`.** SQL Server `datetime2` materialises with an
  unspecified kind, so `new Date()` would read "time in queue" in local time. The
  module parses `...Utc` fields as UTC.

- **`QuoteEntry.aspx` and `QuoteStatus.aspx` had never rendered.** Found the first
  time the portal ran under IIS Express, and older than this phase: with
  `httpRuntime targetFramework="4.8"`, validator controls default to unobtrusive
  validation, which requires a `jquery` ScriptResourceMapping this project does not
  have. Both pages threw on render and `customErrors` silently redirected to
  `Default.aspx`. `msbuild` and `aspnet_compiler` both passed, because it is a
  render-time failure. Fixed with
  `ValidationSettings:UnobtrusiveValidationMode=None` in `Web.config`.
- **CORS for the IIS-hosted portal.** An existing `.env` overrides the compose
  default, so the API rejected the Angular module's cross-origin calls until
  `http://localhost:8095` (command line) and `https://localhost:44390` (Visual
  Studio) were added to `CORS_ALLOWED_ORIGINS`.

---

## Phase 14 — Underwriter Work Queue in React (Contrast)

Status: **Complete**

### Completed
- `/underwriting` route and nav link in the React SPA: `UnderwritingPage`,
  `WorkQueue`, `QuoteReview`, `useUnderwritingWorkbench`, `reviewNoteRules`.
- Feature parity with the Angular module: status filter and search, time in queue,
  read-only application, eligibility trace, carrier appetite with Approve disabled
  when no carrier will write, note rules per decision, server 400 beside the note,
  409 reloads queue and quote, plain-language outcome.
- Client methods and wire types for `/api/underwriting`; `parseUtc`,
  `formatDateTime`, `formatTimeInQueue` helpers.
- Concern-by-concern comparison in `LEGACY-MODERNIZATION.md`.
- No API, domain or database change: both UIs share the Phase 13 endpoints.

### Verification
- `npm run lint` → **PASS** (clean); `npm run test:ci` → **PASS, 81** (40 new);
  `npm run build` → **PASS**
- Web container rebuilt; walked in Chrome at `http://localhost:8080/underwriting`

### Important decisions
- **A page-scoped hook, not a global store.** Nothing outside the page needs the
  queue state.
- **Reset by `key`, validation derived during render.** No effects synchronising
  form state — the idiomatic React counterparts of the Angular module's effect and
  reactive-form validators, which is what the contrast is meant to show.
- **Refs for values read in async callbacks.** State captured by a callback is the
  value from its render, so a double-click inside one render would otherwise send
  two decisions. A test fires both calls in the same `act`.

---

## Phase 15 — Agent Workspace

Status: **Complete**

### Completed
- **Domain/persistence:** `Quote.SubmittedBy` (optional on `Quote.Submit`, trimmed,
  length-checked); `AddQuoteSubmittedBy` migration with
  `IX_Quotes_SubmittedBy_CreatedUtc`.
- **Application:** `SubmitQuoteCommand.SubmittedBy`;
  `GetQuoteHandler.ListSubmittedByAsync` returning `QuoteStatusView`s.
- **API:** `POST /api/quotes` attributes the quote from the token;
  `GET /api/quotes/mine` lists the caller's quotes and returns 401 without a token.
- **React:** `/agent` route and nav link — simulated sign-in (`agentSession`),
  `QuoteForm` extracted from the new-quote page and shared, `useAgentQuotes`
  (refreshes while any quote is non-terminal, pauses at a cap), quote table with
  status, outcome, premium and a link to the comparison page.

### Verification
- `dotnet build` → **PASS**, 0 warnings; `dotnet test` → **PASS, 384**
  (167 domain, 125 application, 92 integration on real SQL Server)
- `npm run lint` → **PASS**; `npm run test:ci` → **PASS, 104**; `npm run build` → **PASS**
- Containers rebuilt, migration applied; walked in Chrome at `http://localhost:8080/agent`

### Important decisions
- **Identity from the token, never the body** — the same rule as underwriter
  decisions.
- **`/mine` is 401 even with authorization relaxed.** Relaxed policies let an
  anonymous caller *act*; they cannot tell the API who "mine" is.
- **The token is opt-in per API call.** Sent when submitting as the agent and listing
  their quotes, never on customer or underwriter calls — otherwise a decision made
  on the underwriting page in the same browser would be recorded as the agent's.
- **One quote form.** Extracted rather than copied, so the customer and agent pages
  cannot validate differently. The existing new-quote tests pass unchanged against
  the extracted component.
- **Refreshing has a cap.** A quote stuck in Processing (worker down, dead-lettered
  message) must not keep an open tab polling indefinitely.
- **Signed-in and signed-out are separate components**, so signing out unmounts the
  workspace and the next agent never sees the previous agent's list.

---

## Acceptance criteria

### Functional
- [x] User can submit a quote
- [x] API returns `202 Accepted`
- [x] Quote begins `Pending`
- [x] Service Bus worker processes the quote asynchronously
- [x] Quote becomes `Rated` or `Referral`
- [x] Rated quote can become a policy
- [x] Payment endpoint requires an idempotency key
- [x] Duplicate payment does not create a duplicate transaction
- [x] Legacy contract maps into the same application logic
- [x] SPA demonstrates the workflow
- [x] **5 carriers × 3 tiers = 15 priced options** *(extension)*
- [x] **Responsive, mobile-first, with a mobile quote lookup** *(extension)*
- [x] **NoSQL catalog and read models managed through EF Core** *(extension)*
- [x] **A real legacy Web Forms project** *(extension)*

### Technical
- [x] .NET 10 solution builds cleanly — 0 warnings, 0 errors
- [x] SQL Server migrations work
- [x] Domain tests pass — 130
- [x] Application tests pass — 90
- [x] API integration tests pass — 71, against real SQL Server
- [x] Frontend tests and build pass — 41, lint clean
- [x] Docker Compose works from a clean state
- [x] Correlation ID visible across API and worker logs
- [x] Service Bus consumer is retry-safe — verified 1→5 then dead-letter
- [x] Outbox prevents losing the event between commit and publication
- [x] No secrets committed
- [x] OpenAPI works in Development
- [x] Health endpoints work

### DevOps
- [x] Pipeline builds backend
- [x] Pipeline runs backend tests
- [x] Pipeline builds frontend
- [x] Pipeline validates and builds Docker assets
- [x] Pipeline publishes deployable artifacts
- [ ] `main` has required PR build validation — **needs an Azure DevOps
      organisation**; the pipeline and the step-by-step policy setup are in
      `docs/DEVOPS.md`
- [ ] At least one feature PR has passed validation — **same prerequisite**

---

## Remaining issues

One item cannot be completed in this environment:

**Azure DevOps branch policy is not configured.** It requires an Azure DevOps
organisation and a pushed repository. `azure-pipelines.yml` is complete and valid
(4 stages, parses cleanly), and `docs/DEVOPS.md` has the step-by-step setup
including how to prove the policy actually blocks a merge.

One partial limitation:

**The legacy project's `publish` target is unavailable.**
`Microsoft.WebApplication.targets` ships with Visual Studio's *ASP.NET and web
development* workload, not with the Framework or MSBuild BuildTools. The project
warns clearly rather than failing. Compilation (Debug and Release) and full
`aspnet_compiler` precompilation both succeed, which verifies the code itself.

Everything else is implemented and verified: the full stack runs end-to-end from
a clean Docker state, 291 .NET tests and 41 frontend tests pass, both solutions
build with zero warnings, and the legacy Web Forms application compiles and
precompiles.
