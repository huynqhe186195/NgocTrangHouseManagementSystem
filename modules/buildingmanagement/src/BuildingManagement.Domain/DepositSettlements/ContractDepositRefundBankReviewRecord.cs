using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Two-person review of ANY EXTERNALLY EXECUTED manual refund. This does
/// not initiate bank transfers, query a bank API, or claim automatic bank
/// verification. The reviewer attests a concrete bank statement reference
/// independently of the root Owner who approved the final disposition.
/// </summary>
public sealed class ContractDepositRefundBankReviewRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid DepositDecisionId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid ReviewedByUserId { get; private set; }
    public DateTime ReviewedAtUtc { get; private set; }
    public DateTime PaidAtUtc { get; private set; }
    public decimal VerifiedRefundAmountVnd { get; private set; }
    public string BankTransactionReference { get; private set; } = string.Empty;
    public string BankStatementEvidenceReference { get; private set; } = string.Empty;
    public string SignedReviewReference { get; private set; } = string.Empty;
    public string ReviewJson { get; private set; } = string.Empty;
    public string ReviewSha256 { get; private set; } = string.Empty;
    public bool IsBankApiVerified => false;

    private ContractDepositRefundBankReviewRecord() { }
    private ContractDepositRefundBankReviewRecord(Guid id) : base(id) { }

    public static ContractDepositRefundBankReviewRecord Attest(
        Guid id, ContractDepositSettlementDecisionRecord decision,
        Guid reviewerId, DateTime reviewedAtUtc, DateTime paidAtUtc,
        string transactionReference, string statementEvidenceReference,
        string signedReviewReference)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision.VerifyStoredDecision();
        var bank = transactionReference?.Trim() ?? string.Empty;
        var statement = statementEvidenceReference?.Trim() ?? string.Empty;
        var signature = signedReviewReference?.Trim() ?? string.Empty;
        if (id == Guid.Empty || reviewerId == Guid.Empty ||
            reviewerId == decision.ApprovedByUserId ||
            decision.ApprovedRefundAmount <= 0m ||
            reviewedAtUtc.Kind != DateTimeKind.Utc ||
            paidAtUtc.Kind != DateTimeKind.Utc ||
            paidAtUtc < decision.ApprovedAtUtc ||
            reviewedAtUtc < paidAtUtc ||
            reviewedAtUtc > DateTime.UtcNow ||
            bank.Length is < 8 or > 128 ||
            statement.Length is < 12 or > 500 ||
            signature.Length is < 12 or > 500 ||
            string.Equals(bank, statement, StringComparison.Ordinal) ||
            string.Equals(bank, signature, StringComparison.Ordinal) ||
            string.Equals(statement, signature, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Refund needs a distinct reviewer, actual paid bank evidence and signed reference.");
        var at = reviewedAtUtc.AddTicks(-(reviewedAtUtc.Ticks % 10L));
        var paid = paidAtUtc.AddTicks(-(paidAtUtc.Ticks % 10L));
        var json = JsonSerializer.Serialize(new
        {
            ContractId = decision.ContractId,
            MoveOutRequestId = decision.MoveOutRequestId,
            DepositDecisionId = decision.Id,
            BuildingId = decision.BuildingId,
            ReviewedByUserId = reviewerId,
            ReviewedAtUtc = at,
            PaidAtUtc = paid,
            VerifiedRefundAmountVnd = decision.ApprovedRefundAmount,
            BankTransactionReference = bank,
            BankStatementEvidenceReference = statement,
            SignedReviewReference = signature
        });
        var result = new ContractDepositRefundBankReviewRecord(id)
        {
            ContractId = decision.ContractId,
            MoveOutRequestId = decision.MoveOutRequestId,
            DepositDecisionId = decision.Id,
            BuildingId = decision.BuildingId,
            ReviewedByUserId = reviewerId,
            ReviewedAtUtc = at,
            PaidAtUtc = paid,
            VerifiedRefundAmountVnd = decision.ApprovedRefundAmount,
            BankTransactionReference = bank,
            BankStatementEvidenceReference = statement,
            SignedReviewReference = signature,
            ReviewJson = json,
            ReviewSha256 = Sha(json)
        };
        result.Verify(decision);
        return result;
    }

    public void Verify(ContractDepositSettlementDecisionRecord decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision.VerifyStoredDecision();
        if (Id == Guid.Empty || ContractId != decision.ContractId ||
            MoveOutRequestId != decision.MoveOutRequestId ||
            DepositDecisionId != decision.Id ||
            BuildingId != decision.BuildingId ||
            ReviewedByUserId == Guid.Empty ||
            ReviewedByUserId == decision.ApprovedByUserId ||
            VerifiedRefundAmountVnd != decision.ApprovedRefundAmount ||
            VerifiedRefundAmountVnd <= 0m ||
            ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            PaidAtUtc.Kind != DateTimeKind.Utc ||
            PaidAtUtc < decision.ApprovedAtUtc ||
            ReviewedAtUtc < PaidAtUtc ||
            BankTransactionReference.Length is < 8 or > 128 ||
            BankStatementEvidenceReference.Length is < 12 or > 500 ||
            SignedReviewReference.Length is < 12 or > 500 ||
            string.Equals(BankTransactionReference,
                BankStatementEvidenceReference, StringComparison.Ordinal) ||
            string.Equals(BankTransactionReference,
                SignedReviewReference, StringComparison.Ordinal) ||
            string.Equals(BankStatementEvidenceReference,
                SignedReviewReference, StringComparison.Ordinal) ||
            ReviewJson.Length is < 10 or > 1000000 ||
            ReviewSha256 != Sha(ReviewJson))
            throw new InvalidDataException(
                "Refund bank evidence diverges from signed deposit decision.");
        try
        {
            using var parsed = JsonDocument.Parse(ReviewJson);
            var x = parsed.RootElement;
            if (x.GetProperty("ContractId").GetGuid() != ContractId ||
                x.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                x.GetProperty("DepositDecisionId").GetGuid() != DepositDecisionId ||
                x.GetProperty("BuildingId").GetGuid() != BuildingId ||
                x.GetProperty("ReviewedByUserId").GetGuid() != ReviewedByUserId ||
                x.GetProperty("ReviewedAtUtc").GetDateTime() != ReviewedAtUtc ||
                x.GetProperty("PaidAtUtc").GetDateTime() != PaidAtUtc ||
                x.GetProperty("VerifiedRefundAmountVnd").GetDecimal() != VerifiedRefundAmountVnd ||
                x.GetProperty("BankTransactionReference").GetString() != BankTransactionReference ||
                x.GetProperty("BankStatementEvidenceReference").GetString() !=
                    BankStatementEvidenceReference ||
                x.GetProperty("SignedReviewReference").GetString() != SignedReviewReference)
                throw new InvalidDataException("Refund bank review indexed fields differ.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        { throw new InvalidDataException("Refund review JSON invalid.",ex); }
    }

    public void RequireForCloseout(
        ContractDepositSettlementDecisionRecord decision, Guid ownerActorId,
        DateTime paidAtUtc, string transactionReference,
        string bankStatementEvidenceReference)
    {
        Verify(decision);
        if (ownerActorId == ReviewedByUserId ||
            PaidAtUtc != paidAtUtc ||
            !StringComparer.Ordinal.Equals(BankTransactionReference,
                transactionReference?.Trim()) ||
            !StringComparer.Ordinal.Equals(BankStatementEvidenceReference,
                bankStatementEvidenceReference?.Trim()))
            throw new InvalidOperationException("Refund proof is not independently matched.");
    }

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))
            .ToLowerInvariant();
}
