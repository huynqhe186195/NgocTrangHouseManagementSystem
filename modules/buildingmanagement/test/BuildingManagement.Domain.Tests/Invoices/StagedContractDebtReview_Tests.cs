using System;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class StagedContractDebtReview_Tests
{
    private static readonly DateOnly BeforeDue = new(2026, 11, 4);
    private static readonly DateOnly AfterDue = new(2026, 11, 9);

    [Fact]
    public void STAGEDCONTRACT01_Multiple_Invoices_Have_Independent_Balances()
    {
        var contract = Guid.NewGuid();
        var one = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var two = Invoice(contract, 700_000m, new DateOnly(2026, 11, 8));
        var payment = Receipt(contract, one.InvoiceId, 200_000m);
        payment.Complete(new DateTime(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc));
        var result = Assess(contract, BeforeDue, new[] { one, two },
            new[] { Entry(payment, one.InvoiceId) }, new[] { payment });

        Assert.True(result.HasCompleteStagedEvidence);
        Assert.Equal(1_200_000m, result.TotalInvoiced);
        Assert.Equal(200_000m, result.TotalReceivedAsOf);
        Assert.Equal(1_000_000m, result.TotalOutstandingAsOf);
        Assert.Equal(2, result.Invoices.Count);
        Assert.False(result.IsOfficialReceivable);
        Assert.False(result.CanOffsetDeposit);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void STAGEDCONTRACT02_Overdue_And_Paid_Are_Separate()
    {
        var contract = Guid.NewGuid();
        var one = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var two = Invoice(contract, 700_000m, new DateOnly(2026, 11, 8));
        var payment = Receipt(contract, one.InvoiceId, 500_000m);
        payment.Complete(new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc));
        var result = Assess(contract, AfterDue, new[] { one, two },
            new[] { Entry(payment, one.InvoiceId) }, new[] { payment });

        Assert.True(result.HasCompleteStagedEvidence);
        Assert.Equal(700_000m, result.TotalOutstandingAsOf);
        Assert.Contains(result.Invoices,
            x => x.InvoiceId == one.InvoiceId &&
                 x.Status == IssuedInvoicePaymentStatus.Paid);
        Assert.Contains(result.Invoices,
            x => x.InvoiceId == two.InvoiceId &&
                 x.Status == IssuedInvoicePaymentStatus.Overdue);
    }

    [Fact]
    public void STAGEDCONTRACT03_Future_Receipt_Is_Excluded_From_AsOf_Sums()
    {
        var contract = Guid.NewGuid();
        var invoice = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var payment = Receipt(contract, invoice.InvoiceId, 500_000m);
        payment.Complete(new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));
        var result = Assess(contract, BeforeDue, new[] { invoice },
            new[] { Entry(payment, invoice.InvoiceId) }, new[] { payment });

        Assert.True(result.HasCompleteStagedEvidence);
        Assert.Equal(0m, result.TotalReceivedAsOf);
        Assert.Equal(500_000m, result.TotalOutstandingAsOf);
    }

    [Fact]
    public void STAGEDCONTRACT04_Unjournaled_Payment_Invalidates_All_Balances()
    {
        var contract = Guid.NewGuid();
        var invoice = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var payment = Receipt(contract, invoice.InvoiceId, 100_000m);
        payment.Complete(DateTime.UtcNow);
        var result = Assess(contract, BeforeDue, new[] { invoice },
            Array.Empty<InvoicePaymentAllocationJournalEntry>(), new[] { payment });

        Assert.Contains("CompletedInvoicePaymentUnjournaled", result.Blockers);
        Assert.Null(result.TotalOutstandingAsOf);
        Assert.Empty(result.Invoices);
    }

    [Fact]
    public void STAGEDCONTRACT05_Unknown_Invoice_Allocation_Blocks_Review()
    {
        var contract = Guid.NewGuid();
        var invoice = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var other = Guid.NewGuid();
        var payment = Receipt(contract, other, 100_000m);
        payment.Complete(DateTime.UtcNow);
        var result = Assess(contract, BeforeDue, new[] { invoice },
            new[] { Entry(payment, other) }, new[] { payment });

        Assert.Contains("AllocationReferencesMissingContractInvoice",
            result.Blockers);
        Assert.Null(result.TotalOutstandingAsOf);
    }

    [Fact]
    public void STAGEDCONTRACT06_Duplicate_Or_Foreign_Invoice_Blocks_Review()
    {
        var contract = Guid.NewGuid();
        var invoice = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var duplicate = Assess(contract, BeforeDue, new[] { invoice, invoice },
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            Array.Empty<Payment>());
        Assert.Contains("InvalidOrDuplicateIssuedInvoices", duplicate.Blockers);
        var foreign = Assess(contract, BeforeDue,
            new[] { invoice with { ContractId = Guid.NewGuid() } },
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            Array.Empty<Payment>());
        Assert.Contains("InvalidOrDuplicateIssuedInvoices", foreign.Blockers);
    }

    [Fact]
    public void STAGEDCONTRACT07_Overpayment_Blocks_Contract_Closeout_Preview()
    {
        var contract = Guid.NewGuid();
        var invoice = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var payment = Receipt(contract, invoice.InvoiceId, 600_000m);
        payment.Complete(new DateTime(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc));
        var result = Assess(contract, BeforeDue, new[] { invoice },
            new[] { Entry(payment, invoice.InvoiceId) }, new[] { payment });

        Assert.Contains("UnresolvedInvoiceOverpayment", result.Blockers);
        Assert.Null(result.TotalOutstandingAsOf);
        Assert.Empty(result.Invoices);
        Assert.False(result.CanOffsetDeposit);
    }

    [Fact]
    public void STAGEDCONTRACT09_Future_Issued_Invoice_Is_Excluded_From_History()
    {
        var contract = Guid.NewGuid();
        var historical = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var future = Invoice(contract, 700_000m, new DateOnly(2026, 11, 10))
            with
            {
                IssuedAtUtc = new DateTime(
                    2026, 11, 5, 18, 0, 0, DateTimeKind.Utc)
            };
        var result = Assess(contract, BeforeDue,
            new[] { historical, future },
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            Array.Empty<Payment>());

        Assert.True(result.HasCompleteStagedEvidence);
        Assert.Equal(500_000m, result.TotalInvoiced);
        Assert.Equal(500_000m, result.TotalOutstandingAsOf);
        Assert.Single(result.Invoices);
    }

    [Fact]
    public void STAGEDCONTRACT10_Receipt_Before_Issue_Is_Not_Valid_Payment_Evidence()
    {
        var contract = Guid.NewGuid();
        var invoice = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5))
            with
            {
                IssuedAtUtc = new DateTime(
                    2026, 11, 4, 8, 0, 0, DateTimeKind.Utc)
            };
        var payment = Receipt(contract, invoice.InvoiceId, 500_000m);
        payment.Complete(new DateTime(2026, 11, 4, 7, 59, 0, DateTimeKind.Utc));
        var result = Assess(contract, BeforeDue, new[] { invoice },
            new[] { Entry(payment, invoice.InvoiceId) }, new[] { payment });

        Assert.Contains("ReceiptPredatesInvoiceIssuance", result.Blockers);
        Assert.Null(result.TotalOutstandingAsOf);
    }

    [Fact]
    public void STAGEDCONTRACT11_Only_Future_Issuance_Is_Not_Historical_Zero_Proof()
    {
        var contract = Guid.NewGuid();
        var future = Invoice(contract, 500_000m, new DateOnly(2026, 11, 10))
            with
            {
                IssuedAtUtc = new DateTime(
                    2026, 11, 4, 18, 0, 0, DateTimeKind.Utc)
            };
        var result = Assess(contract, BeforeDue, new[] { future },
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            Array.Empty<Payment>());

        Assert.Contains("NoInvoiceIssuedAsOfDate", result.Blockers);
        Assert.Null(result.TotalOutstandingAsOf);
        Assert.False(result.CanOffsetDeposit);
    }

    [Fact]
    public void STAGEDCONTRACT12_Receipt_Not_Journaled_AsOf_Is_Not_Allocated()
    {
        var contract = Guid.NewGuid();
        var invoice = Invoice(contract, 500_000m, new DateOnly(2026, 11, 5));
        var payment = Receipt(contract, invoice.InvoiceId, 500_000m);
        payment.Complete(new DateTime(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc));
        var journal = Entry(payment, invoice.InvoiceId);
        typeof(InvoicePaymentAllocationJournalEntry)
            .GetProperty(nameof(InvoicePaymentAllocationJournalEntry.RecordedAtUtc))!
            .SetValue(journal, new DateTime(
                2026, 11, 7, 1, 0, 0, DateTimeKind.Utc));

        var history = Assess(contract, BeforeDue, new[] { invoice },
            new[] { journal }, new[] { payment });
        Assert.True(history.HasCompleteStagedEvidence);
        Assert.Equal(0m, history.TotalReceivedAsOf);
        Assert.Equal(500_000m, history.TotalOutstandingAsOf);

        var later = Assess(contract, new DateOnly(2026, 11, 8),
            new[] { invoice }, new[] { journal }, new[] { payment });
        Assert.True(later.HasCompleteStagedEvidence);
        Assert.Equal(500_000m, later.TotalReceivedAsOf);
        Assert.Equal(0m, later.TotalOutstandingAsOf);
    }

    [Fact]
    public void STAGEDCONTRACT08_No_Issued_Invoices_Is_Not_Zero_Debt_Proof()
    {
        var contract = Guid.NewGuid();
        var result = Assess(contract, BeforeDue,
            Array.Empty<StagedContractInvoiceDebtFacts>(),
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            Array.Empty<Payment>());
        Assert.Contains("NoIssuedInvoiceEvidence", result.Blockers);
        Assert.Null(result.TotalOutstandingAsOf);
    }

    private static StagedContractInvoiceDebtFacts Invoice(
        Guid contract, decimal amount, DateOnly due)
        => new(Guid.NewGuid(), contract, amount,
            new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), due);

    private static Payment Receipt(Guid contract, Guid invoice, decimal amount)
        => new(Guid.NewGuid(), "CTDEBT-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), PaymentPurpose.InvoicePayment,
            PaymentMethod.BankTransfer, PaymentChannel.Manual, amount,
            contractId: contract, invoiceId: invoice);

    private static InvoicePaymentAllocationJournalEntry Entry(
        Payment payment, Guid invoice)
        => InvoicePaymentAllocationJournalEntry.StageForValidation(
            Guid.NewGuid(), payment, invoice, Guid.NewGuid(), DateTime.UtcNow);

    private static StagedContractDebtReview Assess(
        Guid contract, DateOnly asOf,
        StagedContractInvoiceDebtFacts[] invoices,
        InvoicePaymentAllocationJournalEntry[] entries, Payment[] payments)
        => StagedContractDebtEvaluator.Assess(
            contract, asOf, invoices, entries, payments);
}
