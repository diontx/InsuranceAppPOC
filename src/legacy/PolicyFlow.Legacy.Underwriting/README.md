# PolicyFlow Underwriting Module (Angular)

The underwriter work queue for the legacy agent portal: pick a referred quote,
review the application, and **approve**, **request follow-up** or **decline** it.

An Angular 22 standalone application that runs *inside* a Web Forms page
(`../PolicyFlow.Legacy.WebForms/Underwriting.aspx`) and talks to the modern API
(`/api/underwriting`). It is the pattern for adding new functionality to a legacy
shell without writing more Web Forms: the portal keeps its master page, navigation
and deployment, and the new screen is a client-side module with no postbacks, no
ViewState and no business rules.

---

## Run it

The PolicyFlow API must be running (`docker compose up -d` at the repository root).

```bash
npm ci
npm start            # ng serve on http://localhost:4200
```

Open **http://localhost:4200**. The dev server renders the module inside a copy of
the portal chrome using the portal's real `legacy.css`, and proxies `/api` to the
API on `localhost:8081` (`proxy.conf.json`), so no CORS configuration is needed.

To put referrals in the queue, submit a quote with `priorInsuranceMonths: 0`
(or `annualMileage` above 40,000 for a risk no carrier will write).

## Test

```bash
npm test             # Vitest, watch mode
npm run test:ci      # single run
```

67 specs across the API client, interceptor, error normalisation, store, pipes,
both components and the whole workbench against a faked network.

## Build for the portal

```bash
npm run build
```

Writes `main.js` and `styles.css` into
`../PolicyFlow.Legacy.WebForms/Scripts/underwriting/`, with fixed file names so the
`.aspx` page can reference them (the page appends the file's timestamp as a cache
key). The output is git-ignored; `Underwriting.aspx` says plainly when it has not
been built.

Under IIS Express the portal is a different origin from the API, so the API's
`CORS_ALLOWED_ORIGINS` must include `https://localhost:44390` (the compose default
does).

---

## Structure

```
src/app/
├── api/
│   ├── underwriting.models.ts        wire types, mirroring UnderwritingViews.cs
│   ├── underwriting-api.service.ts   HttpClient calls; transport only
│   ├── api-config.ts                 API base address, read from the host element
│   ├── api-problem.ts                ProblemDetails → one error shape
│   └── correlation-id.interceptor.ts X-Correlation-Id on every request
├── state/
│   └── underwriting-store.ts         signals: queue, open quote, decision in flight
├── queue/work-queue.*                the queue: filter, search, select
├── review/quote-review.*             the review form and decision
├── shared/                           status pill, UTC and time-in-queue pipes
├── testing/fixtures.ts               spec data
└── app.*                             the workbench layout (<uw-workbench>)
```

## Design notes

- **No business rules in the browser.** The eligibility trace, carrier appetite,
  and every decision rule come from the API. The only validation duplicated here
  is note length, to save a round trip; the server stays authoritative.
- **Standalone components, signals, zoneless.** Zoneless matters more than usual:
  the host page loads the ASP.NET AJAX library, and zone.js would patch its timers
  and events too.
- **Presentational components, one store.** Components take inputs and emit
  intent; `UnderwritingStore` owns HTTP and state, including discarding a slow
  response for a quote the underwriter has already moved away from, and reloading
  after a 409 when another underwriter decided first.
- **Scoped global styles.** Every rule in `styles.css` is under `uw-workbench`, so
  the bundle cannot restyle the Web Forms controls on the same page.
- **Living inside `<form runat="server">`.** Enter in a text input would submit the
  master page's server form and post the whole page back; the module prevents
  implicit submission on its own inputs, and there is a spec for it.
