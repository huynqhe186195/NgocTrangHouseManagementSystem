using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.Utilities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Last non-executing 2H financial/physical dossier cross-check.
/// The input objects must ALL be loaded from original scoped DB records by
/// an authenticated adapter with stable locks and full history.
/// No client-supplied "verified" boolean is accepted. This is NOT a
/// substitute for real posted adjustment, refund ledger or commit gate.
/// </summary>
public static class RoomChangeMidMonthDossierPreflight
{
    public static RoomChangeMidMonthDossierReadiness Assess(
        RoomChangeRequest? request,
        Contract? source,
        RoomChangeTenantRosterSnapshotRecord? roster,
        RoomChangeBusinessPolicyRecord? policy,
        RoomChangeBusinessPolicyReviewRecord? policyReview,
        RoomChangeMidMonthFinancialReconciliationPacket? packet,
        RoomChangeDayPricingRuleCandidate? dayRule,
        RoomChangeDayPricingOwnerSignoff? pricingOwnerSignoff,
        RoomChangeHandoverEvidenceRecord? physical,
        UtilityMeter? originalElectricityMeter,
        MeterReading? originalElectricityReading,
        UtilityMeter? originalWaterMeter,
        MeterReading? originalWaterReading,
        RoomChangeHandoverThreePartySignoff? threePartySignoff,
        RoomChangeTransferDepositCashReview? cashReview,
        BuildingManagement.Payments.Payment? originalTargetTopUpReceipt)
    {
        var blockers = new List<string>();
        if (request is null || source is null || roster is null ||
            policy is null || policyReview is null || packet is null)
            return Block("DossierOriginalScopedSourcesMissing");

        try
        {
            packet.VerifyAgainst(request, source, roster, policy, policyReview);
        }
        catch (Exception e) when (e is InvalidOperationException or
            InvalidDataException or ArgumentException)
        {
            return Block("DossierFinancialPacketOrPolicyDrift");
        }

        if (dayRule is null || pricingOwnerSignoff is null)
            blockers.Add("D01D02OwnerSignedPricingRuleMissing");
        else
        {
            try
            {
                dayRule.Verify(request, source, roster, policy, policyReview);
                pricingOwnerSignoff.Verify(
                    dayRule, request, source, roster, policy, policyReview);
                if (packet.Quote.TransferDate != dayRule.TransferDate ||
                    packet.Quote.SourceFullMonthlyRentVnd !=
                        dayRule.SourceMonthlyRentVnd ||
                    packet.Quote.TargetFullMonthlyRentVnd !=
                        dayRule.TargetMonthlyRentVnd ||
                    packet.Quote.ProposedSourceRentVnd !=
                        dayRule.ProposedSourceRentVnd ||
                    packet.Quote.ProposedTargetRentVnd !=
                        dayRule.ProposedTargetRentVnd ||
                    packet.Quote.TransferDayOwner != dayRule.TransferDayOwner ||
                    packet.Quote.Rounding != dayRule.Rounding ||
                    packet.Quote.SignedRuleReference !=
                        dayRule.SignedPricingRuleReference)
                    blockers.Add("D01D02ApprovedQuoteDoesNotMatchFinancialPacket");
            }
            catch (Exception e) when (e is InvalidOperationException or
                InvalidDataException or ArgumentException)
            {
                blockers.Add("D01D02SourcePricingDecisionDrift");
            }
        }

        if (physical is null || threePartySignoff is null ||
            originalElectricityMeter is null ||
            originalElectricityReading is null ||
            originalWaterMeter is null ||
            originalWaterReading is null)
            blockers.Add("D06OriginalMeterAndThreePartySignatureMissing");
        else
        {
            try
            {
                physical.Verify(request, originalElectricityMeter,
                    originalElectricityReading, originalWaterMeter,
                    originalWaterReading);
                threePartySignoff.Verify(request, roster, physical);
            }
            catch (Exception e) when (e is InvalidOperationException or
                InvalidDataException or ArgumentException)
            {
                blockers.Add("D06OriginalPhysicalOrSignatoryDrift");
            }
        }

        var deposit = packet.Deposit;
        if (deposit.UnpaidTopUpRequirementVnd > 0m ||
            deposit.UnpaidRefundCandidateVnd > 0m)
        {
            if (cashReview is null)
                blockers.Add("D04MissingIndependentCashMovementEvidence");
            else
            {
                try
                {
                    cashReview.Verify(packet, originalTargetTopUpReceipt);
                    if ((deposit.UnpaidTopUpRequirementVnd > 0m &&
                         cashReview.Leg != RoomChangeDepositCashLeg.VerifiedTopUp) ||
                        (deposit.UnpaidRefundCandidateVnd > 0m &&
                         cashReview.Leg != RoomChangeDepositCashLeg.ManuallyReviewedRefund))
                        blockers.Add("D04CashEvidenceWrongDirection");
                }
                catch (Exception e) when (e is InvalidDataException or
                    InvalidOperationException or ArgumentException)
                {
                    blockers.Add("D04CashEvidenceDoesNotMatchApprovedDepositSplit");
                }
            }
        }
        else if (cashReview is not null)
            blockers.Add("D04SpuriousCashMovementOnFullCarry");

        if (packet.Rent.RequiresAppendOnlyIssuedInvoiceCorrection)
            blockers.Add("D03OfficialPerLineAdjustmentPostingNotYetProven");
        if (packet.Rent.RequiresSeparateOvercollectionReview)
            blockers.Add("D03OvercollectionNeedsIndependentSettlement");

        // Never expose an execution authorization. Even an otherwise
        // consistent set of signed REFERENCES still needs independently
        // verified exact bank ledger, official invoice postings, unique
        // deposit disposition, identity leases and SQL commit-time proof.
        var nonOperational = blockers.Where(x =>
            x != "D03OfficialPerLineAdjustmentPostingNotYetProven" &&
            x != "D03OvercollectionNeedsIndependentSettlement").ToArray();
        return new RoomChangeMidMonthDossierReadiness(
            nonOperational.Length == 0,
            blockers.Distinct(StringComparer.Ordinal).ToArray(),
            IsBankGatewayVerified: false, CanExecuteTransfer: false,
            IsOfficialInvoiceCorrectionPosted: false,
            IsDepositCashJournalPosted: false);
    }

    private static RoomChangeMidMonthDossierReadiness Block(string code)
        => new(false, new[] { code }, false, false, false, false);
}

public sealed record RoomChangeMidMonthDossierReadiness(
    bool AllPresentedDocumentarySourcesMatch,
    IReadOnlyList<string> Blockers,
    bool IsBankGatewayVerified,
    bool CanExecuteTransfer,
    bool IsOfficialInvoiceCorrectionPosted,
    bool IsDepositCashJournalPosted);
