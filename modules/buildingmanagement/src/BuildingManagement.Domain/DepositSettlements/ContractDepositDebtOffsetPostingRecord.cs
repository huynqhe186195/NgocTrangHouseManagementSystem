using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// OI-04 authenticated INTERNAL offset posting: one append-only balanced event
/// represents BOTH the debit of held deposit and the credit of official debt.
/// It is never a bank receipt, refund, forfeiture or contract closeout.
/// Issued invoices and their original payment journals are never mutated.
/// Only the isolated SERIALIZABLE staging writer may create this event after
/// independent debt review, signed Owner approval and commit-time re-audit.
/// </summary>
public sealed class ContractDepositDebtOffsetPostingRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid ApprovalId { get; private set; }
    public Guid PostedByOwnerId { get; private set; }
    public DateTime PostedAtUtc { get; private set; }
    public decimal DebitHeldDepositVnd { get; private set; }
    public decimal CreditOfficialDebtVnd { get; private set; }
    public decimal RemainingHeldDepositVnd { get; private set; }
    public decimal RemainingOfficialDebtVnd { get; private set; }
    public string ApprovalSha256 { get; private set; } = string.Empty;
    public string PostingJson { get; private set; } = string.Empty;
    public string PostingSha256 { get; private set; } = string.Empty;

    public bool IsInternalOffsetPosted => true;
    public bool IsExternallyVerifiedBankTransfer => false;
    public bool CanRefund => false;
    public bool CanCompleteMoveOut => false;

    private ContractDepositDebtOffsetPostingRecord() { }
    private ContractDepositDebtOffsetPostingRecord(Guid id) : base(id) { }

    public static ContractDepositDebtOffsetPostingRecord Post(
        Guid id, Guid actor, DateTime postedAtUtc,
        ContractDepositDebtOffsetApprovalRecord approval,
        decimal currentHeldDepositVnd,
        IReadOnlyCollection<ContractDepositOffsetClaim> currentOfficialClaims)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(currentOfficialClaims);
        approval.RequireCurrentEvidence(approval.ContractId,
            approval.MoveOutRequestId, approval.BuildingId,
            currentHeldDepositVnd, currentOfficialClaims);

        if (id == Guid.Empty || actor == Guid.Empty ||
            actor != approval.ApprovedByOwnerId ||
            postedAtUtc.Kind != DateTimeKind.Utc ||
            postedAtUtc < approval.ApprovedAtUtc ||
            postedAtUtc > DateTime.UtcNow ||
            currentHeldDepositVnd != approval.VerifiedHeldAmount ||
            approval.ApprovedOffsetAmount <= 0m ||
            approval.ApprovedOffsetAmount > currentHeldDepositVnd ||
            approval.ExpectedRemainingDeposit < 0m ||
            approval.ExpectedUncoveredDebt < 0m)
            throw new InvalidOperationException(
                "Internal offset requires current independently verified cash, debt and the same signed Owner.");

        using var snapshot = JsonDocument.Parse(approval.ApprovalJson);
        var lines = JsonSerializer.Deserialize<ContractDepositOffsetPreviewLine[]>(
            snapshot.RootElement.GetProperty("ApprovedLines").GetRawText())
            ?? throw new InvalidDataException("Owner approval has no offset lines.");
        if (lines.Length == 0 || lines.Sum(x => x.ProposedAppliedVnd) !=
            approval.ApprovedOffsetAmount)
            throw new InvalidDataException("Owner authorization is not balanced.");

        // Keep exactly the source claim IDs and allocations signed by Owner;
        // no custom HTTP-provided amount, invoice line or tier priority.
        var at = postedAtUtc.AddTicks(-(postedAtUtc.Ticks % 10L));
        if (at < approval.ApprovedAtUtc)
            throw new InvalidOperationException("Offset cannot predate its approval.");
        var json = JsonSerializer.Serialize(new
        {
            ContractId = approval.ContractId,
            MoveOutRequestId = approval.MoveOutRequestId,
            BuildingId = approval.BuildingId,
            ApprovalId = approval.Id,
            PostedByOwnerId = actor,
            PostedAtUtc = at,
            DebitHeldDepositVnd = approval.ApprovedOffsetAmount,
            CreditOfficialDebtVnd = approval.ApprovedOffsetAmount,
            RemainingHeldDepositVnd = approval.ExpectedRemainingDeposit,
            RemainingOfficialDebtVnd = approval.ExpectedUncoveredDebt,
            ApprovalSha256 = approval.ApprovalSha256,
            ApprovedLines = lines
        });
        var result = new ContractDepositDebtOffsetPostingRecord(id)
        {
            ContractId = approval.ContractId,
            MoveOutRequestId = approval.MoveOutRequestId,
            BuildingId = approval.BuildingId,
            ApprovalId = approval.Id,
            PostedByOwnerId = actor,
            PostedAtUtc = at,
            DebitHeldDepositVnd = approval.ApprovedOffsetAmount,
            CreditOfficialDebtVnd = approval.ApprovedOffsetAmount,
            RemainingHeldDepositVnd = approval.ExpectedRemainingDeposit,
            RemainingOfficialDebtVnd = approval.ExpectedUncoveredDebt,
            ApprovalSha256 = approval.ApprovalSha256,
            PostingJson = json,
            PostingSha256 = Hash(json)
        };
        result.VerifyStoredPosting(approval);
        return result;
    }

    public IReadOnlyList<ContractDepositOffsetPreviewLine> ReadVerifiedLines(
        ContractDepositDebtOffsetApprovalRecord approval)
    {
        VerifyStoredPosting(approval);
        using var doc = JsonDocument.Parse(PostingJson);
        return Array.AsReadOnly(JsonSerializer.Deserialize<ContractDepositOffsetPreviewLine[]>(
            doc.RootElement.GetProperty("ApprovedLines").GetRawText())!);
    }

    public void VerifyStoredPosting(ContractDepositDebtOffsetApprovalRecord approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        approval.VerifyStoredApproval();
        if (Id == Guid.Empty || ContractId != approval.ContractId ||
            MoveOutRequestId != approval.MoveOutRequestId ||
            BuildingId != approval.BuildingId || ApprovalId != approval.Id ||
            PostedByOwnerId != approval.ApprovedByOwnerId ||
            PostedAtUtc.Kind != DateTimeKind.Utc ||
            PostedAtUtc < approval.ApprovedAtUtc ||
            DebitHeldDepositVnd <= 0m ||
            DebitHeldDepositVnd != CreditOfficialDebtVnd ||
            DebitHeldDepositVnd != approval.ApprovedOffsetAmount ||
            RemainingHeldDepositVnd != approval.ExpectedRemainingDeposit ||
            RemainingOfficialDebtVnd != approval.ExpectedUncoveredDebt ||
            ApprovalSha256 != approval.ApprovalSha256 ||
            PostingJson.Length is < 100 or > 1000000 ||
            PostingSha256 != Hash(PostingJson))
            throw new InvalidDataException("Internal deposit offset proof is invalid.");
        try
        {
            using var doc = JsonDocument.Parse(PostingJson);
            var d = doc.RootElement;
            if (d.GetProperty("ContractId").GetGuid() != ContractId ||
                d.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                d.GetProperty("BuildingId").GetGuid() != BuildingId ||
                d.GetProperty("ApprovalId").GetGuid() != ApprovalId ||
                d.GetProperty("PostedByOwnerId").GetGuid() != PostedByOwnerId ||
                d.GetProperty("PostedAtUtc").GetDateTime() != PostedAtUtc ||
                d.GetProperty("DebitHeldDepositVnd").GetDecimal() != DebitHeldDepositVnd ||
                d.GetProperty("CreditOfficialDebtVnd").GetDecimal() != CreditOfficialDebtVnd ||
                d.GetProperty("RemainingHeldDepositVnd").GetDecimal() != RemainingHeldDepositVnd ||
                d.GetProperty("RemainingOfficialDebtVnd").GetDecimal() != RemainingOfficialDebtVnd ||
                d.GetProperty("ApprovalSha256").GetString() != ApprovalSha256)
                throw new InvalidDataException("Internal offset indexed amount was altered.");
            var lines = JsonSerializer.Deserialize<ContractDepositOffsetPreviewLine[]>(
                d.GetProperty("ApprovedLines").GetRawText())
                ?? throw new InvalidDataException("Missing internal offset lines.");
            using var original = JsonDocument.Parse(approval.ApprovalJson);
            var approved = JsonSerializer.Deserialize<ContractDepositOffsetPreviewLine[]>(
                original.RootElement.GetProperty("ApprovedLines").GetRawText())!;
            if (!lines.SequenceEqual(approved) ||
                lines.Sum(x => x.ProposedAppliedVnd) != CreditOfficialDebtVnd)
                throw new InvalidDataException("Internal offset lines differ from Owner signature.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
            or FormatException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("Internal offset JSON is corrupt.", ex);
        }
    }

    private static string Hash(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
