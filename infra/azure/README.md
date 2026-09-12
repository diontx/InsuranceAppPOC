# Optional Azure Deployment

> ## Read this before deploying anything
>
> **The POC does not need Azure.** It runs entirely in local Docker. This
> directory exists to show how it *would* be deployed, and to keep that decision
> explicit rather than accidental.
>
> `main.bicep` is **not deployed by CI**. The deployment stage in
> `azure-pipelines.yml` is commented out and, if enabled, sits behind a manual
> approval gate.

---

## Cost

Azure Container Apps has a monthly free grant (180,000 vCPU-seconds and
360,000 GiB-seconds at the time of writing), and `main.bicep` sets
`minReplicas: 0` so a dev environment scales to zero when idle and stays inside
it.

**Ancillary resources still cost money.** These are the ones that catch people:

| Resource | Cost behaviour |
|---|---|
| **Log Analytics** | Per GB ingested. Always non-zero once anything logs. Capped at 0.5 GB/day in the template. |
| **Container Registry** | Basic is a flat monthly charge whether you push or not. |
| **Azure SQL Database** | The free offer covers one database; a second is billed. |
| **Cosmos DB for MongoDB** | The free tier is one account per subscription. Beyond that, provisioned throughput bills continuously. |
| **Service Bus** | Standard has a monthly base charge. Basic has no topics. |
| **Public IP / outbound data** | Small but non-zero. |

**Before deploying:**

1. Check current pricing — the figures above date quickly.
2. Set a **budget alert** on the subscription. This is the single most useful
   safeguard.
3. Deploy into a **dedicated resource group** so everything can be deleted in one
   action.

**For strict zero cost:** keep the runtime local and use Azure DevOps only for
CI/CD. That is the recommended path, and it still demonstrates the full pipeline.

---

## What the template does and does not create

**Creates:** a Log Analytics workspace, a Container Apps environment, and the API
and worker as Container Apps with system-assigned managed identities.

**Deliberately does not create:** SQL Server, MongoDB/Cosmos, or Service Bus.

That omission is the point. Provisioning data stores from a template is where an
accidental Azure bill comes from, and the free-tier rules for each differ. Create
them yourself, deliberately, and pass the connection strings in as secure
parameters.

---

## Deploying

### 1. Prerequisites

```bash
az login
az account set --subscription "<subscription-id>"

az group create --name policyflow-dev-rg --location eastus

# Container registry (Basic has a flat monthly charge)
az acr create --resource-group policyflow-dev-rg \
  --name policyflowdevacr --sku Basic

# Let Container Apps pull via managed identity rather than admin credentials
az acr update --name policyflowdevacr --admin-enabled false
```

Then create your data stores — Azure SQL (free offer), Cosmos DB for MongoDB
(free tier), and a Service Bus namespace with a `quote-submitted` queue.

### 2. Push images with a unique tag

```bash
TAG=$(git rev-parse --short HEAD)   # or the pipeline build id

az acr login --name policyflowdevacr

docker build -f src/PolicyFlow.Api/Dockerfile \
  -t policyflowdevacr.azurecr.io/policyflow-api:$TAG .
docker build -f src/PolicyFlow.Worker/Dockerfile \
  -t policyflowdevacr.azurecr.io/policyflow-worker:$TAG .

docker push policyflowdevacr.azurecr.io/policyflow-api:$TAG
docker push policyflowdevacr.azurecr.io/policyflow-worker:$TAG
```

**Never use `latest` as the only tag.** It makes rollback impossible and the
running version unidentifiable.

### 3. Deploy

```bash
az deployment group create \
  --resource-group policyflow-dev-rg \
  --template-file infra/azure/main.bicep \
  --parameters \
      environmentName=dev \
      containerRegistry=policyflowdevacr.azurecr.io \
      imageTag=$TAG \
      jwtAuthority="https://login.microsoftonline.com/<tenant-id>/v2.0" \
      jwtAudience="api://<app-id>" \
      sqlConnectionString="<from Key Vault or a secure prompt>" \
      mongoConnectionString="<...>" \
      serviceBusConnectionString="<...>"
```

Pass secrets from Key Vault or an interactive secure prompt. **Never put them on
a shell command line in a script that gets committed** — they land in shell
history and CI logs.

### 4. Grant the managed identities

```bash
API_PRINCIPAL=$(az deployment group show \
  --resource-group policyflow-dev-rg --name main \
  --query properties.outputs.apiPrincipalId.value -o tsv)

# Registry pull
az role assignment create --assignee $API_PRINCIPAL \
  --role AcrPull --scope $(az acr show --name policyflowdevacr --query id -o tsv)

# Service Bus (do the same for the worker with Data Receiver)
az role assignment create --assignee $API_PRINCIPAL \
  --role "Azure Service Bus Data Sender" \
  --scope "<service-bus-namespace-resource-id>"
```

### 5. Run migrations as a separate step

`APPLY_MIGRATIONS_ON_STARTUP` is **false** in the template. Startup migrations
mean an application instance can silently alter its own schema, and two instances
starting together can race.

```bash
dotnet ef database update \
  --project src/PolicyFlow.Infrastructure \
  --startup-project src/PolicyFlow.Infrastructure \
  --connection "<azure-sql-connection-string>"
```

Or generate an idempotent script for a DBA to review:

```bash
dotnet ef migrations script --idempotent \
  --project src/PolicyFlow.Infrastructure \
  --startup-project src/PolicyFlow.Infrastructure \
  --output migrate.sql
```

**MongoDB needs no migration** — collections are created on first write and
indexes are created at startup by `MongoIndexInitializer`.

### 6. Verify

```bash
API_URL=$(az deployment group show \
  --resource-group policyflow-dev-rg --name main \
  --query properties.outputs.apiUrl.value -o tsv)

curl $API_URL/health/ready
```

All three checks should be `Healthy`. Note that `/swagger` is **not** available —
`ASPNETCORE_ENVIRONMENT=Production` in the template, and the API does not expose
its schema outside Development.

---

## Configuration differences from local

The deployed configuration is meaningfully stricter, and the startup guards
enforce several of these rather than trusting the template:

| Setting | Local | Azure | Why |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | Disables Swagger; enables HSTS and HTTPS redirection |
| `JWT_SIGNING_KEY` | local HS256 key | **absent** | Startup *throws* if a symmetric key is set outside Development — the API could otherwise mint the tokens it accepts |
| `JWT_AUTHORITY` | absent | Entra ID | Switches to RS256 against published JWKS; keys rotate without redeploy |
| `REQUIRE_AUTHENTICATION` | `false` | `true` | Policies are enforced |
| `ENABLE_DEV_TOKEN_ENDPOINT` | `true` | `false` | Startup *throws* if true outside Development |
| `APPLY_MIGRATIONS_ON_STARTUP` | `true` | `false` | Schema changes belong in a reviewed pipeline step |
| `SEED_CARRIER_CATALOG` | `true` | `false` | Seeding overwrites operator rate edits |

---

## Tearing it down

```bash
az group delete --name policyflow-dev-rg --yes --no-wait
```

Everything in the template is inside that resource group, which is the reason to
deploy into a dedicated one. Data stores you created separately are **not**
deleted by this — check for them explicitly:

```bash
az resource list --query "[?tags.application=='PolicyFlow'].{name:name, type:type, group:resourceGroup}" -o table
```

Every resource in the template carries `application: PolicyFlow` and
`purpose: proof-of-concept` tags so a stray one is identifiable. Untagged
resources are how a demo deployment becomes a recurring charge nobody can
attribute.

---

## Enabling the pipeline deployment stage

The `DeployDev` stage in `azure-pipelines.yml` is commented out. To enable it:

1. Create an Azure Resource Manager **service connection** in Azure DevOps.
2. Create an **Environment** named `policyflow-dev`.
3. Add an **approval check** on that environment. This is the important step —
   it is what makes deployment a decision rather than a side effect of merging.
4. Store connection strings as **secret** pipeline variables, or reference a Key
   Vault-linked variable group.
5. Uncomment the stage and set the service connection name.

The stage is already conditioned on `main` only, and passes `$(Build.BuildId)` as
the image tag.
