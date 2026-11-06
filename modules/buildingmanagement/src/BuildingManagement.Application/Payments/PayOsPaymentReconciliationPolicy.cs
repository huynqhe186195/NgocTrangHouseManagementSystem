using System;

namespace BuildingManagement.Payments;

/// <summary>
/// Never posts a receipt. Evaluates a provider lookup against a persisted intent
/// while the worker holds its own DB transaction and building authorization.
/// A PAID provider response alone cannot bypass the durable webhook inbox,
/// uniqueness, review or financial journal requirements.
/// </summary>
public static class PayOsPaymentReconciliationPolicy
{
    public static PayOsReconciliationDisposition Classify(
        PayOsPaymentIntent intent,
        PayOsPaymentLinkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.OrderCode != intent.OrderCode ||
            snapshot.AmountVnd != intent.AmountVnd ||
            string.IsNullOrWhiteSpace(intent.PaymentLinkId) ||
            !string.Equals(snapshot.PaymentLinkId, intent.PaymentLinkId, StringComparison.Ordinal) ||
            snapshot.AmountPaidVnd < 0 ||
            snapshot.AmountRemainingVnd < 0 ||
            snapshot.AmountPaidVnd > intent.AmountVnd ||
            snapshot.AmountRemainingVnd > intent.AmountVnd ||
            snapshot.AmountPaidVnd + snapshot.AmountRemainingVnd != intent.AmountVnd)
            return PayOsReconciliationDisposition.ConflictRequiresReview;

        if (intent.Status == PayOsPaymentIntentStatus.Posted)
            return snapshot.Status == "PAID" &&
                snapshot.AmountPaidVnd == intent.AmountVnd &&
                snapshot.AmountRemainingVnd == 0
                ? PayOsReconciliationDisposition.AlreadyPosted
                : PayOsReconciliationDisposition.ConflictRequiresReview;

        if (intent.Status == PayOsPaymentIntentStatus.Cancelled)
            return snapshot.AmountPaidVnd > 0 || snapshot.Status == "PAID"
                ? PayOsReconciliationDisposition.LateFundsRequireReview
                : PayOsReconciliationDisposition.NoFundsConfirmed;

        if (snapshot.Status == "PAID" &&
            snapshot.AmountPaidVnd == intent.AmountVnd &&
            snapshot.AmountRemainingVnd == 0)
            return PayOsReconciliationDisposition.FundsDetectedPendingIndependentPosting;

        if (snapshot.Status == "CANCELLED" && snapshot.AmountPaidVnd == 0)
            return PayOsReconciliationDisposition.NoFundsConfirmed;

        if (snapshot.Status is "PENDING" or "PROCESSING" &&
            snapshot.AmountPaidVnd == 0)
            return PayOsReconciliationDisposition.AwaitingPayment;

        return PayOsReconciliationDisposition.ConflictRequiresReview;
    }
}

public enum PayOsReconciliationDisposition
{
    AwaitingPayment = 0,
    NoFundsConfirmed = 1,
    FundsDetectedPendingIndependentPosting = 2,
    AlreadyPosted = 3,
    LateFundsRequireReview = 4,
    ConflictRequiresReview = 5
}
