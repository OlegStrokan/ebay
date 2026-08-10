using OpsConsole.Redaction;
using Protos.AdminOps;

namespace OpsConsole.Endpoints;

public static class SagaEndpoints
{
    public static void MapSagaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sagas").WithTags("Sagas").RequireAuthorization("OpsViewer");

        group.MapGet("", async (
            string? status,
            string? sagaType,
            string? search,
            int? skip,
            int? take,
            AdminOpsService.AdminOpsServiceClient client) =>
        {
            var response = await client.GetSagasAsync(new GetSagasRequest
            {
                Status = status ?? string.Empty,
                SagaType = sagaType ?? string.Empty,
                Search = search ?? string.Empty,
                Skip = skip ?? 0,
                Take = take ?? 50
            });
            return Results.Ok(response);
        });

        group.MapGet("/{id}", async (string id, AdminOpsService.AdminOpsServiceClient client) =>
        {
            var response = await client.GetSagaAsync(new GetSagaRequest { SagaId = id });
            return response.Found ? Results.Ok(response) : Results.NotFound();
        });

        group.MapGet("/{id}/events", async (string id, AdminOpsService.AdminOpsServiceClient client) =>
        {
            var response = await client.GetSagaEventsAsync(new GetSagaEventsRequest { SagaId = id });

            foreach (var step in response.Steps)
            {
                step.Request = PiiRedactor.RedactJson(step.Request);
                step.Response = PiiRedactor.RedactJson(step.Response);
            }

            return Results.Ok(response);
        });
    }
}
