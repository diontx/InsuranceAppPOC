# Interview Talking Points

Defensible positions on each architectural choice, with the reasoning and the
trade-off. The value of a POC is not the feature count — it is being able to
explain why each decision was made and what it cost.

---

## Legacy and modern .NET

> The POC demonstrates how I would modernize an older ASP.NET estate
> incrementally. Existing applications stay operational while business logic is
> extracted into services and newer workflows move behind APIs.

**The thing I would emphasise:** the real failure mode in legacy modernization is
not that the old system still exists. It is **the same business rule existing
twice.**

That is what actually goes wrong. Rating lives in `QuoteEntry.aspx.cs`
code-behind. A mobile channel is needed. The rules cannot be called from anywhere
else — they are entangled with `txtMileage.Text` and a `Page` lifecycle — so they
get copied. The two copies drift, and the same customer gets a different price
depending on which channel they used. Nobody notices for months.

So the seam in this POC holds **no** business logic, and two tests fail the build
if that ever changes:
`LegacyAndModernSubmissions_ProduceIdenticalPricing` and
`LegacyOfferSets_MatchTheModernOfferSetsExactly`.

**Why not just rewrite it?** Three reasons. Web Forms was never ported to .NET
Core, so there is no upgrade path — the framework is the wall. A big-bang rewrite
bets everything on one cutover reproducing years of undocumented behaviour
correctly, with no fallback. And it means a long feature freeze, which is rarely
something the business will actually agree to.

**What I deliberately did not do:** containerize Web Forms on Linux. It needs
`System.Web`. Attempting it would have burned time proving something that cannot
work. The legacy project is a real, buildable .NET Framework 4.8 application,
excluded from the main solution and CI so `dotnet build` stays green everywhere.

---

## REST APIs

> Resource-oriented endpoints, correct status codes, validation,
> `ProblemDetails`, correlation IDs, asynchronous I/O, and explicit idempotency
> for transaction-sensitive operations.

**Why `202`, not `201`, on quote submission.** The quote exists; its price does
not. Underwriting calls a bureau and five carrier gateways — 1.5 to 3 seconds of
third-party latency. Holding the request open couples a customer's page load to
the slowest third party in the chain, and makes my capacity a function of someone
else's uptime.

**Why `200` on both the original payment and its replay.** A retrying client must
not be able to distinguish them. Returning 201 then 200 would tell a client
something it should not act on, and some clients treat a changed status code as a
failure.

**Why `422` and not `400` for a business-rule violation.** 400 means "I cannot
parse this." 422 means "I understood you and the answer is no." Clients can act
on that difference — a 400 means fix the request, a 422 means the request was
fine and the state is wrong.

**The status-code map** is in `GlobalExceptionHandler`, and the more important
half of that class is security: never leak internals. Stack traces, SQL text and
type names all reveal the shape of the system. The full exception goes to the log;
the client gets a generic message and a correlation id.

---

## SQL Server

> SQL Server is not just persistence here. Constraints, indexes, migrations,
> `rowversion` and transaction boundaries protect business integrity.

The two constraints I would point at:

```sql
UX_Policies_QuoteId                   -- a quote can back at most one policy
UX_Payments_PolicyId_IdempotencyKey   -- the idempotency guarantee
```

**The schema enforces the rule; the application only produces a nicer error.**
That distinction matters. The application pre-read cannot survive two concurrent
requests on two servers. The index can. There is an integration test that fires
twenty simultaneous payments and asserts one row.

**Why the idempotency scope is `(PolicyId, IdempotencyKey)` and not the key
alone.** Keys are chosen by clients. Two customers could independently pick the
same value, and a globally unique index would then reject a legitimate second
payment. A key only needs to be unique within the resource it acts on.

**The filtered index** is worth mentioning:

```sql
IX_OutboxMessages_ProcessedUtc_OccurredUtc  WHERE ProcessedUtc IS NULL
```

A processed message leaves the index entirely, so the publisher's hot query stays
fast no matter how many million rows accumulate behind it.

**`rowversion`** on `Quotes` and `Policies` means EF Core includes it in the
`WHERE` clause, so a lost update raises `DbUpdateConcurrencyException` — mapped
to a 409 — instead of silently overwriting a concurrent change.

**Money is `decimal(18,2)`, never float.** Binary floating point cannot represent
cents exactly, and premiums are summed and compared.

**`UPDLOCK, HOLDLOCK`** on the number allocator. Without it, two concurrent
submissions read the same `LastValue` and both claim the same number; the unique
index rejects the loser and the customer sees a 500 for no reason they can act
on.

---

## Event-driven architecture

> The API returns quickly while underwriting runs asynchronously. The outbox
> closes the reliability gap between a database transaction and message
> publication, and the consumer is designed for at-least-once delivery.

**The specific gap the outbox closes.** If the API committed the quote and *then*
published to Service Bus, a crash between the two leaves a `Pending` quote nobody
will ever underwrite. No error, no alert — just a customer who never gets a
price. Writing the event in the same transaction makes "quote saved" and "event
will be sent" one atomic fact.

**The ordering is the whole point, and it is easy to get backwards:**

1. Read unsent rows
2. Send and **wait for the broker acknowledgement**
3. *Only then* mark processed

Marking processed before the send reintroduces exactly the gap the outbox exists
to close. In this order a crash causes a **re-send** — safe, because the consumer
deduplicates.

**Which is why delivery is at-least-once, never exactly-once.** Exactly-once
across a database and a broker is not achievable without distributed
transactions. At-least-once plus an idempotent consumer is the practical answer,
and being able to say that clearly matters more than the implementation.

**Three dedup layers**, cheapest first: a `ProcessedMessage` primary-key seek; the
quote state machine refusing a terminal→terminal transition; and the composite
primary key catching two workers racing.

**The composite key is deliberate.** Keying on `MessageId` alone would mean a
second, unrelated consumer of the same event could never process it — a bug that
only surfaces the day a second subscriber is added.

**Transient versus permanent failure.** These need opposite responses, and
conflating them causes either lost work or infinite retry loops. A dependency
outage is **abandoned** and redelivered. A domain-rule violation is
**dead-lettered immediately**, because every retry fails identically and retrying
just blocks throughput and fills the logs.

**`Task.Delay`, never `Thread.Sleep`.** A blocking sleep consumes a thread-pool
thread per carrier; with a five-way fan-out under load, that is how a service
starves itself.

---

## Polyglot persistence

> SQL Server holds anything needing ACID transactions and constraints. MongoDB
> holds carrier configuration and the comparison read model. Both through EF
> Core.

**Why the catalog is a document.** Carrier configuration is deeply nested and
*ragged* — each carrier has different state factors, a different discount list,
possibly different coverage tiers. Relationally that is five child tables and a
join per read, and adding a carrier-specific factor is a schema migration. As a
document it is one read, and a new factor is a new field.

It is also read-mostly and needs no transaction, so it does not belong in the
store that provides one.

**That this is real, not decorative:**
`CatalogPricingChange_ChangesThePremium` edits the catalog and asserts the next
quote prices differently. An operator can change pricing without a redeploy.

**The discipline that makes polyglot persistence safe: one source of truth.** The
relational store is authoritative; MongoDB is a cache. Three rules follow:

- A failed **write** is logged and swallowed. The relational record has committed;
  throwing would cause a retry and re-underwriting — trading a cache miss for
  real data corruption.
- A failed **read** falls back to projecting from SQL. Slower, correct.
- Money is re-read from SQL before anything binds. BSON stores doubles; the
  cent-exact value lives in `decimal(18,2)`.

Both the write and read rules have tests.

**Where I would push back on myself:** two stores in a POC invites the
overengineering question, and it is fair. My answer is that each is doing
something the other is genuinely bad at, and the system degrades rather than
failing when the optional one is down. If the comparison payload were three
fields, one store would be right.

---

## Troubleshooting and observability

> Correlation IDs and structured logs let me trace a quote from HTTP request
> through SQL, messaging and worker processing instead of guessing where latency
> or failure occurred.

The demo I would actually run:

```bash
docker compose logs api worker | grep <correlation-id>
```

One id, two processes, every step with `ElapsedMs`. The carrier timings show
`FanOutMs=564` while individual carriers took 215–562 ms — which proves the
fan-out is concurrent rather than sequential, from the logs alone.

**The correlation id is validated before use**, and that is a security point
rather than a hygiene one. It lands in log output and a response header, so an
unvalidated value is a log-injection and header-injection vector — a newline
would let a caller forge log lines. Anything outside 64 chars of
`[A-Za-z0-9._:-]` is replaced with a generated GUID rather than rejected, because
a bad trace id is no reason to fail a customer's quote.

**Structured, not formatted.** `ElapsedMs` and `CarrierId` are queryable fields.
That is the difference between finding the slow carrier in one query and grepping
a log file.

**Never logged:** bearer tokens, the idempotency key (client-chosen, could contain
anything), request bodies, connection strings, or the rejected value of a
malformed correlation id — logging that would *be* the injection.

**Liveness versus readiness.** `/health/live` checks nothing. A liveness probe
that checks the database will restart a healthy container during a database blip.
`/health/ready` checks dependencies, so a starting instance stays out of the load
balancer.

**Degraded is not Unhealthy.** MongoDB down reports *Degraded* because there is a
fallback; pulling a working instance out of rotation for a degradation would be
wrong. Service Bus down reports *Unhealthy*, because quotes would be accepted and
never underwritten.

---

## Testing strategy

> 291 .NET tests and 41 frontend tests, split by what each layer can actually
> prove.

| Suite | Count | Needs | Proves |
|---|---|---|---|
| Domain | 130 | nothing | Every rating rule and boundary; every legal and illegal transition; culture-independent trace rendering |
| Application | 90 | in-memory EF | Orchestration, redelivery is a no-op, channel parity |
| Integration | 71 | **real SQL Server** | The constraints that actually guarantee correctness |
| Frontend | 41 | jsdom | Key reuse on retry, polling stops at terminal |

**The integration tests run against real SQL Server, and that is not
incidental.** The guarantees that matter most — payment idempotency,
duplicate-bind prevention, `rowversion` concurrency — are enforced by unique
indexes. Against an in-memory provider those tests would pass **without the
constraints existing at all.** That is worse than no test, because it gives false
confidence.

They also use `Migrate()`, not `EnsureCreated()`, so they run against the schema
the migrations actually produce rather than one derived from the model that could
silently diverge.

**Boundary tests, not just happy paths.** Mileage exactly 15,000 must *not* be
surcharged because the rule is `> 15000`; prior coverage of exactly 6 months is
clean because the rule is `< 6`. Off-by-one in a rating rule is a pricing error,
not a crash.

**The frontend test I would point at:**
`reuses the same key when the same intent is submitted again`. A hook that
generated a fresh key per attempt would look identical in the UI and silently
double-charge. That is exactly the class of bug a test should catch.

**Where I chose less.** No mutation testing, no load testing, no contract testing
between the SPA and API. For a POC those would be machinery; I would add contract
tests first if the API gained external consumers.

**`FluentAssertions` was removed** because v8 ships under a commercial licence.
xUnit's built-in assertions do the job and the repository carries no licence
ambiguity.

---

## Security

> Reasonable POC practices, with every production gap written down rather than
> glossed over.

**What is real:** JWT validation with algorithm pinning, six authorization
policies, a CORS allowlist, rate limiting partitioned by identity then IP,
security headers on both the API and nginx, three validation layers, and secrets
only in environment variables.

**Algorithm pinning** is the setting most often missed. Without
`ValidAlgorithms`, a token presenting `"alg": "none"` or a confused-algorithm
variant can bypass signature verification entirely.

**Fail-fast configuration guards.** Startup *throws* if the dev token endpoint is
enabled outside Development, or if a symmetric signing key is configured outside
Development:

> Symmetric keys mean the API can mint the tokens it accepts, so a leaked key
> forges any identity.

A misconfigured deployment fails to boot rather than quietly serving an open
token endpoint.

**Separating `payments:write` from `policies:write`.** Money movement deserves its
own authorization decision and its own audit trail, so the ability to charge can
be revoked independently of the ability to bind.

**The honest part:** `REQUIRE_AUTHENTICATION=false` by default so the demo walks
without a token step, and the startup log says so unambiguously. The policies
still exist with real names on the same attributes, so enforcing is a config
change. `docs/SECURITY.md` lists thirteen production gaps — a real identity
provider, an append-only audit trail, spoofable `X-Forwarded-For`, the weak
second factor on the mobile lookup.

**No PCI DSS scope, by design.** The payment path contains no card number, no
token and no merchant credential. A real integration would tokenise on the client
and never let card data reach the service, which keeps the scope where it is.

**`sa` locally is a shortcut and I would say so.** Production needs an
application login with `db_datareader`/`db_datawriter` plus `EXECUTE` and nothing
else.

---

## Frontend

> React 19 with Vite and TypeScript, mobile-first, behind nginx which
> reverse-proxies the API.

**Proxying rather than cross-origin calls.** Same-origin requests mean no CORS
preflight on every call and no API hostname baked into the bundle at build time.

**Mobile-first literally.** Every base style targets a phone; media queries add
layout upward. The details that actually matter on a device:

- 16px minimum input font — below that iOS Safari zooms the page when a field is
  focused, which makes a form feel broken
- `100dvh` not `100vh` — `100vh` includes the retracting mobile URL bar, so a
  footer sits below the fold
- `auto-fit, minmax(min(100%, 280px), 1fr)` — one column to four with no
  breakpoint arithmetic
- pinch-zoom **not** blocked; `maximum-scale` is an accessibility failure
- the mobile lookup returns status *and* all fifteen offers in one round trip

**Polling, not websockets**, and I would defend that. It is the right amount of
machinery for a workflow that finishes in two seconds: no connection to keep
alive, no reconnection logic, no sticky sessions on a scaled-out API, and the
status endpoint is one indexed primary-key read. It stops on the server's own
`isTerminal` flag rather than the client re-deriving which statuses are final,
and caps at 30 attempts so a broken backend surfaces as an error rather than an
infinite spinner.

**Where I would invest next:** the offer list is not virtualised. At fifteen rows
that is fine; at five hundred it would not be.

---

## CI/CD

> The pipeline verifies backend, frontend and container builds before merge, and
> an Azure Repos branch policy makes successful validation a merge requirement.

**Backend and frontend run in parallel** — neither depends on the other, and it
roughly halves wall-clock time. Containers build only if both pass; building
images for a broken commit wastes agent minutes.

**There is no `pr:` trigger, deliberately.** For Azure Repos, PR validation is a
**branch policy**. A `pr:` trigger is the GitHub convention and is *silently
ignored* by Azure Repos — a good way to believe your PRs are validated when they
are not.

**`npm ci`, not `npm install`.** It installs exactly the locked versions and
fails if the lockfile disagrees with `package.json`. A CI build that silently
resolves a different dependency tree than the one that was tested is not a
validation of anything.

**An explicit secret check** in the packaging stage fails the pipeline if a
`.env` ever reaches the deployment artifact. A comment would not have caught it.

**Deployment is manual and commented out.** Container Apps has a free monthly
grant but ancillary resources still cost money. Infrastructure deployment should
be an explicit decision behind an approval gate, not a side effect of merging.

**Unique image tags**, never `latest` alone — it makes rollback impossible and
the running version unidentifiable.

---

## What I would do differently at scale

Worth having ready, because "what would you change?" is always asked.

**Carrier gateways behind Polly.** The simulated gateways catch their own
failures. Real ones need retry with jitter, a circuit breaker per carrier, and a
timeout budget for the whole fan-out. A carrier that is down should be
short-circuited, not retried on every quote.

**Outbox partitioning.** One publisher sweeping one table is a bottleneck and a
single point of failure. At volume: partition by a hash, or move to Debezium-style
change data capture.

**Read-model rebuild.** There is no way to regenerate the Mongo documents from
SQL in bulk today. That is fine while it is a cache with a TTL, but a production
system needs a replay mechanism for when the projection logic changes.

**Rate versioning.** Carrier configuration is mutable in place. Real insurance
needs effective-dated rate versions and the ability to re-rate a historical quote
at the rates in force on its submission date. That is a significant modelling
change, not a feature toggle.

**Append-only audit trail.** Structured logs are not a tamper-evident audit
record. Binding and payment events need their own immutable store.

**Distributed tracing.** Correlation ids are threaded manually. OpenTelemetry
with the existing `Activity` plumbing would give spans and a service map for
almost no extra code, and would show the carrier fan-out as a proper parallel
span set.

**Least-privilege database accounts** instead of `sa`.

---

## The bugs worth admitting

Being able to describe something that went wrong, and why, is usually more
convincing than a clean narrative. All seven were found by actually running the
thing, not by reading the code.

**`EnableRetryOnFailure` forbids user-initiated transactions.** Every quote
submission returned a 500:
`SqlServerRetryingExecutionStrategy does not support user-initiated
transactions`. The fix is to wrap the transaction in
`Database.CreateExecutionStrategy()`, and it is also the *correct* semantics — a
transient fault retries the whole operation rather than retrying a commit on a
transaction that already rolled back. The consequence is that the delegate must
be idempotent.

**The EF Mongo provider cannot map `Dictionary<string, ComplexType>`.** It reads
it as an unresolvable navigation. Nested collections have to be `List<T>` of
owned entities declared with `OwnsMany`. Dictionaries with *primitive* values are
fine, which is why the state factors stayed dictionaries and the coverages became
a list with the tier as a field.

**The Service Bus emulator has no management API.**
`ServiceBusAdministrationClient.QueueExistsAsync` is the obvious health check and
fails against a perfectly healthy queue. Switched to a data-plane `PeekMessage`,
which also does not consume from the queue the worker is reading.

**nginx discards inherited `add_header` directives.** If a `location` declares any
`add_header`, it drops *every* one inherited from the `server` block. Adding a
`Cache-Control` header inside `location /` silently removed every security header
from the HTML shell. Caught with `curl -D -`, fixed with an included snippet.

**`Directory.Build.props` leaked into the .NET Framework project.** MSBuild walks
up the directory tree, so the legacy Web Forms project inherited
`<Nullable>enable</Nullable>` with `WarningsAsErrors=CS8618` — twenty errors on
DTOs that are nullable *by design*, because every field arrives from an old client
as a possibly-absent string. It also inherited `<TargetFramework>net10.0</...>`,
because the root file sets that when the property is empty and legacy projects use
the *old* property name, `TargetFrameworkVersion`. Fixed with a shadowing
`src/legacy/Directory.Build.props`.

**`aspnet_compiler` found a defect `msbuild` could not.** Compiling the class
library does not check the `.aspx` files. Full ASP.NET precompilation revealed
that the project template's `<add namespace="System.Web.Optimization" />` in
`Web.config` references a type from a NuGet package this project deliberately
does not have — `CS0234`, but only at precompile time. That is the class of
defect that reaches a deployment. Worth knowing that Web Forms needs *two* build
checks, not one.

**Rating traces rendered `¤795.00` instead of `$795.00` in the container.** A bare
`$"{premium:C}"` formats with the *ambient* culture, and the container runs with
no `LANG`, so its default culture is invariant — which has no currency symbol.
`:P0` also rendered `24 %` with a space. The trace is shown on every offer card,
so the wrong output reached the UI, and the same quote rendered differently on a
dev machine than in the container — directly contradicting the determinism the
other rating tests assert.

Fixed by pinning the culture at the point of formatting (`RatingFormat.Culture`)
rather than setting `LANG` in the Dockerfile, because a domain service should not
depend on process-wide state for its output; setting `LANG` would have masked the
symptom and left the dependency in the code. Locked in with 28 tests that run the
rating services under `de-DE`, `fr-FR`, `ja-JP`, `en-GB` and invariant, and assert
the trace is **byte-identical** across all of them.

Each of these is documented in a comment where it was fixed, because the next
person to touch that code needs the reason and not just the workaround.
