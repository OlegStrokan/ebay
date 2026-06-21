using Application.Gateways;

namespace Infrastructure.Gateways;

// @todo: Future implementations: JiraIncidentReporter, PagerDutyIncidentReporter, SlackIncidentReporter, SmsIncidentReporter
public class HelpDeskIncidentReporter(
    ILogger<HelpDeskIncidentReporter> logger) : IIncidentReporter
{
    public async Task SendAlertAsync(IncidentAlert alert, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[HelpDesk] Sending {Severity} alert: {AlertType} for Order {OrderId}. Message: {Message}",
            alert.Severity,
            alert.AlertType,
            alert.OrderId,
            alert.Message);


        await Task.CompletedTask;
    }

    public async Task CreateInterventionTicketAsync(InterventionTicket ticket, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[HelpDesk] Creating manual intervention ticket for Order {OrderId}. Issue: {Issue}. Suggested action: {SuggestedAction}",
            ticket.OrderId,
            ticket.Issue,
            ticket.SuggestedAction);
        
        await Task.CompletedTask;
    }
}
