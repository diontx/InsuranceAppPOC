using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PolicyFlow.Api.Middleware;

/// <summary>
/// Documents the cross-cutting request headers in OpenAPI.
/// </summary>
/// <remarks>
/// <c>Idempotency-Key</c> and <c>X-Correlation-Id</c> are part of the API contract
/// but are handled by middleware and attributes rather than appearing as normal
/// parameters, so without this filter they would be easy to miss in the generated
/// schema. An idempotency contract a client cannot discover is an idempotency
/// contract that will be ignored.
/// </remarks>
public sealed class HeaderDocumentationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= [];

        var isPaymentEndpoint = context.ApiDescription.RelativePath?
            .Contains("payments", StringComparison.OrdinalIgnoreCase) ?? false;

        // Already declared as a [FromHeader] parameter on the payment action, so
        // adding it again would produce a duplicate. Enrich rather than re-add.
        var existingIdempotencyKey = operation.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase));

        if (isPaymentEndpoint && existingIdempotencyKey is OpenApiParameter idempotencyKey)
        {
            idempotencyKey.Required = true;
            idempotencyKey.Description =
                "Required. A unique value per payment *intent*. Reuse the same key on every " +
                "retry of that intent: the API returns the original transaction instead of " +
                "creating a second one. A new key per HTTP attempt defeats the mechanism.";
            idempotencyKey.Example = JsonValue.Create("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        }

        if (operation.Parameters.Any(p =>
                string.Equals(p.Name, CorrelationIdMiddleware.HeaderName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = CorrelationIdMiddleware.HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description =
                "Optional trace id. Generated when omitted, and echoed in the response header. " +
                "Flows into SQL rows, the Service Bus message and worker logs, so one value " +
                "traces a quote end to end. Must be 64 characters or fewer of letters, digits, " +
                "'-', '_', '.' or ':'; anything else is replaced with a generated id.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 64 },
            Example = JsonValue.Create("9f1b7c42-0e52-4a6b-8f3d-1c0a4e6b2d91")
        });
    }
}
