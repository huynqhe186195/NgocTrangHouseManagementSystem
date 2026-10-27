using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class FinalMoveOutBillReview_Tests
{
    [Fact]
    public void FINALBILL01_Approved_Quote_Combines_Full_Rent_And_Both_Draft_Utilities()
    {
        var (request, contract, draft) = Fixture();
        var review = Review(request, contract, draft);

        Assert.Equal(3_500_000m, review.FullMonthRent);
        Assert.Equal(30_000m, review.ElectricityAmount);
        Assert.Equal(20_000m, review.WaterAmount);
        Assert.Equal(3_550_000m, review.ProvisionalRentAndUtilities);
        Assert.Equal(draft.Id, review.UtilityDraftId);
        Assert.Contains("FinalInvoiceNotIssued", review.Blockers);
        Assert.Contains("OutstandingDebtNotReconciled", review.Blockers);
        Assert.Contains("DepositSettlementNotVerified", review.Blockers);
        Assert.False(review.CanIssue);
        Assert.False(review.IsReceivablePosted);
    }

    [Fact]
    public void FINALBILL02_Missing_Or_Ambiguous_Draft_Does_Not_Invent_Utility_Money()
    {
        var (request, contract, draft) = Fixture();
        var missing = FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, Array.Empty<Invoice>(),
            Array.Empty<InvoiceItem>(), Array.Empty<InvoiceItemMeterSource>());
        Assert.Null(missing.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftMissing", missing.Blockers);

        var duplicate = FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { draft, draft },
            draft.Items.ToArray(), draft.Items.SelectMany(x => x.MeterSources).ToArray());
        Assert.Null(duplicate.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftAmbiguous", duplicate.Blockers);
    }

    [Fact]
    public void FINALBILL03_Absent_Meter_Source_Refuses_Money_Quote()
    {
        var (request, contract, draft) = Fixture();
        var result = FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { draft }, draft.Items.ToArray(),
            Array.Empty<InvoiceItemMeterSource>());
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftEvidenceInvalid", result.Blockers);
    }

    [Fact]
    public void FINALBILL04_Pending_Request_Does_Not_Quote()
    {
        var (request, contract, draft) = Fixture(approve: false);
        var result = Review(request, contract, draft);
        Assert.Null(result.FullMonthRent);
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("MoveOutNotApproved", result.Blockers);
    }

    [Fact]
    public void FINALBILL05_Another_Contract_Cannot_Borrow_Utility_Draft()
    {
        var (request, contract, _) = Fixture();
        var foreign = BuildDraft(Guid.NewGuid(), request.RoomId);
        var result = Review(request, contract, foreign);
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftScopeMismatch", result.Blockers);
    }

    [Fact]
    public void FINALBILL06_Old_Month_Draft_Cannot_Be_Used_For_Final_Month()
    {
        var (request, contract, _) = Fixture();
        var stale = BuildDraft(contract.Id, request.RoomId, month: 10);
        var result = Review(request, contract, stale);
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftScopeMismatch", result.Blockers);
    }

    [Fact]
    public void FINALPACKAGE01_Full_Rent_And_Immutable_Meter_Snapshot_Have_Checksum()
    {
        var (request, contract, draft) = Fixture();
        var captureTime = new DateTime(2026, 12, 1, 1, 0, 0, DateTimeKind.Utc);
        var package = Package(request, contract, draft, captureTime);

        Assert.Equal(3_500_000m, package.FullMonthRent);
        Assert.Equal(3_550_000m, package.KnownRentAndUtilities);
        Assert.Equal(draft.Id, package.DraftInvoiceId);
        Assert.Equal(new DateOnly(2026, 11, 30), package.FinalBillingDate);
        Assert.Equal(2, package.Utilities.Count);
        Assert.Equal(64, package.EvidenceSha256.Length);
        Assert.True(package.VerifyChecksum());
        Assert.False(package.CanIssue);
        Assert.False(package.IsReceivablePosted);
        Assert.Equal(30_000m, package.Utilities.Single(x =>
            x.UtilityType == UtilityType.Electricity).Amount);
        Assert.Equal(20_000m, package.Utilities.Single(x =>
            x.UtilityType == UtilityType.Water).Amount);
    }

    [Fact]
    public void FINALPACKAGE02_Capture_Is_Deterministic_For_Same_Source_Graph()
    {
        var (request, contract, draft) = Fixture();
        var capturedAt = new DateTime(2026, 11, 30, 23, 0, 0, DateTimeKind.Utc);
        var first = Package(request, contract, draft, capturedAt);
        var second = Package(request, contract, draft, capturedAt);

        Assert.Equal(first.EvidenceJson, second.EvidenceJson);
        Assert.Equal(first.EvidenceSha256, second.EvidenceSha256);
        Assert.Equal(first.Utilities[0].Sources[0].SourceId,
            second.Utilities[0].Sources[0].SourceId);
        Assert.Throws<NotSupportedException>(() =>
            ((System.Collections.Generic.IList<FinalInvoiceUtilityEvidence>)
                first.Utilities).Clear());
    }

    [Fact]
    public void FINALPACKAGE03_No_Snapshot_From_Pending_Request()
    {
        var (request, contract, draft) = Fixture(approve: false);
        Assert.Throws<InvalidOperationException>(() =>
            Package(request, contract, draft, DateTime.UtcNow));
    }

    [Fact]
    public void FINALPACKAGE04_Rejects_Stale_And_Cross_Contract_Draft()
    {
        var (request, contract, _) = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            Package(request, contract,
                BuildDraft(contract.Id, request.RoomId, month: 10),
                DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() =>
            Package(request, contract,
                BuildDraft(Guid.NewGuid(), request.RoomId),
                DateTime.UtcNow));
    }

    [Fact]
    public void FINALPACKAGE05_Requires_Utc_Capture_And_All_Meter_Evidence()
    {
        var (request, contract, draft) = Fixture();
        Assert.Throws<ArgumentException>(() =>
            Package(request, contract, draft,
                new DateTime(2026, 11, 30, 15, 0, 0, DateTimeKind.Local)));
        Assert.Throws<InvalidOperationException>(() =>
            FinalInvoiceEvidencePackage.Capture(
                request, contract, draft, draft.Items.ToArray(),
                Array.Empty<InvoiceItemMeterSource>(), DateTime.UtcNow));
    }

    private static FinalInvoiceEvidencePackage Package(
        MoveOutRequest request, Contract contract, Invoice draft,
        DateTime capturedAtUtc)
        => FinalInvoiceEvidencePackage.Capture(
            request, contract, draft, draft.Items.ToArray(),
            draft.Items.SelectMany(x => x.MeterSources).ToArray(),
            capturedAtUtc);

    [Fact]
    public void ISSUEGATE01_Three_Known_Line_Amounts_Do_Not_Issue_Draft()
    {
        var (request, contract, draft) = Fixture();
        var review = Review(request, contract, draft);
        var package = Package(request, contract, draft, DateTime.UtcNow);
        var receipts = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, Array.Empty<Payment>());

        var result = FinalInvoiceIssuePreflightEvaluator.Assess(
            review, package, receipts);

        Assert.Equal(draft.Id, result.UtilityDraftId);
        Assert.Equal(3_550_000m, result.KnownBaseChargeTotal);
        Assert.Equal(package.EvidenceSha256, result.EvidenceSha256);
        Assert.Equal(3, result.BaseCharges.Count);
        Assert.Equal(3_500_000m, result.BaseCharges.Single(x =>
            x.Type == FinalInvoiceBaseChargeType.FullMonthRent).Amount);
        Assert.Equal(30_000m, result.BaseCharges.Single(x =>
            x.Type == FinalInvoiceBaseChargeType.Electricity).Amount);
        Assert.Equal(20_000m, result.BaseCharges.Single(x =>
            x.Type == FinalInvoiceBaseChargeType.Water).Amount);
        Assert.Contains("AuxiliaryFeePolicyUndecided", result.Blockers);
        Assert.Contains("InvoiceDueDatePolicyUndecided", result.Blockers);
        Assert.Contains("IssuerBuildingAuthorizationUnavailable", result.Blockers);
        Assert.Contains("ImmutableIssuedInvoiceLedgerNotImplemented", result.Blockers);
        Assert.Contains("AuditedPaymentAllocationNotImplemented", result.Blockers);
        Assert.False(result.CanIssue);
        Assert.False(result.IsReceivablePosted);
        Assert.False(result.IsDebtCalculated);
    }

    [Fact]
    public void ISSUEGATE02_Missing_Evidence_Does_Not_Create_Charge_Lines()
    {
        var (request, contract, draft) = Fixture();
        var review = Review(request, contract, draft);
        var receipts = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, Array.Empty<Payment>());

        var result = FinalInvoiceIssuePreflightEvaluator.Assess(
            review, null, receipts);

        Assert.Null(result.KnownBaseChargeTotal);
        Assert.Null(result.EvidenceSha256);
        Assert.Empty(result.BaseCharges);
        Assert.Contains("VerifiedFinalInvoiceEvidenceMissing", result.Blockers);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEGATE03_Other_Contract_Evidence_Is_Rejected()
    {
        var (request, contract, draft) = Fixture();
        var review = Review(request, contract, draft);
        var receipts = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, Array.Empty<Payment>());
        var (foreignRequest, foreignContract, foreignDraft) = Fixture();
        var foreign = Package(
            foreignRequest, foreignContract, foreignDraft, DateTime.UtcNow);

        var result = FinalInvoiceIssuePreflightEvaluator.Assess(
            review, foreign, receipts);

        Assert.Contains("VerifiedEvidenceMismatch", result.Blockers);
        Assert.Null(result.KnownBaseChargeTotal);
        Assert.Empty(result.BaseCharges);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEGATE04_Completed_Receipt_On_Unissued_Draft_Is_Anomaly()
    {
        var (request, contract, draft) = Fixture();
        var review = Review(request, contract, draft);
        var package = Package(request, contract, draft, DateTime.UtcNow);
        var receipt = new Payment(
            Guid.NewGuid(), "ISSUEGATE-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), PaymentPurpose.InvoicePayment,
            PaymentMethod.Cash, PaymentChannel.Manual, 100000m,
            contractId: contract.Id, invoiceId: draft.Id);
        receipt.Complete(DateTime.UtcNow);
        var payments = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { receipt });

        var result = FinalInvoiceIssuePreflightEvaluator.Assess(
            review, package, payments);

        Assert.Contains(
            "Payment:CompletedPaymentLinkedToUnissuedDraft", result.Blockers);
        Assert.Equal(3_550_000m, result.KnownBaseChargeTotal);
        Assert.False(result.IsDebtCalculated);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEGATE05_Pending_MoveOut_Never_Presents_Issuable_Line_Items()
    {
        var (request, contract, draft) = Fixture(approve: false);
        var review = Review(request, contract, draft);
        var payments = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, Array.Empty<Payment>());

        var result = FinalInvoiceIssuePreflightEvaluator.Assess(
            review, null, payments);

        Assert.Contains("Bill:MoveOutNotApproved", result.Blockers);
        Assert.Null(result.KnownBaseChargeTotal);
        Assert.Empty(result.BaseCharges);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEAUTH01_Matching_Scopes_Still_Cannot_Issue_Without_Financial_Policy()
    {
        var (preflight, facts) = IssueFacts();

        var result = InvoiceIssuanceScopePolicy.Assess(preflight, facts);

        Assert.True(result.IsScopeConsistent);
        Assert.Contains("FinancialIssuancePrerequisitesIncomplete", result.Blockers);
        Assert.False(result.CanIssue);
        Assert.False(result.CanPostReceivable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ISSUEAUTH02_No_Authenticated_Or_Entitled_Issuer_Is_Denied(bool missingId)
    {
        var (preflight, facts) = IssueFacts();
        var invalid = missingId
            ? facts with { AuthenticatedIssuerId = null }
            : facts with { IssuerBuildingMemberships = Array.Empty<Guid>() };

        var result = InvoiceIssuanceScopePolicy.Assess(preflight, invalid);

        Assert.Contains("IssuerIdentityOrBuildingPermissionDenied", result.Blockers);
        Assert.False(result.IsScopeConsistent);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEAUTH03_Permission_Denied_Cannot_Be_Circumvented_By_Membership()
    {
        var (preflight, facts) = IssueFacts();

        var result = InvoiceIssuanceScopePolicy.Assess(
            preflight, facts with { IssuerPermissionGranted = false });

        Assert.Contains("IssuerIdentityOrBuildingPermissionDenied", result.Blockers);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEAUTH04_Cross_Contract_Room_Or_Floor_Is_Rejected()
    {
        var (preflight, facts) = IssueFacts();
        var crossed = facts with
        {
            ContractId = Guid.NewGuid(),
            RoomFloorId = Guid.NewGuid()
        };

        var result = InvoiceIssuanceScopePolicy.Assess(preflight, crossed);

        Assert.Contains("IssuerResourceRelationshipMismatch", result.Blockers);
        Assert.False(result.IsScopeConsistent);
    }

    [Fact]
    public void ISSUEAUTH05_Another_Draft_Or_MoveOut_Cannot_Borrow_Authority()
    {
        var (preflight, facts) = IssueFacts();
        var reused = facts with
        {
            RequestedDraftInvoiceId = Guid.NewGuid(),
            RequestedMoveOutId = Guid.NewGuid()
        };

        var result = InvoiceIssuanceScopePolicy.Assess(preflight, reused);

        Assert.Contains("IssuerResourceRelationshipMismatch", result.Blockers);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEAUTH06_Unverified_Evidence_Digest_Never_Qualifies()
    {
        var (preflight, facts) = IssueFacts();

        var result = InvoiceIssuanceScopePolicy.Assess(
            preflight, facts with { PersistedEvidenceSha256 = new string('0', 64) });

        Assert.Contains("PersistedInvoiceEvidenceMismatch", result.Blockers);
        Assert.False(result.IsScopeConsistent);
    }

    [Fact]
    public void ISSUEAUTH07_Duplicate_Invoice_For_Same_Period_Is_Blocked()
    {
        var (preflight, facts) = IssueFacts();

        var result = InvoiceIssuanceScopePolicy.Assess(
            preflight, facts with { ExistingIssuedInvoiceForPeriod = true });

        Assert.Contains("InvoiceAlreadyIssuedForBillingPeriod", result.Blockers);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEAUTH08_NotDraft_Status_Is_Rejected_Without_Changing_Invoice()
    {
        var (preflight, facts) = IssueFacts();

        var result = InvoiceIssuanceScopePolicy.Assess(
            preflight, facts with { PersistedInvoiceStatus = (InvoiceStatus)1 });

        Assert.Contains("PersistedInvoiceNotDraft", result.Blockers);
        Assert.False(result.IsScopeConsistent);
    }

    [Fact]
    public void ISSUEAUTH09_Null_Preflight_Or_Facts_Are_Denied()
    {
        var (preflight, facts) = IssueFacts();

        Assert.Contains("IssuerScopeFactsUnavailable",
            InvoiceIssuanceScopePolicy.Assess(null, facts).Blockers);
        Assert.Contains("IssuerScopeFactsUnavailable",
            InvoiceIssuanceScopePolicy.Assess(preflight, null).Blockers);
    }

    private static (FinalInvoiceIssuePreflight, InvoiceIssuanceScopeFacts)
        IssueFacts()
    {
        var (request, contract, draft) = Fixture();
        var bill = Review(request, contract, draft);
        var package = Package(request, contract, draft,
            new DateTime(2026, 11, 30, 20, 0, 0, DateTimeKind.Utc));
        var payments = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            bill, Array.Empty<Payment>());
        var preflight = FinalInvoiceIssuePreflightEvaluator.Assess(
            bill, package, payments);
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var facts = new InvoiceIssuanceScopeFacts(
            AuthenticatedIssuerId: Guid.NewGuid(),
            IssuerPermissionGranted: true,
            RequestedMoveOutId: request.Id,
            PersistedMoveOutId: request.Id,
            RequestedDraftInvoiceId: draft.Id,
            PersistedDraftInvoiceId: draft.Id,
            InvoiceContractId: contract.Id,
            InvoiceRoomId: contract.RoomId,
            ContractId: contract.Id,
            ContractRoomId: contract.RoomId,
            RoomId: contract.RoomId,
            RoomFloorId: floorId,
            FloorId: floorId,
            FloorBuildingId: buildingId,
            BuildingId: buildingId,
            IssuerBuildingMemberships: new[] { buildingId },
            PersistedInvoiceStatus: draft.Status,
            PersistedEvidenceSha256: package.EvidenceSha256,
            ExistingIssuedInvoiceForPeriod: false);
        return (preflight, facts);
    }

    [Fact]
    public void ISSUEARCH01_Stage_Verified_Evidence_Without_Posting_Debt()
    {
        var (evidence, preflight) = StagingFixture();
        var preparedBy = Guid.NewGuid();
        var staged = FinalInvoiceIssueEvidenceRecord.Prepare(
            Guid.NewGuid(), preparedBy,
            new DateTime(2026, 12, 1, 2, 0, 0, DateTimeKind.Utc),
            evidence, preflight);

        staged.VerifyStoredEvidence();
        Assert.Equal(evidence.DraftInvoiceId, staged.DraftInvoiceId);
        Assert.Equal(evidence.EvidenceSha256, staged.EvidenceSha256);
        Assert.Equal(evidence.KnownRentAndUtilities, staged.KnownBaseChargeTotal);
        Assert.Equal(preparedBy, staged.PreparedByUserId);
        Assert.Equal(2026, staged.BillingYear);
        Assert.Equal(11, staged.BillingMonth);
        Assert.False(staged.IsInvoiceIssued);
        Assert.False(staged.IsReceivablePosted);
    }

    [Fact]
    public void ISSUEARCH02_Foreign_Preflight_Cannot_Archive_Other_Draft()
    {
        var (evidence, _) = StagingFixture();
        var (_, foreignPreflight) = StagingFixture();
        Assert.Throws<InvalidOperationException>(() =>
            FinalInvoiceIssueEvidenceRecord.Prepare(
                Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
                evidence, foreignPreflight));
    }

    [Fact]
    public void ISSUEARCH03_Missing_Actor_Or_UTC_Time_Is_Rejected()
    {
        var (evidence, preflight) = StagingFixture();
        Assert.Throws<ArgumentException>(() =>
            FinalInvoiceIssueEvidenceRecord.Prepare(
                Guid.NewGuid(), Guid.Empty, DateTime.UtcNow,
                evidence, preflight));
        Assert.Throws<ArgumentException>(() =>
            FinalInvoiceIssueEvidenceRecord.Prepare(
                Guid.NewGuid(), Guid.NewGuid(), DateTime.SpecifyKind(
                    new DateTime(2026, 11, 30), DateTimeKind.Unspecified),
                evidence, preflight));
    }

    [Fact]
    public void ISSUEARCH05_Different_Capture_Times_Require_A_Matching_Preflight()
    {
        var (request, contract, draft) = Fixture();
        var firstSnapshot = Package(request, contract, draft,
            new DateTime(2026, 11, 30, 18, 0, 0, DateTimeKind.Utc));
        var secondSnapshot = Package(request, contract, draft,
            new DateTime(2026, 11, 30, 18, 0, 1, DateTimeKind.Utc));
        var bill = Review(request, contract, draft);
        var paymentEvidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            bill, Array.Empty<Payment>());
        var firstPreflight = FinalInvoiceIssuePreflightEvaluator.Assess(
            bill, firstSnapshot, paymentEvidence);

        Assert.Equal(firstSnapshot.KnownRentAndUtilities,
            secondSnapshot.KnownRentAndUtilities);
        Assert.NotEqual(firstSnapshot.EvidenceSha256,
            secondSnapshot.EvidenceSha256);
        Assert.Throws<InvalidOperationException>(() =>
            FinalInvoiceIssueEvidenceRecord.Prepare(
                Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
                secondSnapshot, firstPreflight));
    }

    [Fact]
    public void ISSUEARCH04_Changed_Stored_Amount_Or_Json_Is_Detected()
    {
        var (evidence, preflight) = StagingFixture();
        var stored = FinalInvoiceIssueEvidenceRecord.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, evidence, preflight);
        typeof(FinalInvoiceIssueEvidenceRecord)
            .GetProperty(nameof(FinalInvoiceIssueEvidenceRecord.KnownBaseChargeTotal))!
            .SetValue(stored, 1m);
        Assert.Throws<InvalidDataException>(stored.VerifyStoredEvidence);

        var other = FinalInvoiceIssueEvidenceRecord.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, evidence, preflight);
        typeof(FinalInvoiceIssueEvidenceRecord)
            .GetProperty(nameof(FinalInvoiceIssueEvidenceRecord.EvidenceJson))!
            .SetValue(other, other.EvidenceJson + " ");
        Assert.Throws<InvalidDataException>(other.VerifyStoredEvidence);
    }

    [Theory]
    [InlineData("MissingSourceId")]
    [InlineData("MissingMeterId")]
    [InlineData("InvalidSourceType")]
    [InlineData("OutsideBillingPeriod")]
    [InlineData("ReadingDeltaMismatch")]
    [InlineData("MissingRateId")]
    [InlineData("NonUtcCaptureTime")]
    [InlineData("RepeatedMeterSource")]
    public void ISSUEARCH07_Rehashed_But_Invalid_Historical_Meter_Evidence_Is_Rejected(
        string corruption)
    {
        var (evidence, preflight) = StagingFixture();
        var record = FinalInvoiceIssueEvidenceRecord.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
            evidence, preflight);
        record.VerifyStoredEvidence();

        var root = JsonNode.Parse(record.EvidenceJson)!;
        var firstUtility = root["Utilities"]![0]!;
        var source = firstUtility["Sources"]![0]!;

        switch (corruption)
        {
            case "MissingSourceId":
                source["SourceId"] = Guid.Empty.ToString();
                break;
            case "MissingMeterId":
                source["UtilityMeterId"] = Guid.Empty.ToString();
                break;
            case "InvalidSourceType":
                source["SourceType"] = 999;
                break;
            case "OutsideBillingPeriod":
                source["RecordedDate"] = "2026-10-31T00:00:00";
                break;
            case "ReadingDeltaMismatch":
                source["CurrentReading"] = 999999m;
                break;
            case "MissingRateId":
                firstUtility["UtilityRateId"] = Guid.Empty.ToString();
                break;
            case "NonUtcCaptureTime":
                root["CapturedAtUtc"] = "2026-11-30T18:00:00";
                break;
            case "RepeatedMeterSource":
                var second = root["Utilities"]![1]!["Sources"]![0]!;
                second["SourceId"] = source["SourceId"]!.ToString();
                second["SourceType"] = source["SourceType"]!.GetValue<int>();
                break;
            default:
                throw new ArgumentException("Unknown test scenario", nameof(corruption));
        }

        // Deliberately recompute the checksum to show that SHA-256 alone
        // is NOT business validation or a protected digital signature.
        var modifiedJson = root.ToJsonString();
        var modifiedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(modifiedJson)))
            .ToLowerInvariant();
        typeof(FinalInvoiceIssueEvidenceRecord)
            .GetProperty(nameof(FinalInvoiceIssueEvidenceRecord.EvidenceJson))!
            .SetValue(record, modifiedJson);
        typeof(FinalInvoiceIssueEvidenceRecord)
            .GetProperty(nameof(FinalInvoiceIssueEvidenceRecord.EvidenceSha256))!
            .SetValue(record, modifiedHash);

        Assert.Throws<InvalidDataException>(record.VerifyStoredEvidence);
        Assert.False(record.IsInvoiceIssued);
        Assert.False(record.IsReceivablePosted);
    }

    [Fact]
    public void ISSUEDRIFT01_Unchanged_Draft_Matches_Original_Capture()
    {
        var (archive, request, contract, draft) = DriftFixture();
        var result = DriftReview(archive, request, contract, draft);

        Assert.True(result.MatchesCurrentDraft);
        Assert.DoesNotContain("ArchivedEvidenceDiffersFromCurrentDraft",
            result.Blockers);
        Assert.Contains("InvoiceIssuanceAndDebtPostingNotImplemented",
            result.Blockers);
        Assert.False(result.CanIssue);
        Assert.False(result.IsReceivablePosted);
    }

    [Fact]
    public void ISSUEDRIFT02_Changing_Stored_Utility_Rate_Id_Is_Detected()
    {
        var (archive, request, contract, draft) = DriftFixture();
        var electricity = draft.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity);
        typeof(InvoiceItem).GetProperty(nameof(InvoiceItem.UtilityRateId))!
            .SetValue(electricity, Guid.NewGuid());

        var result = DriftReview(archive, request, contract, draft);

        Assert.False(result.MatchesCurrentDraft);
        Assert.Contains("ArchivedEvidenceDiffersFromCurrentDraft", result.Blockers);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void ISSUEDRIFT03_Changing_Meter_Source_Reading_Is_Detected()
    {
        var (archive, request, contract, draft) = DriftFixture();
        var electricitySource = draft.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity).MeterSources.Single();
        typeof(InvoiceItemMeterSource)
            .GetProperty(nameof(InvoiceItemMeterSource.CurrentReading))!
            .SetValue(electricitySource, electricitySource.CurrentReading + 1m);

        var result = DriftReview(archive, request, contract, draft);

        Assert.False(result.MatchesCurrentDraft);
        Assert.Contains("CurrentDraftEvidenceInvalid", result.Blockers);
    }

    [Fact]
    public void ISSUEDRIFT04_Missing_Or_Ambiguous_Draft_Is_Never_Current()
    {
        var (archive, request, contract, draft) = DriftFixture();
        var items = draft.Items.ToArray();
        var sources = items.SelectMany(x => x.MeterSources).ToArray();

        var missing = FinalInvoiceIssueEvidenceDriftEvaluator.Assess(
            archive, request, contract, Array.Empty<Invoice>(),
            items, sources);
        var ambiguous = FinalInvoiceIssueEvidenceDriftEvaluator.Assess(
            archive, request, contract, new[] { draft, draft },
            items, sources);

        Assert.False(missing.MatchesCurrentDraft);
        Assert.False(ambiguous.MatchesCurrentDraft);
        Assert.Contains("CurrentDraftMissingAmbiguousOrChanged", missing.Blockers);
        Assert.Contains("CurrentDraftMissingAmbiguousOrChanged", ambiguous.Blockers);
    }

    [Fact]
    public void ISSUEDRIFT05_Request_Scope_Change_Blocks_Reusing_Archive()
    {
        var (archive, _, contract, draft) = DriftFixture();
        var otherRequest = new MoveOutRequest(
            Guid.NewGuid(), contract.Id, contract.RoomId,
            new DateOnly(2026, 11, 30), "Other request");
        otherRequest.Approve(DateTime.UtcNow, null);

        var result = DriftReview(archive, otherRequest, contract, draft);

        Assert.False(result.MatchesCurrentDraft);
        Assert.Contains("MoveOutOrContractScopeChanged", result.Blockers);
    }

    private static FinalInvoiceIssueEvidenceDriftReview DriftReview(
        FinalInvoiceIssueEvidenceRecord archive,
        MoveOutRequest request, Contract contract, Invoice draft)
    {
        var items = draft.Items.ToArray();
        return FinalInvoiceIssueEvidenceDriftEvaluator.Assess(
            archive, request, contract, new[] { draft }, items,
            items.SelectMany(x => x.MeterSources).ToArray());
    }

    private static (FinalInvoiceIssueEvidenceRecord, MoveOutRequest, Contract, Invoice)
        DriftFixture()
    {
        var (request, contract, draft) = Fixture();
        var package = Package(request, contract, draft,
            new DateTime(2026, 11, 30, 18, 0, 0, DateTimeKind.Utc));
        var review = Review(request, contract, draft);
        var receipts = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, Array.Empty<Payment>());
        var preflight = FinalInvoiceIssuePreflightEvaluator.Assess(
            review, package, receipts);
        var archive = FinalInvoiceIssueEvidenceRecord.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
            package, preflight);
        return (archive, request, contract, draft);
    }

    private static (FinalInvoiceEvidencePackage, FinalInvoiceIssuePreflight)
        StagingFixture()
    {
        var (request, contract, draft) = Fixture();
        var review = Review(request, contract, draft);
        var evidence = Package(request, contract, draft, DateTime.UtcNow);
        var receipts = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, Array.Empty<Payment>());
        var preflight = FinalInvoiceIssuePreflightEvaluator.Assess(
            review, evidence, receipts);
        return (evidence, preflight);
    }

    private static FinalMoveOutBillReview Review(
        MoveOutRequest request, Contract contract, Invoice draft)
        => FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { draft }, draft.Items.ToArray(),
            draft.Items.SelectMany(x => x.MeterSources).ToArray());

    private static (MoveOutRequest, Contract, Invoice) Fixture(bool approve = true)
    {
        var roomId = Guid.NewGuid();
        var contract = new Contract(Guid.NewGuid(), "TEST-FINAL",
            roomId, new DateTime(2026, 1, 1), null, 3_500_000m);
        contract.Activate();
        var request = new MoveOutRequest(Guid.NewGuid(), contract.Id,
            roomId, new DateOnly(2026, 11, 30), null);
        if (approve)
            request.Approve(DateTime.UtcNow, null);
        return (request, contract, BuildDraft(contract.Id, roomId));
    }

    private static Invoice BuildDraft(Guid contractId, Guid roomId, int month = 11)
    {
        var sourceType = Enum.GetValues<MeterUsageSourceType>()[0];
        InvoiceDraftUtilityLineSnapshot Line(
            UtilityType type, decimal quantity, decimal unitPrice)
        {
            var meter = new InvoiceDraftMeterSegmentSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), sourceType,
                new DateTime(2026, month, 30), 0m, quantity);
            return new InvoiceDraftUtilityLineSnapshot(
                type, Guid.NewGuid(), "SingleRateWholeMonth",
                quantity, unitPrice, quantity * unitPrice,
                new[] { meter }, Array.Empty<string>());
        }

        var snapshot = InvoiceDraftSnapshot.Capture(
            contractId, roomId, 2026, month,
            new[]
            {
                Line(UtilityType.Electricity, 10m, 3_000m),
                Line(UtilityType.Water, 2m, 10_000m)
            });
        return Invoice.CreateDraft(Guid.NewGuid(), snapshot, Guid.NewGuid);
    }
}
