using Application.Common;

namespace Application.Commands.CancelAuthorization;

public sealed record CancelAuthorizationCommand(
    string ProviderPaymentIntentId) : ICommand<Result>;