namespace Domain.Enums;


public enum LedgerAccount
{
    // Card hold placed on the customer, not yet taken (reserved, not real cash yet)
    CustomerAuthorized = 0,
    // Cash actually taken from the customer
    CustomerCaptured = 1,
    // Sales revenue we recognize as earned, net of tax
    MerchantRevenue = 2,
    // Tax collected from the customer that we owe to the tax authority, not our money
    TaxPayable = 3,
    // Money we owe back to a customer until a refund is paid out
    RefundsPayable = 4,
    // Fees charged by the payment provider (e.g. Stripe) for processing a payment
    GatewayFees = 5,
    // Gain or loss from converting between currencies for reporting
    FxGainLoss = 6,
    // Payment disputed and reversed by the card network (future, not yet posted anywhere)
    Chargebacks = 7,
    // Offsetting entry for an authorization hold, released once captured or voided
    AuthorizationHold = 8
}
