# Troubleshooting

Diagnosis by symptom. Every issue below was actually encountered building this
POC, and the fix is recorded in the code where it was applied.

---

## First: three commands

```bash
docker compose ps                          # what is up, and is it healthy
curl -s http://localhost:8081/health/ready # which dependency is unhappy
docker compose logs --tail=50 api worker   # what the processes are saying
```

`/health/ready` names the failing dependency and its check duration, which is
usually enough to locate the problem:

```json
{
  "status": "Unhealthy",
  "checks": [
    { "name": "sql-server",  "status": "Healthy",   "durationMs": 1.9 },
    { "name": "mongodb",     "status": "Degraded",  "description": "MongoDB is unreachable..." },
    { "name": "service-bus", "status": "Unhealthy", "durationMs": 5095.6 }
  ]
}
```

A 5-second check duration is a timeout, not slowness — that is a connection
being refused.

---

## Startup

### `MSSQL_SA_PASSWORD is required. Copy .env.example to .env.`

You have not created `.env`.

```bash
cp .env.example .env
# then replace every <placeholder>
```

The `${VAR:?message}` syntax in `docker-compose.yml` fails fast with the variable
name rather than starting a container with a silent default. That is deliberate.

### SQL Server container starts then immediately exits

```bash
docker compose logs sql | tail -20
```

Two usual causes:

**Password complexity.** SQL Server requires 8+ characters with at least three
of: uppercase, lowercase, digit, symbol. The container refuses to start otherwise
and says so.

**Memory.** SQL Server needs roughly 2 GB. Docker Desktop → Settings →
Resources → raise the memory limit to at least 4 GB.

### `Globalization Invariant Mode is not supported`

`InvariantGlobalization` is enabled somewhere. `Microsoft.Data.SqlClient` refuses
to run in invariant mode. `Directory.Build.props` sets it to `false` explicitly
with a comment explaining why; if you have overridden it, undo that.

### Containers build but the worker restarts in a loop

```bash
docker compose logs worker | tail -20
```

If you see `Framework 'Microsoft.AspNetCore.App' ... was not found`, something in
the worker has pulled in an ASP.NET Core framework reference while the Dockerfile
uses the smaller `runtime` base image.

This happened with `Serilog.AspNetCore`. The fix was to use
`Serilog.Extensions.Hosting` instead — the worker has no HTTP surface, so
shipping a web stack into it is wrong anyway. Check for a newly added package
with a framework reference before reaching for the heavier base image.

### `docker compose up --build` fails with `NETSDK1064: Package ... was not found`

The host's `bin/` and `obj/` folders have been copied into the build context,
overwriting the container's restore output with `project.assets.json` files that
point at Windows NuGet paths.

`.dockerignore` excludes them. If you have modified it, restore those entries —
this is not a build-speed optimisation, it is what makes the build work at all.

---

## Quote processing

### A quote stays `Pending` forever

The single most common problem. It means the outbox is not draining.

```bash
docker compose logs api | grep -i outbox
```

**If you see `Outbox publisher is not running: no Service Bus connection string
is configured`:**
`SERVICEBUS_CONNECTION_STRING` is missing from `.env`. Quotes are persisted
correctly but nothing will ever underwrite them.

**If you see `Outbox message publish failed and will be retried`:**
The emulator is unreachable. Check it:

```bash
docker compose logs servicebus-emulator | tail -30
```

You want to see `Creating queue: quote-submitted` and `Emulator Service is
Successfully Up!`. The emulator depends on SQL Server, so if SQL is unhealthy the
emulator never starts either.

**If you see `'ServiceBusEventPublisher' type only implements IAsyncDisposable`:**
A regression. The publisher must be registered as a **singleton** and the outbox
sweep must use `CreateAsyncScope()`, not `CreateScope()`. Both are in
`DependencyInjection.cs` and `OutboxPublisherService.cs` with comments explaining
why.

**Inspect the outbox directly:**

```bash
docker exec policyflow-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d PolicyFlow -W -s "|" \
  -Q "SELECT TOP 10 Id, EventType, ProcessedUtc, RetryCount, LastError
      FROM OutboxMessages ORDER BY OccurredUtc DESC"
```

`ProcessedUtc IS NULL` with a rising `RetryCount` means the publisher is trying
and failing; `LastError` says why. **Rows staying unprocessed is the outbox
working correctly** — it is refusing to lose the event.

### A quote reaches `Processing` and stops

The worker claimed it and then failed.

```bash
docker compose logs worker | grep -iE "abandon|dead-letter|error"
```

If the customer name is `FAIL-DEPENDENCY`, this is **intended** — the simulated
bureau fails, the message is abandoned and retried five times, then dead-lettered
by the broker. The quote correctly remains `Processing`: it was claimed but never
finished.

For any other cause, the log line names the exception. A `DomainException` is
dead-lettered immediately because retrying would fail identically.

### `SqlServerRetryingExecutionStrategy does not support user-initiated transactions`

Returned as a 500 from `POST /api/quotes`.

The provider is configured with `EnableRetryOnFailure`, and a retrying strategy
refuses a user-initiated transaction unless the whole transaction sits inside the
retriable unit.

`UnitOfWork.ExecuteInTransactionAsync` wraps its work in
`Database.CreateExecutionStrategy()`. If this error reappears, something is
calling `BeginTransactionAsync` outside that wrapper.

### `DeliveryCount` climbs but the quote never completes

Working as designed for a genuine dependency failure. After
`SERVICEBUS_MAX_DELIVERY_COUNT` (5) the broker dead-letters the message.

To inspect the dead-letter queue you need a Service Bus client — the emulator has
no management API. In practice, the worker logs already say why each delivery
failed.

---

## The carrier comparison

### `baseline: null` and `totalRatingMs: 0`

The API is serving the **SQL fallback** instead of the MongoDB read model. The
offers are correct; the baseline and timing are not available from that path.

```bash
docker compose logs worker | grep -i comparison
```

**`Comparison read model was not updated`** means the write failed. This is
non-fatal by design — the relational record has already committed, and throwing
would cause the message to be retried and the quote re-underwritten.

Common causes:

**`Unable to determine the relationship represented by navigation
'CarrierDocument.Coverages' of type 'Dictionary<string, CoverageSubDocument>'`**
The EF Mongo provider cannot map a dictionary whose value is a complex type.
Nested collections must be `List<T>` of owned entities, declared with
`OwnsMany` in `CarrierCatalogDbContext.OnModelCreating`. Dictionaries with
*primitive* values are fine.

**`Standalone servers do not support transactions`**
The provider wraps `SaveChanges` in a transaction by default, and MongoDB
supports transactions only on a replica set. `CarrierCatalogDbContext` sets
`Database.AutoTransactionBehavior = AutoTransactionBehavior.Never`, with a
comment explaining why that is correct here (every save writes one document).

### The carrier panel is empty, or prices look wrong

```bash
docker compose logs api | grep -i "carrier catalog"
```

**`Carrier catalog seeded. CarrierCount=5`** — good.

**`Carrier catalog seeding failed; the compiled-in panel will be used`** — Mongo
is unreachable or the model is invalid. Quoting still works from
`CarrierPanel.All`, so this degrades rather than breaks. The exception follows on
the next log line.

**`Carrier catalog is unreachable; using the compiled-in panel. Pricing
configuration changes made in MongoDB will NOT be reflected`** — this is the
warning that matters. Silently serving stale pricing is how a system quotes last
quarter's rates for a month before anyone notices.

Verify the documents:

```bash
docker exec policyflow-mongo mongosh --quiet \
  -u policyflow_app -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin \
  --eval 'db=db.getSiblingDB("policyflow_catalog");
          print("carriers: "+db.carriers.countDocuments());
          db.carriers.find({},{CarrierId:1,BaseAnnualPremium:1,_id:0}).forEach(printjson)'
```

### Fewer than fifteen offers

Usually **correct**. Carriers have different appetites:

| Carrier | Min. prior coverage | Max. mileage | Does not write |
|---|---|---|---|
| Liberty Crest | 0 | 30,000 | — |
| Summit Shield | 6 | 25,000 | AL, MS |
| BlueRidge | 0 | 20,000 | **FL, TX** |
| Patriot Vale | 0 | 40,000 | — |
| Ironclad Direct | 12 | 22,000 | AL, MS |

Check the `declinations` array in `GET /api/quotes/{id}/offers` — each entry
states the reason, and `wasUnavailable` distinguishes a gateway *failure* from a
carrier *declining*.

If `wasUnavailable: true` on everything, the gateway is throwing. That is
expected for customer name `FAIL-CARRIER`.

---

## Payments

### `400 Bad Request` on a payment

The `Idempotency-Key` header is missing. It is required, and the API refuses
rather than guessing, because an unkeyed payment cannot be made safe.

```bash
curl -X POST http://localhost:8081/api/policies/$POLICY_ID/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"amount":125.00}'
```

### A retry created a second payment

The client sent a **different key**. That is the one way to defeat the
protection.

Generate one key per payment *intent* and reuse it on every retry of that intent.
`usePaymentIntent.ts` holds the key in a ref for the life of the intent and does
**not** clear it after a failure — a failed attempt may still have been processed
server-side, so the retry must carry the same key.

Verify what actually persisted:

```bash
docker exec policyflow-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d PolicyFlow -W -s "|" \
  -Q "SELECT PolicyId, IdempotencyKey, Amount, ExternalTransactionId
      FROM Payments ORDER BY CreatedUtc DESC"
```

### `409 Conflict` binding a policy

The quote is already bound. Guaranteed by `UX_Policies_QuoteId`, so it holds even
under concurrent requests — ten simultaneous binds produce one policy and nine
409s.

---

## Frontend

### The SPA loads but every API call fails

```bash
curl -s http://localhost:8080/api/carriers   # through the nginx proxy
curl -s http://localhost:8081/api/carriers   # direct to the API
```

If direct works and proxied does not, the nginx proxy is misconfigured. It
forwards to `http://api:8080/api/` — the Compose service name, not `localhost`,
because inside the container `localhost` is the nginx container itself.

### The web container is `unhealthy` but the site works

The health check is probing the wrong address. `localhost` resolves to IPv6 `::1`
first in the nginx image, while nginx listens only on IPv4 `0.0.0.0:8080`, so the
probe gets "connection refused" against a perfectly healthy server.

Both Dockerfiles use `127.0.0.1` explicitly for this reason.

### Security headers are missing from the HTML shell

An nginx inheritance rule: if a `location` block defines **any** `add_header`, it
**discards every** `add_header` inherited from the enclosing `server`.

Setting them once at server level and then adding `Cache-Control` inside
`location /` silently drops all of them from exactly the responses that matter
most. The fix is `nginx-security-headers.conf`, included in every block that adds
headers of its own.

```bash
curl -sD - -o /dev/null http://localhost:8080/ | grep -iE "content-security|x-frame"
```

### A deep link 404s on refresh

`/quotes/<guid>` has no file on disk. The SPA fallback (`try_files $uri $uri/
/index.html`) lets React Router resolve it. Check that `location /` block exists.

### `npm run build` fails with `'test' does not exist in type 'UserConfigExport'`

Vitest configuration has been added to `vite.config.ts`. It belongs in
`vitest.config.ts` — kept separate so the production build never typechecks
against vitest, and the Docker build stage does not depend on test tooling.

### Polling never stops

The client stops on the server's `isTerminal` flag. If it is spinning forever,
check what the status endpoint actually returns:

```bash
curl -s http://localhost:8081/api/quotes/$QUOTE_ID | grep -o '"isTerminal":[a-z]*'
```

The hook also gives up after 30 attempts (45 seconds) and surfaces a "still
working" message, so an infinite spinner indicates a UI bug rather than a
backend one.

---

## Tests

### Integration tests fail with `MSSQL_SA_PASSWORD is not set`

By design — the factory throws rather than defaulting to a literal, because a
hard-coded test credential is how one ends up committed.

```bash
docker compose up -d sql

# Bash
export MSSQL_SA_PASSWORD=$(grep '^MSSQL_SA_PASSWORD=' .env | cut -d= -f2-)

# PowerShell
$env:MSSQL_SA_PASSWORD = (Get-Content .env | Select-String '^MSSQL_SA_PASSWORD=').ToString().Split('=',2)[1]
```

### Integration tests fail with `No SQL Server connection string`

The factory publishes its configuration as **process environment variables** in
its constructor, because the top-level `Program.cs` reads configuration during
service registration — before `WebApplicationFactory.ConfigureAppConfiguration`
is applied.

Because those variables are process-global, all test classes share a single
factory through `IntegrationTestCollection`. Two factories would overwrite each
other's connection string. If you add a test class, give it
`[Collection(IntegrationTestCollection.Name)]`.

### Integration tests leave databases behind

Each run creates `PolicyFlowTests_<guid>` and drops it on teardown. A crashed run
can leave one. Cleanup is intentionally best-effort — failing teardown would mask
a real assertion failure.

```bash
docker exec policyflow-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "SELECT name FROM sys.databases WHERE name LIKE 'PolicyFlowTests_%'"
```

Or just `docker compose down -v`.

### A Vitest test fails with `expected 'The server returned an unexpected response.'`

A `Response` body can only be read **once**. `mockResolvedValue` returns the same
instance to every call, so the second read gets a consumed body. Use
`mockImplementation(() => Promise.resolve(...))` to create a fresh `Response` per
call.

---

## Ports

```bash
netstat -ano | findstr ":8080 :8081 :1433 :27017 :5672"   # Windows
lsof -i :8080,:8081,:1433,:27017,:5672                     # macOS/Linux
```

Override in `.env` rather than editing `docker-compose.yml`:

```bash
WEB_HTTP_PORT=9080
API_HTTP_PORT=9081
```

`1433`, `27017` and `5672` are hard-coded in `docker-compose.yml`. If they
collide, either stop the conflicting service or edit the port mapping — the
containers talk to each other over the Compose network, so only the host side of
the mapping matters.

---

## Clean slate

In increasing order of severity:

```bash
docker compose restart api worker          # just the app
docker compose down && docker compose up -d # recreate containers, keep data
docker compose down -v && docker compose up --build   # discard all data
```

Nuclear, including cached images:

```bash
docker compose down -v --rmi local
docker builder prune -f
docker compose up --build
```

---

## Reading the logs

Every log line is structured, so grep on a field:

```bash
# One correlation id across both processes
docker compose logs api worker | grep "<correlation-id>"

# Every underwriting outcome
docker compose logs worker | grep "Operation=Underwrite"

# Slow carriers
docker compose logs worker | grep "Operation=RateCarrier" | grep -oE "CarrierId=[a-z-]+ .*ElapsedMs=[0-9]+"

# Everything the outbox did
docker compose logs api | grep "Operation=OutboxPublisher"

# Idempotency in action
docker compose logs api | grep -E "Replayed|RaceCollapsed"
```

`Result=` is the outcome field on every significant operation: `Accepted`,
`Rated`, `Referral`, `Declined`, `Replayed`, `Abandoned`, `DeadLettered`,
`Conflict`, `Failed`.

**Never in the logs:** bearer tokens, idempotency keys, request bodies,
connection strings. If you need to correlate a payment, use the
`ExternalTransactionId`, which is a synthetic reference and safe to log.
