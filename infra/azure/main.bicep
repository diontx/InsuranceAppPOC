// =============================================================================
// PolicyFlow — minimal Azure Container Apps environment.
//
// NOT DEPLOYED BY CI. Infrastructure deployment is an explicit, manual step
// behind an approval gate. See infra/azure/README.md for the cost warnings
// before running this.
//
// Deliberately small. It describes the API and worker as Container Apps and
// REFERENCES existing data stores rather than provisioning them, because
// creating a SQL Server and a Cosmos account is where an accidental Azure bill
// comes from.
// =============================================================================

targetScope = 'resourceGroup'

@description('Deployment environment name. Used in resource names and tags.')
@allowed(['dev', 'test'])
param environmentName string = 'dev'

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Container registry login server, e.g. myregistry.azurecr.io.')
param containerRegistry string

@description('''
Image tag to deploy. Pass the pipeline build id.
NEVER use 'latest' as the only tag: it makes rollback impossible and the running
version unidentifiable.
''')
param imageTag string

@description('SQL Server connection string. Pass as a secure pipeline variable; never commit it.')
@secure()
param sqlConnectionString string

@description('MongoDB / Cosmos for MongoDB connection string. Pass as a secure pipeline variable.')
@secure()
param mongoConnectionString string

@description('Azure Service Bus connection string. Pass as a secure pipeline variable.')
@secure()
param serviceBusConnectionString string

@description('Entra ID authority for token validation, e.g. https://login.microsoftonline.com/<tenant>/v2.0')
param jwtAuthority string

@description('API audience claim, e.g. api://<app-id>.')
param jwtAudience string

@description('''
Minimum replicas for the API.
Zero enables scale-to-zero, which keeps a dev environment inside the Container
Apps free grant. The trade-off is a cold start on the first request after an idle
period, which is acceptable for dev and not for production.
''')
@minValue(0)
@maxValue(5)
param apiMinReplicas int = 0

@minValue(0)
@maxValue(5)
param workerMinReplicas int = 0

var namePrefix = 'policyflow-${environmentName}'

var commonTags = {
  application: 'PolicyFlow'
  environment: environmentName
  managedBy: 'bicep'
  // Tagged so a stray resource group is identifiable and deletable. Untagged
  // resources are how a demo deployment turns into a recurring charge nobody
  // can attribute.
  purpose: 'proof-of-concept'
  costCenter: 'demo'
}

// -----------------------------------------------------------------------------
// Log Analytics
// -----------------------------------------------------------------------------
// Container Apps requires a log destination. This is the one resource here that
// always carries a cost, so retention is minimal and the SKU is pay-as-you-go
// with a daily cap.
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  tags: commonTags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    // 30 days is the minimum billable retention.
    retentionInDays: 30
    workspaceCapping: {
      // Hard ceiling on ingestion. A runaway log loop in a dev environment can
      // otherwise generate a surprising bill.
      dailyQuotaGb: json('0.5')
    }
  }
}

// -----------------------------------------------------------------------------
// Container Apps environment
// -----------------------------------------------------------------------------
resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  tags: commonTags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    // Consumption-only. A workload profile environment has a minimum hourly
    // charge whether anything is running or not.
    zoneRedundant: false
  }
}

// -----------------------------------------------------------------------------
// API
// -----------------------------------------------------------------------------
resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-api'
  location: location
  tags: commonTags

  // System-assigned managed identity. The eventual goal is to delete the
  // connection-string secrets below entirely and use Entra ID authentication to
  // SQL Server and Service Bus, so there is no credential to leak or rotate.
  identity: {
    type: 'SystemAssigned'
  }

  properties: {
    managedEnvironmentId: containerAppsEnvironment.id

    configuration: {
      ingress: {
        // Internet-facing. A production deployment should put Front Door or
        // Application Gateway in front for WAF and DDoS protection — see the
        // production gaps in docs/SECURITY.md.
        external: true
        targetPort: 8080
        // Platform-managed TLS. The application sets HSTS and redirects HTTP
        // outside Development.
        allowInsecure: false
        transport: 'auto'
      }

      // Secrets are environment-injected, never baked into the image.
      secrets: [
        {
          name: 'sql-connection-string'
          value: sqlConnectionString
        }
        {
          name: 'mongo-connection-string'
          value: mongoConnectionString
        }
        {
          name: 'servicebus-connection-string'
          value: serviceBusConnectionString
        }
      ]

      registries: [
        {
          server: containerRegistry
          // Managed identity rather than an admin username and password.
          identity: 'system'
        }
      ]
    }

    template: {
      containers: [
        {
          name: 'api'
          image: '${containerRegistry}/policyflow-api:${imageTag}'

          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }

          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              // NOT Development. That would enable Swagger, permit a symmetric
              // signing key and allow startup migrations. The startup guards in
              // SecurityExtensions throw if a dev-only setting is combined with
              // this value.
              value: 'Production'
            }
            {
              name: 'ASPNETCORE_HTTP_PORTS'
              value: '8080'
            }
            {
              name: 'SQL_CONNECTION_STRING'
              secretRef: 'sql-connection-string'
            }
            {
              name: 'MONGO_CONNECTION_STRING'
              secretRef: 'mongo-connection-string'
            }
            {
              name: 'SERVICEBUS_CONNECTION_STRING'
              secretRef: 'servicebus-connection-string'
            }
            {
              name: 'JWT_AUTHORITY'
              // Entra ID. With an authority set, tokens are validated against
              // its published JWKS using RS256 and keys rotate without a
              // redeploy.
              value: jwtAuthority
            }
            {
              name: 'JWT_AUDIENCE'
              value: jwtAudience
            }
            {
              name: 'REQUIRE_AUTHENTICATION'
              // Enforced here, unlike the local demo default.
              value: 'true'
            }
            {
              name: 'ENABLE_DEV_TOKEN_ENDPOINT'
              // Startup THROWS if this is true outside Development. Set
              // explicitly rather than relying on the default.
              value: 'false'
            }
            {
              name: 'APPLY_MIGRATIONS_ON_STARTUP'
              // Migrations belong in a controlled pipeline step with a rollback
              // plan, not in application startup.
              value: 'false'
            }
            {
              name: 'SEED_CARRIER_CATALOG'
              // Seeding overwrites operator edits to carrier pricing. Safe for a
              // demo, wrong once anyone is maintaining rates.
              value: 'false'
            }
          ]

          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              initialDelaySeconds: 15
              periodSeconds: 15
              failureThreshold: 3
            }
          ]
        }
      ]

      scale: {
        minReplicas: apiMinReplicas
        maxReplicas: 3
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

// -----------------------------------------------------------------------------
// Underwriting worker
// -----------------------------------------------------------------------------
resource workerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-worker'
  location: location
  tags: commonTags

  identity: {
    type: 'SystemAssigned'
  }

  properties: {
    managedEnvironmentId: containerAppsEnvironment.id

    configuration: {
      // No ingress. The worker serves no HTTP and must not be reachable.
      activeRevisionsMode: 'Single'

      secrets: [
        {
          name: 'sql-connection-string'
          value: sqlConnectionString
        }
        {
          name: 'mongo-connection-string'
          value: mongoConnectionString
        }
        {
          name: 'servicebus-connection-string'
          value: serviceBusConnectionString
        }
      ]

      registries: [
        {
          server: containerRegistry
          identity: 'system'
        }
      ]
    }

    template: {
      containers: [
        {
          name: 'worker'
          image: '${containerRegistry}/policyflow-worker:${imageTag}'

          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }

          env: [
            {
              name: 'DOTNET_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'SQL_CONNECTION_STRING'
              secretRef: 'sql-connection-string'
            }
            {
              name: 'MONGO_CONNECTION_STRING'
              secretRef: 'mongo-connection-string'
            }
            {
              name: 'SERVICEBUS_CONNECTION_STRING'
              secretRef: 'servicebus-connection-string'
            }
            {
              name: 'SERVICEBUS_QUEUE'
              value: 'quote-submitted'
            }
            // No JWT settings: the worker neither issues nor validates tokens.
          ]

          // No probes. The worker exposes no endpoint, and a process-liveness
          // check would be meaningless — the container is up whether or not the
          // Service Bus processor is actually consuming. Queue depth is the real
          // signal, and it is what the scale rule below uses.
        }
      ]

      scale: {
        minReplicas: workerMinReplicas
        maxReplicas: 5
        rules: [
          {
            name: 'queue-depth'
            // KEDA scaling on queue length: the correct signal for a consumer.
            // CPU-based scaling would be wrong here, because the worker is
            // mostly awaiting I/O and looks idle while doing real work.
            custom: {
              type: 'azure-servicebus'
              metadata: {
                queueName: 'quote-submitted'
                messageCount: '10'
              }
              auth: [
                {
                  secretRef: 'servicebus-connection-string'
                  triggerParameter: 'connection'
                }
              ]
            }
          }
        ]
      }
    }
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------
@description('Public HTTPS endpoint for the API.')
output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'

@description('API managed identity principal id. Grant it SQL and Service Bus data-plane roles.')
output apiPrincipalId string = apiApp.identity.principalId

@description('Worker managed identity principal id. Grant it Service Bus Data Receiver.')
output workerPrincipalId string = workerApp.identity.principalId

@description('Log Analytics workspace id, for querying structured logs.')
output logAnalyticsWorkspaceId string = logAnalytics.id
