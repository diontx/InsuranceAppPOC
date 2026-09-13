# Demo Script

A timed five-minute walkthrough. Each section states what to show, what to say,
and the architectural point it makes.

**Before you start:**

```bash
docker compose up -d
curl http://localhost:8081/health/ready        # all three Healthy
```

Have open: the SPA at http://localhost:8080, and a terminal running
`docker compose logs -f` (API and worker interleaved — that is the point).

---

## 0. Frame it — 20 seconds

> This is PolicyFlow, an auto-insurance quote marketplace. A customer submits one
> application and we shop it against five carriers, returning up to fifteen
> priced options.
>
> Every carrier and every premium is fictional — the algorithms are deliberately
> simple so the numbers can be checked by hand. What is real is the
> architecture: ASP.NET Core on .NET 10, SQL Server and MongoDB both through EF
> Core, Azure Service Bus, a React front end, and a legacy Web Forms application
> running alongside it.

Show the repository tree briefly. Point at `src/` — six .NET projects, a React
app, and `legacy/`.

> Dependencies point inward. The domain project references nothing, which is why
> its 130 tests run in about 130 milliseconds with no database and no mocks.

---

## 1. Submit a quote — 45 seconds

**Browser:** http://localhost:8080 → *Start a quote*.

Point at the carrier panel on the landing page first:

> Those five carriers are read live from MongoDB, not hard-coded markup.

Fill the form (defaults are fine) and submit.

**Immediately** point at the status transitions: **Pending → Processing → Rated.**

> The API returned **202 Accepted**, not 201. The quote exists; its price does
> not yet.
>
> Underwriting calls a prior-insurance bureau and five carrier gateways —
> roughly one and a half to three seconds of third-party latency. If I held the
> HTTP request open for that, a customer's page load would be coupled to the
> slowest third party in the chain, and my capacity would be a function of
> someone else's uptime.
>
> So the API does one thing: persist the quote and return. The browser polls.

---

## 2. Trace it across two processes — 60 seconds

**Terminal:**

```bash
docker compose logs api    | grep <correlationId>
docker compose logs worker | grep <correlationId>
```

(The correlation id is shown at the bottom of the quote status page.)

Walk the lines:

```
[API]    Quote accepted.        Operation=SubmitQuote  ElapsedMs=225
[API]    Event published.       Operation=PublishEvent MessageId=f643a03c...
[WORKER] Quote claimed for underwriting.
[WORKER] Prior insurance verified.  ElapsedMs=708
[WORKER] Carrier rated. CarrierId=liberty-crest       ElapsedMs=387
[WORKER] Carrier rated. CarrierId=summit-shield       ElapsedMs=562
[WORKER] Carrier rated. CarrierId=blueridge-assurance ElapsedMs=311
[WORKER] Carrier rated. CarrierId=patriot-vale        ElapsedMs=494
[WORKER] Carrier rated. CarrierId=ironclad-direct     ElapsedMs=215
[WORKER] Quote rated.  OfferCount=15  FanOutMs=564  ElapsedMs=1778
[WORKER] Message processed. DeliveryCount=1 Result=Completed
```

> One correlation id, two processes, and every step in between. That is the
> difference between "the site is slow" and a single log query.
>
> Look at the carrier timings. Individually they total about two seconds. The
> fan-out took **564 milliseconds** — roughly the slowest carrier, not the sum.
> They run concurrently, and each one's failure is isolated, so a slow carrier
> cannot take down the panel.
>
> The delays are `Task.Delay`, never `Thread.Sleep`. A blocking sleep would
> consume a thread-pool thread per carrier; with a five-way fan-out under load,
> that is how a service starves itself.

---

## 3. The comparison — 30 seconds

**Browser:** the fifteen offers.

> Fifteen options — five carriers, three coverage tiers each — cheapest first.
> Filterable by tier.

Expand **"How this price was calculated"** on one card.

> Every offer carries its full rating trace. Base premium, state factor, vehicle
> factor, mileage factor, tier factor, discounts, final. Pricing is completely
> deterministic — no randomness anywhere, including the simulated latency, which
> is derived from the quote id. Same input, same price, every time.

If a carrier declined (submit a Florida quote to guarantee it):

> BlueRidge is not licensed in Florida, so it returns nothing and we say so. A
> short panel is a correct answer, not a bug — and we distinguish a carrier
> *declining* from a gateway *failing*.

---

## 4. The data stores — 45 seconds

```bash
docker exec policyflow-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d PolicyFlow -W -s "|" \
  -Q "SELECT t.name AS TableName, i.name AS IndexName
      FROM sys.indexes i JOIN sys.tables t ON i.object_id=t.object_id
      WHERE i.is_unique=1 AND i.name IS NOT NULL ORDER BY t.name"
```

> SQL Server is not just persistence here. Look at what the *schema* enforces.
>
> `UX_Policies_QuoteId` — a quote can back at most one policy.
> `UX_Payments_PolicyId_IdempotencyKey` — that one I will come back to.
>
> And the outbox index is **filtered**: `WHERE ProcessedUtc IS NULL`. A processed
> message leaves the index entirely, so the publisher's hot query stays fast no
> matter how many million rows accumulate behind it.

Then MongoDB:

```bash
docker exec policyflow-mongo mongosh --quiet \
  -u policyflow_app -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin \
  --eval 'db=db.getSiblingDB("policyflow_catalog");
          print("carriers: "+db.carriers.countDocuments());
          print("comparisons: "+db.quote_comparisons.countDocuments())'
```

> Two stores, chosen for different jobs. SQL Server holds anything needing ACID
> transactions and constraints. MongoDB holds carrier configuration and the
> comparison read model — both deeply nested, both read-mostly, neither needing a
> transaction.
>
> Both go through EF Core, so it is the same programming model.
>
> The critical discipline: **MongoDB is a cache, not a source of truth.** If the
> read-model write fails, we log it and move on — the relational record has
> already committed, and throwing would cause the message to be retried and the
> quote re-underwritten. We would be trading a cache miss for real data
> corruption. If the read *fails*, we project from SQL instead. There are tests
> for both.

---

## 5. Payment idempotency — 75 seconds

**This is the centrepiece. Do not rush it.**

**Browser:** buy a policy on one of the offers, then on the policy page:

1. Enter an amount, press **Pay now**. → Approved, with a transaction reference.
2. Press **"Submit the same payment again."**

> Same transaction id. Same amount. Marked as a duplicate. And the payment
> history below still shows **one row.**

Point at the idempotency key displayed on screen:

> One key per payment *intent*, generated once and reused on every retry. The
> naive version — a fresh key per HTTP attempt — looks identical in the UI and
> defeats the protection completely, because the server sees every retry as a
> new payment. There is a frontend test that asserts the key is reused.

Then the part that matters:

```bash
docker compose logs api | grep -i "replayed\|collapsed"
```

> Three layers, in order of strength.
>
> The **unique index on `(PolicyId, IdempotencyKey)`** is the actual guarantee.
> The application pre-read is an optimisation that turns the common retry into a
> fast 200. And if the pre-read loses a race, we catch the constraint violation
> and return the original payment.
>
> The pre-read alone cannot work. Two concurrent requests on two servers both
> pass it before either insert commits. The index can, and does — there is an
> integration test that fires **twenty simultaneous requests** and asserts one
> row.
>
> And note: disabling the button in the UI is not a substitute. By the time a
> request has timed out it has already reached the server. The next client will
> not have that button at all.

Then the scope decision:

> The constraint is scoped to the policy, not global. Keys are chosen by clients
> — two customers could independently pick the same one, and a globally unique
> index would reject a legitimate second payment.

---

## 6. Failure, on demand — 45 seconds

Submit a quote with customer name **`FAIL-DEPENDENCY`**, then:

```bash
docker compose logs worker | grep -E "DeliveryCount|Abandoned"
```

```
Transient dependency failure; abandoning for redelivery. DeliveryCount=1 MaxDeliveryCount=5
Transient dependency failure; abandoning for redelivery. DeliveryCount=2 MaxDeliveryCount=5
...
DeliveryCount=5
```

> The verification service is down. The worker **abandons** the message so
> Service Bus redelivers it, and after five attempts the broker dead-letters it.
>
> The important part is that we distinguish transient from permanent. A
> dependency outage gets retried, because it is expected to recover. A domain
> rule violation gets dead-lettered immediately, because every retry will fail
> identically and retrying it just blocks throughput and fills the logs.
>
> That distinction is where most message handlers go wrong in one direction or
> the other.

Then the reliability story behind it:

> This all rests on the transactional outbox. The quote row and the
> `QuoteSubmitted` event are written in the **same SQL transaction**. If I had
> committed the quote and *then* published to the queue, a crash between the two
> would leave a Pending quote nobody ever underwrites — no error, no alert, just
> a customer who never gets a price.
>
> The publisher sends first, waits for the broker acknowledgement, and only
> *then* marks the row processed. Getting that order backwards reintroduces
> exactly the gap the outbox exists to close. In this order a crash causes a
> re-send, which is safe, because the consumer deduplicates.
>
> Which is why delivery is at-least-once and never exactly-once. Exactly-once
> across a database and a broker is not achievable without distributed
> transactions. At-least-once plus an idempotent consumer is the practical
> answer.

---

## 7. Mobile — 30 seconds

Resize the browser to phone width, or open on a real phone:
`http://<your-ip>:8080/lookup`

Look up the quote by number + surname.

> The comparison screen is most likely to be opened on a phone, so it is
> mobile-first — every base style targets a phone and media queries add layout
> upward, not the reverse.
>
> Concretely: 44-pixel touch targets, 16-pixel minimum input font because below
> that iOS Safari zooms the page when a field is focused, `100dvh` rather than
> `100vh` because `100vh` includes the retracting URL bar, and a grid that goes
> from one column to four with no breakpoint arithmetic.
>
> This lookup returns status *and* all fifteen offers in **one** round trip. On a
> cellular connection a second request is a visible delay.

On the security of it:

> It is anonymous, and that is a deliberate decision with limits. Quote numbers
> are sequential and guessable, so the surname is a second factor — and a wrong
> surname returns a byte-identical 404 to an unknown quote number, so it cannot
> be used to enumerate. It is rate limited and read-only.
>
> For production that is not enough. A surname is often public. The right answer
> is a one-time link to a verified channel, and that is written down as a
> production gap rather than glossed over.

---

## 8. Legacy modernization — 60 seconds

```bash
curl -s -X POST http://localhost:8081/legacy/quotes \
  -H "Content-Type: application/json" \
  -d '{"InsuredName":"Sam Carter","RiskState":"GA","VehYear":"2023",
       "VehMake":"Toyota","VehModel":"Camry",
       "Mileage":"12,000","PriorCovMonths":"36"}'
```

```json
{"returnCode":0,"returnMessage":"QUOTE ACCEPTED FOR UNDERWRITING",
 "quoteNbr":"Q-2026-000004","quoteStatus":"PENDING"}
```

> PascalCase fields, every value a string, a numeric return code, and HTTP 200
> even on a validation failure. That is the contract the existing Web Forms
> application already posts, and `"12,000"` with a comma is what the page
> actually sends.
>
> I deliberately did **not** try to containerize Web Forms on Linux — it needs
> `System.Web`, which only exists on Windows, and there is no upgrade path
> because Web Forms was never ported to .NET Core.
>
> Instead I modelled the migration seam. The legacy application translates its
> contract into the modern application layer.

Then the point:

> The seam holds **no business logic.** It calls the same `SubmitQuoteHandler`
> the React app reaches. Rating exists once.
>
> And that is not an aspiration — there are two tests that fail the build if it
> ever stops being true. One asserts the same risk through both contracts
> produces an identical premium and best carrier. The other asserts all fifteen
> carrier, tier and premium rows match exactly.
>
> Because the real failure mode in legacy modernization is not that the old
> system still exists. It is **the same business rule existing twice** — and then
> drifting, until the same customer gets a different price depending on which
> channel they used.

If there is time, open `src/legacy/PolicyFlow.Legacy.WebForms/QuoteEntry.aspx.cs`:

> This code-behind used to hold the entire workflow inline — parse the textboxes,
> apply surcharges, look up a hard-coded state table, open a `SqlConnection`,
> insert with a concatenated statement, write the premium into a label. Several
> hundred lines, inseparable from the page lifecycle, and therefore never unit
> tested, because instantiating it meant instantiating a `Page` with an
> `HttpContext`.
>
> Now it gathers input, calls the API, renders the response. The project has zero
> NuGet dependencies — there is nothing left for it to depend on.
>
> It is thin enough to delete now. That is the goal.

---

## 7a. The agent's side — 45 seconds

**Browser:** http://localhost:8080/agent

Sign in as `a.patel`, then submit two quotes: one clean, one with prior coverage
`0`. Point at **My quotes**:

> Pending, then Rated or Referral, without touching Refresh. The list refreshes
> while anything is in underwriting and goes quiet once everything has an outcome —
> and it gives up after a couple of minutes, so a stuck quote cannot keep a tab
> polling forever.

Sign out and in as `m.okafor`:

> Empty. Quotes are attributed from the token — there is no agent id in the request
> for anyone to change — and "my quotes" filters on that same claim. Without a
> token it is a 401 even in this relaxed demo, because the API cannot know whose
> quotes to return.

The referred quote from this step is the one to open in the underwriting queue next.

---

## 8a. The underwriter's side — 60 seconds

**Before:** `npm start` in `src/legacy/PolicyFlow.Legacy.Underwriting`, and submit
a quote with `priorInsuranceMonths: 0` (a legacy one via the curl above works too).

**Browser:** http://localhost:4200

> Referred quotes need a human. This is the underwriter's work queue, and it lives
> in the legacy portal — that is where internal users already work. But it is not
> a Web Forms page. It is an Angular module the page hosts, calling the modern
> API.

Click the referral. Point at, in order:

1. **The read-only application.** "The underwriter decides on what was applied for."
2. **The rule that referred it.** "Computed by the same `QuoteRatingService` the
   worker ran. The browser knows no rules."
3. **Carrier appetite — 3 of 5.** "Before I press Approve I can see who will price
   it."

Press **Approve**.

> That shopped the carrier panel through the same code the worker uses — I
> extracted it rather than copy it, and a test asserts an approved referral prices
> exactly as the rating service does. The quote is Rated; the customer can buy it.

Optionally look it up at http://localhost:8080/lookup to show the offers.

Then a risk no carrier will write (`annualMileage: 95000`):

> Approve is unavailable, and it says why. Approval overrides our house rule, not a
> carrier's appetite. If I force it through the API, it is a 422.

On why Angular inside Web Forms:

> New code should not be written against the thing being retired. This moves to
> any host unchanged. It is the strangler pattern for the UI: the seam keeps old
> pages working, and new pages arrive already modern inside the old shell.
>
> Two details that only show up when you actually host it: the master page wraps
> everything in one server-side form, so Enter in a search box would post the
> whole page back — prevented, with a test. And it runs zoneless, so it is not
> patching the ASP.NET AJAX library's timers on the same page.

For contrast, open **http://localhost:8080/underwriting** beside it — the same
queue built in React in the modern SPA:

> Same API, same rules, same behaviour — it cannot differ, because neither UI owns a
> rule. What differs is the framework and the host. In React the form resets by
> remounting on a `key` and validation is derived during render; in Angular it is a
> reactive form whose validators follow the decision. And three things the Angular
> version has to handle — CORS, the Web Forms server form, and style scoping — are
> not Angular at all. They are the cost of living inside the legacy page, and they
> go away with it.

If asked about concurrency:

> Two underwriters, one quote: the status check gives a clean 409, and the
> `rowversion` guarantees it — an integration test fires five simultaneous
> decisions and exactly one is recorded. The UI then reloads the quote so the
> second underwriter sees what happened.

---

## 9. Tests and CI — 45 seconds

```bash
dotnet test
```

```
PolicyFlow.Domain.Tests        130 passed   (130 ms)
PolicyFlow.Application.Tests    90 passed
PolicyFlow.Api.IntegrationTests 71 passed   (real SQL Server)
```

```bash
cd src/PolicyFlow.Web && npm run test:ci   # 41 passed
```

> 291 .NET tests and 41 frontend tests.
>
> The split is deliberate. Domain tests cover every rating rule and every legal
> and illegal state transition, with no infrastructure at all. Application tests
> cover the orchestration — including that a redelivered message is a no-op.
>
> The integration tests run against **real SQL Server**, and that is not
> incidental. The guarantees that matter most in this system — payment
> idempotency, duplicate-bind prevention, `rowversion` concurrency — are enforced
> by unique indexes. Against an in-memory provider those tests would pass without
> the constraints existing at all. That would be worse than no test, because it
> would give false confidence.

Show `azure-pipelines.yml`:

> Backend and frontend run in parallel. Containers build only if both pass.
> Artifacts are published from `main` only, with an explicit check that fails the
> pipeline if a `.env` ever reaches the deployment package.
>
> Note there is no `pr:` trigger. For Azure Repos, PR validation is a **branch
> policy** — a `pr:` trigger is the GitHub convention and is silently ignored,
> which is a good way to believe your PRs are validated when they are not.

---

## Questions to expect, and short answers

**"Why two databases? Isn't that overengineering for a POC?"**
Each is doing something the other is bad at. The carrier catalog is ragged nested
configuration — relationally that is five child tables and a migration to add a
factor. The comparison is a denormalised read model for a mobile screen. And
crucially, neither needs a transaction, so neither belongs in the store that
provides one. If the document store is down, the system degrades rather than
failing — there are tests for that.

**"Why not exactly-once delivery?"**
It is not achievable across a database and a broker without distributed
transactions. At-least-once plus an idempotent consumer is the practical answer,
and the three dedup layers are what make it safe.

**"Why is the domain not using a full DDD framework?"**
The aggregates own their invariants and expose behaviour rather than setters,
which is the part that pays. A mediator, event sourcing and a repository per
entity would be machinery this problem does not need.

**"What would you change for production?"**
`docs/SECURITY.md` has a table of thirteen gaps. The top three: a real identity
provider instead of the simulated one, `REQUIRE_AUTHENTICATION=true`, and an
append-only audit trail for binding and payment events — structured logs are not
a tamper-evident audit record.

**"What is simulated?"**
Five carrier gateways, the prior-insurance bureau, the payment gateway, and the
identity provider. Each is behind an interface and documented as POC-only in its
own file. What is faithfully modelled is their *shape*: remote, slow,
independently failing, and never to be called from inside a synchronous HTTP
request.

**"Did the whole thing work first time?"**
No — and the fixes are worth mentioning. `EnableRetryOnFailure` forbids
user-initiated transactions unless they are wrapped in the execution strategy, so
every quote submission threw until `UnitOfWork` was fixed. The EF Mongo provider
cannot map a dictionary with a complex value type. The Service Bus emulator has
no management API, so the obvious health check fails against a healthy queue. And
nginx discards inherited `add_header` directives in any location that declares
one of its own, which silently dropped every security header from the HTML shell.
All four are documented where they were fixed.
