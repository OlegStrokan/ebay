using Application.Common.Enums;

namespace Application.Gateways;

public sealed record LedgerIncident(
    string AlertType,
    AlertSeverity Severity,
    string Summary,
    IReadOnlyList<string> Details);

public interface IIncidentReporter
{
    Task ReportAsync(LedgerIncident incident, CancellationToken cancellationToken = default);
}
