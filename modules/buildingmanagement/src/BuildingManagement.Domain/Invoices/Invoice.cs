using System;
using System.Collections.Generic;
using System.Linq;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Invoices;

/// <summary>
/// Draft-only persistence aggregate. A draft is not a payable bill.
/// This type intentionally exposes no Issue, Post, Pay or Delete transition.
/// </summary>
public sealed class Invoice : AuditedAggregateRoot<Guid>
{
    private readonly List<InvoiceItem> _items = new();

    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public int BillingYear { get; private set; }
    public int BillingMonth { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public decimal? ProvisionalUtilityTotal { get; private set; }
    public IReadOnlyCollection<InvoiceItem> Items => _items.AsReadOnly();

    protected Invoice() { }

    private Invoice(Guid id, InvoiceDraftSnapshot snapshot) : base(id)
    {
        ContractId = snapshot.ContractId;
        RoomId = snapshot.RoomId;
        BillingYear = snapshot.BillingYear;
        BillingMonth = snapshot.BillingMonth;
        Status = InvoiceStatus.Draft;
        ProvisionalUtilityTotal = snapshot.ProvisionalTotal;
    }

    public static Invoice CreateDraft(
        Guid id,
        InvoiceDraftSnapshot snapshot,
        Func<Guid> nextChildId)
    {
        if (id == Guid.Empty) throw new ArgumentException("Invoice Id required.", nameof(id));
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nextChildId);

        var invoice = new Invoice(id, snapshot);
        var seenIds = new HashSet<Guid> { id };
        foreach (var line in snapshot.UtilityLines)
        {
            var itemId = NextUniqueId();
            invoice._items.Add(InvoiceItem.FromSnapshot(
                itemId, id, line, NextUniqueId));
        }

        return invoice;

        Guid NextUniqueId()
        {
            var generated = nextChildId();
            if (generated == Guid.Empty || !seenIds.Add(generated))
                throw new ArgumentException("Child identifiers must be unique, non-empty.", nameof(nextChildId));
            return generated;
        }
    }
}
