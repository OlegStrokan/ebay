using Domain.Enums;
using Domain.Exceptions;

namespace Domain.Services;

public static class PaymentStateMachine
{
    private static readonly HashSet<(PaymentStatus From, PaymentStatus To)> AllowedTransitions =
    [
        (PaymentStatus.Created, PaymentStatus.PendingProviderConfirmation),
        (PaymentStatus.Created, PaymentStatus.Succeeded),
        (PaymentStatus.Created, PaymentStatus.Failed),
        (PaymentStatus.PendingProviderConfirmation, PaymentStatus.Succeeded),
        (PaymentStatus.PendingProviderConfirmation, PaymentStatus.Failed),
        (PaymentStatus.Succeeded, PaymentStatus.RefundPending),
        (PaymentStatus.RefundPending, PaymentStatus.Succeeded),  // partial refund completed — still refundable
        (PaymentStatus.RefundPending, PaymentStatus.Refunded),
        (PaymentStatus.RefundPending, PaymentStatus.RefundFailed),
        (PaymentStatus.RefundFailed, PaymentStatus.RefundPending),
    ];

    public static bool CanTransition(PaymentStatus from, PaymentStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return AllowedTransitions.Contains((from, to));
    }

    public static void EnsureCanTransition(PaymentStatus from, PaymentStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidPaymentStateTransitionException(from, to);
        }
    }
}