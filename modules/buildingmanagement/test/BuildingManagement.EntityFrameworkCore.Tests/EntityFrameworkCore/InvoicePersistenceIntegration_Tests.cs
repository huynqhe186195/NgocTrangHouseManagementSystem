using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Floors;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Tenants;
using Volo.Abp;
using Volo.Abp.Data;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Exercises ACTUAL SQLite inserts, re-hydration of child snapshots and
/// unique DB constraints. Only the isolated ABP test database is used.
/// </summary>
public sealed class InvoicePersistenceIntegration_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<UtilityRate, Guid> _rates;
    private readonly IRepository<UtilityMeter, Guid> _meters;
    private readonly IRepository<MeterReading, Guid> _readings;
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IRepository<InvoiceDraftRevisionRecord, Guid> _revisions;

    public InvoicePersistenceIntegration_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
        _rates = GetRequiredService<IRepository<UtilityRate, Guid>>();
        _meters = GetRequiredService<IRepository<UtilityMeter, Guid>>();
        _readings = GetRequiredService<IRepository<MeterReading, Guid>>();
        _invoices = GetRequiredService<IRepository<Invoice, Guid>>();
        _revisions = GetRequiredService<IRepository<InvoiceDraftRevisionRecord, Guid>>();
    }

    private sealed record Fixture(
        Guid RoomId, Guid ContractId, Guid ElectricityRateId,
        Guid WaterRateId, Guid ElectricityMeterId, Guid WaterMeterId);

    private async Task<Fixture> SeedAsync()
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var electricityRateId = Guid.NewGuid();
        var waterRateId = Guid.NewGuid();
        var electricityMeterId = Guid.NewGuid();
        var waterMeterId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(new Building(
                buildingId, "Draft persistence building " + Guid.NewGuid().ToString("N"),
                "Test address"), autoSave: true);
            await _floors.InsertAsync(new Floor(
                floorId, buildingId, 1, "Floor 1"), autoSave: true);
            await _rooms.InsertAsync(new Room(
                roomId, floorId, "INV-" + Guid.NewGuid().ToString("N")[..8],
                "Test room", 25m, 2, RoomStatus.Available), autoSave: true);
            await _contracts.InsertAsync(new Contract(
                contractId, "INV-CONTRACT-" + Guid.NewGuid().ToString("N"),
                roomId, new DateTime(2026, 1, 1),
                new DateTime(2027, 12, 31), 3_000_000m), autoSave: true);
            await _rates.InsertAsync(new UtilityRate(
                electricityRateId, buildingId, UtilityType.Electricity,
                3500m, new DateTime(2026, 10, 1), null), autoSave: true);
            await _rates.InsertAsync(new UtilityRate(
                waterRateId, buildingId, UtilityType.Water,
                15000m, new DateTime(2026, 10, 1), null), autoSave: true);
            await _meters.InsertAsync(new UtilityMeter(
                electricityMeterId, roomId, UtilityType.Electricity,
                "INVE-" + Guid.NewGuid().ToString("N"),
                10m, new DateTime(2026, 1, 1)), autoSave: true);
            await _meters.InsertAsync(new UtilityMeter(
                waterMeterId, roomId, UtilityType.Water,
                "INVW-" + Guid.NewGuid().ToString("N"),
                100m, new DateTime(2026, 1, 1)), autoSave: true);
        });

        return new Fixture(roomId, contractId, electricityRateId,
            waterRateId, electricityMeterId, waterMeterId);
    }

    private async Task<(Fixture Data, Guid ElectricityReading)> SeedReadyOctoberAsync()
    {
        var f = await SeedAsync();
        var electricityReadingId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await _contracts.GetAsync(f.ContractId);
            contract.Activate();
            await _contracts.UpdateAsync(contract, autoSave: true);

            // The physical meters were installed in January, so
            // September closes the prior-month coverage requirement.
            await _readings.InsertAsync(new MeterReading(
                Guid.NewGuid(), f.ElectricityMeterId,
                2026, 9, 10m, 10m, new DateTime(2026, 9, 30)),
                autoSave: true);
            await _readings.InsertAsync(new MeterReading(
                Guid.NewGuid(), f.WaterMeterId,
                2026, 9, 100m, 100m, new DateTime(2026, 9, 30)),
                autoSave: true);

            await _readings.InsertAsync(new MeterReading(
                electricityReadingId, f.ElectricityMeterId,
                2026, 10, 10m, 20m, new DateTime(2026, 10, 31)),
                autoSave: true);
            await _readings.InsertAsync(new MeterReading(
                Guid.NewGuid(), f.WaterMeterId,
                2026, 10, 100m, 105m, new DateTime(2026, 10, 31)),
                autoSave: true);
        });
        return (f, electricityReadingId);
    }

    private static Invoice CreateDraft(Fixture f,
        Guid electricityReadingId, Guid waterReadingId,
        Guid? contractId = null)
    {
        InvoiceDraftUtilityLineSnapshot MakeLine(
            UtilityType type, Guid rateId, Guid meterId,
            Guid sourceId, decimal previous, decimal current, decimal unitPrice)
        {
            var source = new InvoiceDraftMeterSegmentSnapshot(
                sourceId, meterId, MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 31), previous, current);
            return new InvoiceDraftUtilityLineSnapshot(
                type, rateId, "SingleRateWholeMonth",
                current - previous, unitPrice,
                (current - previous) * unitPrice,
                new[] { source }, Array.Empty<string>());
        }

        var snapshot = InvoiceDraftSnapshot.Capture(
            contractId ?? f.ContractId, f.RoomId, 2026, 10,
            new[]
            {
                MakeLine(UtilityType.Electricity, f.ElectricityRateId,
                    f.ElectricityMeterId, electricityReadingId, 10m, 20m, 3500m),
                MakeLine(UtilityType.Water, f.WaterRateId,
                    f.WaterMeterId, waterReadingId, 100m, 105m, 15000m)
            });
        return Invoice.CreateDraft(Guid.NewGuid(), snapshot, Guid.NewGuid);
    }

    [Fact]
    public async Task INVDB01_Persists_And_Reloads_Header_Lines_And_Counter_Snapshots()
    {
        var fixture = await SeedAsync();
        var electricSource = Guid.NewGuid();
        var waterSource = Guid.NewGuid();
        var draft = CreateDraft(fixture, electricSource, waterSource);

        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _invoices.GetQueryableAsync();
            var loaded = await query.Include(x => x.Items)
                .ThenInclude(x => x.MeterSources)
                .SingleAsync(x => x.Id == draft.Id);

            Assert.Equal(InvoiceStatus.Draft, loaded.Status);
            Assert.Equal(fixture.ContractId, loaded.ContractId);
            Assert.Equal(2026, loaded.BillingYear);
            Assert.Equal(10, loaded.BillingMonth);
            Assert.Equal(110000m, loaded.ProvisionalUtilityTotal);
            Assert.Equal(2, loaded.Items.Count);

            var electric = loaded.Items.Single(x =>
                x.UtilityType == UtilityType.Electricity);
            Assert.Equal(10m, electric.RecordedQuantity);
            Assert.Equal(3500m, electric.UnitPrice);
            Assert.Equal(35000m, electric.ProvisionalAmount);
            Assert.Equal(electricSource, Assert.Single(electric.MeterSources).SourceId);

            var water = loaded.Items.Single(x =>
                x.UtilityType == UtilityType.Water);
            Assert.Equal(75000m, water.ProvisionalAmount);
            Assert.Equal(waterSource, Assert.Single(water.MeterSources).SourceId);
        });
    }

    [Fact]
    public async Task INVDB02_Database_Rejects_Second_Invoice_For_Same_Contract_Month()
    {
        var fixture = await SeedAsync();
        var first = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(first, autoSave: true);
        });

        var second = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        var failure = await Record.ExceptionAsync(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                await _invoices.InsertAsync(second, autoSave: true);
            });
        });

        Assert.NotNull(failure);
        Assert.Contains("UNIQUE", failure.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task INVDB03_Database_Rejects_Reused_Reading_Across_Contracts()
    {
        var fixture = await SeedAsync();
        var electricSource = Guid.NewGuid();
        var first = CreateDraft(fixture, electricSource, Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(first, autoSave: true);
        });

        // A different room + contract ensures this test exercises the
        // global SOURCE uniqueness, not the new room-month constraint.
        var separateRoom = await SeedAsync();
        var second = CreateDraft(
            separateRoom, electricSource, Guid.NewGuid());
        var failure = await Record.ExceptionAsync(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                await _invoices.InsertAsync(second, autoSave: true);
            });
        });

        Assert.NotNull(failure);
        Assert.Contains("UNIQUE", failure.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task INVDB04_Changing_Rate_Does_Not_Reprice_Stored_Draft_Snapshot()
    {
        var fixture = await SeedAsync();
        var draft = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var rate = await _rates.GetAsync(fixture.ElectricityRateId);
            rate.Update(
                rate.BuildingId, rate.UtilityType, 9999m,
                rate.EffectiveFrom, rate.EffectiveTo, rate.Notes);
            await _rates.UpdateAsync(rate, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _invoices.GetQueryableAsync();
            var stored = await query.Include(x => x.Items)
                .ThenInclude(x => x.MeterSources)
                .SingleAsync(x => x.Id == draft.Id);
            var electricity = stored.Items.Single(x =>
                x.UtilityType == UtilityType.Electricity);

            Assert.Equal(3500m, electricity.UnitPrice);
            Assert.Equal(35000m, electricity.ProvisionalAmount);
            Assert.Equal(110000m, stored.ProvisionalUtilityTotal);
            Assert.Equal(10m,
                Assert.Single(electricity.MeterSources).Quantity);
        });
    }


    [Fact]
    public async Task INVDB05_Rejects_Two_Contracts_For_One_Room_Month()
    {
        var fixture = await SeedAsync();
        var first = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(first, autoSave: true);
        });

        // Two different contract numbers are still the SAME physical room.
        // Different meter source IDs bypass the global-source guard, so
        // the room-month unique index must reject the second draft.
        var otherContractId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _contracts.InsertAsync(new Contract(
                otherContractId, "INV-ROOM-CONFLICT-" + Guid.NewGuid().ToString("N"),
                fixture.RoomId, new DateTime(2026, 1, 1),
                new DateTime(2027, 12, 31), 3_000_000m), autoSave: true);
        });

        var second = CreateDraft(
            fixture, Guid.NewGuid(), Guid.NewGuid(), otherContractId);
        var error = await Record.ExceptionAsync(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                await _invoices.InsertAsync(second, autoSave: true);
            });
        });

        Assert.NotNull(error);
        Assert.Contains("UNIQUE", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BuildingManagementInvoices", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task INVDB06_Source_Reading_SoftDelete_Does_Not_Erase_Draft_Evidence()
    {
        var fixture = await SeedAsync();
        var physicalReadId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _readings.InsertAsync(new MeterReading(
                physicalReadId, fixture.ElectricityMeterId,
                2026, 10, 10m, 20m,
                new DateTime(2026, 10, 31)), autoSave: true);
        });

        var invoice = CreateDraft(fixture, physicalReadId, Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(invoice, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            await _readings.DeleteAsync(physicalReadId, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _invoices.GetQueryableAsync();
            var stored = await query.Include(x => x.Items)
                .ThenInclude(x => x.MeterSources)
                .SingleAsync(x => x.Id == invoice.Id);
            var electric = stored.Items.Single(x =>
                x.UtilityType == UtilityType.Electricity);
            var evidence = Assert.Single(electric.MeterSources);

            Assert.Equal(physicalReadId, evidence.SourceId);
            Assert.Equal(10m, evidence.PreviousReading);
            Assert.Equal(20m, evidence.CurrentReading);
            Assert.Equal(10m, evidence.Quantity);
            Assert.Equal(3500m, electric.UnitPrice);
            Assert.Equal(35000m, electric.ProvisionalAmount);
        });
    }

    [Fact]
    public async Task INVDB07_Internal_Freshness_Service_Accepts_Unchanged_Draft_Then_Flags_Rate_Edit()
    {
        var (fixture, _) = await SeedReadyOctoberAsync();
        var drafts = GetRequiredService<IInvoiceDraftStorageAppService>();
        var saved = await drafts.CreateAsync(new CreateInvoiceDraftDto
        {
            ContractId = fixture.ContractId,
            BillingYear = 2026,
            BillingMonth = 10
        });

        Assert.Equal(110000m, saved.ProvisionalUtilityTotal);
        Assert.True(await drafts.HasCurrentSourceSnapshotAsync(saved.Id));

        await WithUnitOfWorkAsync(async () =>
        {
            var rate = await _rates.GetAsync(fixture.ElectricityRateId);
            rate.Update(rate.BuildingId, rate.UtilityType, 7000m,
                rate.EffectiveFrom, rate.EffectiveTo, rate.Notes);
            await _rates.UpdateAsync(rate, autoSave: true);
        });

        Assert.False(await drafts.HasCurrentSourceSnapshotAsync(saved.Id));
        var stored = await drafts.GetAsync(saved.Id);
        Assert.Equal(3500m, stored.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity).UnitPrice);
        Assert.Equal(110000m, stored.ProvisionalUtilityTotal);
        Assert.False(stored.IsIssued);
        Assert.False(stored.HasRecognizedDebt);
    }

    [Fact]
    public async Task INVDB08_Internal_Freshness_Service_Flags_Edited_Meter_Reading()
    {
        var (fixture, electricityReadingId) = await SeedReadyOctoberAsync();
        var drafts = GetRequiredService<IInvoiceDraftStorageAppService>();
        var saved = await drafts.CreateAsync(new CreateInvoiceDraftDto
        {
            ContractId = fixture.ContractId,
            BillingYear = 2026,
            BillingMonth = 10
        });
        Assert.True(await drafts.HasCurrentSourceSnapshotAsync(saved.Id));

        await WithUnitOfWorkAsync(async () =>
        {
            var reading = await _readings.GetAsync(electricityReadingId);
            reading.Update(30m, new DateTime(2026, 10, 31), "corrected");
            await _readings.UpdateAsync(reading, autoSave: true);
        });

        Assert.False(await drafts.HasCurrentSourceSnapshotAsync(saved.Id));
        var stored = await drafts.GetAsync(saved.Id);
        var electric = stored.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity);
        Assert.Equal(10m, electric.Quantity);
        Assert.Equal(35000m, electric.ProvisionalAmount);
        Assert.False(stored.IsIssued);
    }

    [Fact]
    public async Task INVDB09_Revision_Preview_Is_ReadOnly_And_Requires_Changed_Tariff()
    {
        var (fixture, _) = await SeedReadyOctoberAsync();
        var service = GetRequiredService<IInvoiceDraftStorageAppService>();
        var saved = await service.CreateAsync(new CreateInvoiceDraftDto
        {
            ContractId = fixture.ContractId,
            BillingYear = 2026,
            BillingMonth = 10
        });

        var unchanged = await service.PreviewRebuildAsync(
            saved.Id, "Operator verified October rate correction");
        Assert.Equal(InvoiceDraftRebuildGateStatus.AlreadyCurrent, unchanged.Status);

        await WithUnitOfWorkAsync(async () =>
        {
            var rate = await _rates.GetAsync(fixture.ElectricityRateId);
            rate.Update(rate.BuildingId, rate.UtilityType, 7000m,
                rate.EffectiveFrom, rate.EffectiveTo, rate.Notes);
            await _rates.UpdateAsync(rate, autoSave: true);
        });

        var rejectedReason = await service.PreviewRebuildAsync(saved.Id, null);
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReasonRequired,
            rejectedReason.Status);

        var ready = await service.PreviewRebuildAsync(
            saved.Id, "  October electricity rate corrected by operator  ");
        Assert.True(ready.CanPrepareRevision);
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReadyForRevisionDesign, ready.Status);
        Assert.Equal(145000m, ready.ProposedUtilityTotal);
        Assert.Equal("October electricity rate corrected by operator",
            ready.NormalizedReason);

        // A PREVIEW must not replace any stored row/source evidence.
        var persisted = await service.GetAsync(saved.Id);
        Assert.Equal(110000m, persisted.ProvisionalUtilityTotal);
        Assert.Equal(3500m, persisted.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity).UnitPrice);
        Assert.False(persisted.IsIssued);
        Assert.False(persisted.HasRecognizedDebt);
    }

    [Fact]
    public async Task INVDB10_Revision_Preview_Blocks_Deleted_Meter_History()
    {
        var (fixture, electricityReading) = await SeedReadyOctoberAsync();
        var service = GetRequiredService<IInvoiceDraftStorageAppService>();
        var saved = await service.CreateAsync(new CreateInvoiceDraftDto
        {
            ContractId = fixture.ContractId,
            BillingYear = 2026,
            BillingMonth = 10
        });

        await WithUnitOfWorkAsync(async () =>
            await _readings.DeleteAsync(electricityReading, autoSave: true));

        var readiness = await service.PreviewRebuildAsync(
            saved.Id, "Operator requests a revised invoice draft");
        Assert.False(readiness.CanPrepareRevision);
        Assert.Equal(InvoiceDraftRebuildGateStatus.CurrentCandidateNotReady,
            readiness.Status);

        var persisted = await service.GetAsync(saved.Id);
        Assert.Equal(110000m, persisted.ProvisionalUtilityTotal);
        Assert.Equal(10m, persisted.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity).Quantity);
    }

    private static InvoiceDraftRevisionRecord MakeArchive(
        Invoice invoice, int revision = 1)
    {
        // Reconstruct the PREVIOUS historical evidence from the
        // immutable draft graph, not from a fresh current candidate.
        var old = InvoiceDraftSnapshot.Capture(
            invoice.ContractId, invoice.RoomId,
            invoice.BillingYear, invoice.BillingMonth,
            invoice.Items.Select(item => new InvoiceDraftUtilityLineSnapshot(
                item.UtilityType, item.UtilityRateId, item.RateStatus,
                item.RecordedQuantity, item.UnitPrice,
                item.ProvisionalAmount,
                item.MeterSources.Select(source =>
                    new InvoiceDraftMeterSegmentSnapshot(
                        source.SourceId, source.UtilityMeterId,
                        source.SourceType, source.RecordedDate,
                        source.PreviousReading, source.CurrentReading)),
                JsonSerializer.Deserialize<string[]>(item.CoverageIssuesJson)
                    ?? Array.Empty<string>())));
        var evidence = InvoiceDraftRevisionEvidence.Capture(
            invoice.Id, revision, Guid.NewGuid(),
            new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc),
            "Historical revision preserved for accounting audit", old);
        return InvoiceDraftRevisionRecord.Prepare(Guid.NewGuid(), evidence);
    }

    [Fact]
    public async Task INVDB11_Staged_Revision_Ledger_Persists_And_Rehydrates_Old_Evidence()
    {
        var fixture = await SeedAsync();
        var invoice = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        var history = MakeArchive(invoice);
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(invoice, autoSave: true);
            await _revisions.InsertAsync(history, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var stored = await _revisions.GetAsync(history.Id);
            var old = stored.VerifyAndRead();
            Assert.Equal(invoice.Id, old.InvoiceId);
            Assert.Equal(1, old.ArchivedRevision);
            Assert.Equal(invoice.ContractId, old.PreviousDraft.ContractId);
            Assert.Equal(110000m, old.PreviousDraft.ProvisionalTotal);
            Assert.Equal(2, old.PreviousDraft.UtilityLines.Count);
            Assert.Equal(64, stored.EvidenceSha256.Length);

            var current = await _invoices.GetAsync(invoice.Id);
            Assert.Equal(110000m, current.ProvisionalUtilityTotal);
            Assert.Equal(InvoiceStatus.Draft, current.Status);
        });
    }

    [Fact]
    public async Task INVDB12_Database_Rejects_Duplicate_Archived_Revision_Number()
    {
        var fixture = await SeedAsync();
        var invoice = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(invoice, autoSave: true);
            await _revisions.InsertAsync(MakeArchive(invoice), autoSave: true);
        });

        var rejection = await Record.ExceptionAsync(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
                await _revisions.InsertAsync(
                    MakeArchive(invoice), autoSave: true));
        });
        Assert.NotNull(rejection);
        Assert.Contains("UNIQUE", rejection.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task INVDB13_Staged_Ledger_Accepts_Sequential_Revisions_Only_Once()
    {
        var fixture = await SeedAsync();
        var invoice = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(invoice, autoSave: true);
            await _revisions.InsertAsync(MakeArchive(invoice, 1), autoSave: true);
            await _revisions.InsertAsync(MakeArchive(invoice, 2), autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var revisions = await _revisions.GetListAsync(x =>
                x.InvoiceId == invoice.Id);
            Assert.Equal(new[] { 1, 2 },
                revisions.Select(x => x.ArchivedRevision).Order().ToArray());
            Assert.All(revisions, x => Assert.Equal(invoice.Id,
                x.VerifyAndRead().InvoiceId));
        });
    }

    [Fact]
    public async Task FINALBILLDB01_Approved_Quote_Loads_Persisted_Meter_Evidence()
    {
        var (data, readingId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(data, readingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, data.ContractId,
                data.RoomId, new DateOnly(2026, 10, 31), "Final month");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var service = new FinalMoveOutBillReviewAppService(
                GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
                _contracts, _invoices,
                GetRequiredService<IRepository<InvoiceItem, Guid>>(),
                GetRequiredService<IRepository<InvoiceItemMeterSource, Guid>>());

            var review = await service.GetAsync(requestId);
            Assert.Equal(3_000_000m, review.FullMonthRent);
            Assert.Equal(35_000m, review.ElectricityAmount);
            Assert.Equal(75_000m, review.WaterAmount);
            Assert.Equal(3_110_000m, review.ProvisionalRentAndUtilities);
            Assert.Equal(draft.Id, review.UtilityDraftId);
            Assert.False(review.CanIssue);
            Assert.False(review.IsReceivablePosted);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
            Assert.Equal(ContractStatus.Active,
                (await _contracts.GetAsync(data.ContractId)).Status);
        });
    }

    [Fact]
    public async Task FINALBILLDB02_Prior_Month_Draft_Is_Not_Final_Bill()
    {
        var (data, readingId) = await SeedReadyOctoberAsync();
        var priorMonth = CreateDraft(data, readingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, data.ContractId,
                data.RoomId, new DateOnly(2026, 11, 30), "Final month");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(priorMonth, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var service = new FinalMoveOutBillReviewAppService(
                GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
                _contracts, _invoices,
                GetRequiredService<IRepository<InvoiceItem, Guid>>(),
                GetRequiredService<IRepository<InvoiceItemMeterSource, Guid>>());
            var review = await service.GetAsync(requestId);
            Assert.Equal(3_000_000m, review.FullMonthRent);
            Assert.Null(review.ProvisionalRentAndUtilities);
            Assert.Contains("FinalUtilityDraftMissing", review.Blockers);
            Assert.False(review.CanIssue);
        });
    }

    [Fact]
    public async Task FINPAYDB01_Final_Draft_With_Real_Payment_Is_Not_Settled_Debt()
    {
        var (data, readingId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(data, readingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, data.ContractId,
                data.RoomId, new DateOnly(2026, 10, 31), "Payment evidence");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);

            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Test Payer", "0900000111", null,
                    null, null, IdentityType.CitizenId,
                    Guid.NewGuid().ToString("N")), autoSave: true);

            var receipt = new Payment(Guid.NewGuid(),
                "FINPAYDB-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.InvoicePayment, PaymentMethod.Cash,
                PaymentChannel.Manual, 50000m, null, data.ContractId, draft.Id);
            receipt.Complete(DateTime.UtcNow, "FINPAYDB-" + Guid.NewGuid().ToString("N"));
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(receipt, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var bill = new FinalMoveOutBillReviewAppService(
                GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
                _contracts, _invoices,
                GetRequiredService<IRepository<InvoiceItem, Guid>>(),
                GetRequiredService<IRepository<InvoiceItemMeterSource, Guid>>());
            var service = new FinalMoveOutPaymentEvidenceAppService(
                bill,
                GetRequiredService<IRepository<Payment, Guid>>(),
                GetRequiredService<IDataFilter<ISoftDelete>>());

            var evidence = await service.GetAsync(requestId);
            Assert.Equal(draft.Id, evidence.DraftInvoiceId);
            Assert.Equal(50000m, evidence.ObservedCompletedReceiptAmount);
            Assert.Contains("CompletedPaymentLinkedToUnissuedDraft", evidence.Blockers);
            Assert.Null(evidence.OutstandingDebt);
            Assert.False(evidence.IsDebtReconciled);
            Assert.False(evidence.CanSettleDeposit);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
            Assert.Equal(ContractStatus.Active,
                (await _contracts.GetAsync(data.ContractId)).Status);
        });
    }

    [Fact]
    public async Task FINALPACKAGEDB01_Captures_Persisted_Utility_Source_Graph()
    {
        var (fixture, electricitySourceId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(fixture, electricitySourceId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 10, 31), "Final invoice evidence");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var service = MakeFinalEvidenceService();
            var snapshot = await service.GetAsync(requestId);
            Assert.Equal(draft.Id, snapshot.DraftInvoiceId);
            Assert.Equal(fixture.ContractId, snapshot.ContractId);
            Assert.Equal(3_000_000m, snapshot.FullMonthRent);
            Assert.Equal(3_110_000m, snapshot.KnownRentAndUtilities);
            Assert.Equal(2, snapshot.Utilities.Count);
            Assert.Equal(64, snapshot.EvidenceSha256.Length);
            Assert.True(snapshot.VerifyChecksum());
            Assert.Contains(snapshot.Utilities.SelectMany(x => x.Sources),
                s => s.SourceId == electricitySourceId);
            Assert.False(snapshot.CanIssue);
            Assert.False(snapshot.IsReceivablePosted);

            var stored = await _invoices.GetAsync(draft.Id);
            Assert.Equal(InvoiceStatus.Draft, stored.Status);
        });
    }

    [Fact]
    public async Task FINALPACKAGEDB02_Prior_Period_Cannot_Be_Mistaken_For_Final()
    {
        var (fixture, electricitySourceId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(fixture, electricitySourceId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 11, 30), "Final invoice evidence");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var error = await Assert.ThrowsAsync<Volo.Abp.BusinessException>(
                () => MakeFinalEvidenceService().GetAsync(requestId));
            Assert.Equal(InvoiceDraftErrorCodes.CandidateNotReady, error.Code);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ISSUEDRIFTDB01_Staged_Evidence_Detects_Current_Draft_Source_Change(
        bool inconsistentReadings)
    {
        var (fixture, readingId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(fixture, readingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();
        var sourceItemId = draft.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity).MeterSources.Single().Id;

        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 10, 31),
                "Staged proof drift test");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var bundle = await MakeIssuePreflightService()
                .GetEvidenceBundleAsync(requestId);
            var evidence = Assert.IsType<FinalInvoiceEvidencePackage>(
                bundle.Evidence);
            var archive = FinalInvoiceIssueEvidenceRecord.Prepare(
                archiveId, Guid.NewGuid(), DateTime.UtcNow,
                evidence, bundle.Preflight);
            await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .InsertAsync(archive, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var audit = await MakeIssueDriftService().GetAsync(archiveId);
            Assert.True(audit.MatchesCurrentDraft);
            Assert.False(audit.CanIssue);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var repository = GetRequiredService<
                IRepository<InvoiceItemMeterSource, Guid>>();
            var source = await repository.GetAsync(sourceItemId);
            if (inconsistentReadings)
            {
                typeof(InvoiceItemMeterSource)
                    .GetProperty(nameof(InvoiceItemMeterSource.CurrentReading))!
                    .SetValue(source, source.CurrentReading + 1m);
            }
            else
            {
                // Changed historical source date, same quantity and total:
                // valid meter arithmetic, but evidence digest must differ.
                typeof(InvoiceItemMeterSource)
                    .GetProperty(nameof(InvoiceItemMeterSource.RecordedDate))!
                    .SetValue(source, new DateTime(2026, 10, 30));
            }
            await repository.UpdateAsync(source, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var audit = await MakeIssueDriftService().GetAsync(archiveId);
            Assert.False(audit.MatchesCurrentDraft);
            Assert.Contains(inconsistentReadings
                    ? "CurrentDraftEvidenceInvalid"
                    : "ArchivedEvidenceDiffersFromCurrentDraft",
                audit.Blockers);
            Assert.False(audit.CanIssue);
            Assert.False(audit.IsReceivablePosted);
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(archiveId);
            archive.VerifyStoredEvidence();
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    private FinalInvoiceIssueEvidenceDriftAppService MakeIssueDriftService()
        => new(
            GetRequiredService<IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>(),
            GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
            _contracts,
            _invoices,
            GetRequiredService<IRepository<InvoiceItem, Guid>>(),
            GetRequiredService<IRepository<InvoiceItemMeterSource, Guid>>());

    [Fact]
    public async Task ALLOCREVIEWDB01_Completed_Invoice_Payment_Without_Journal_Is_Found()
    {
        var data = await SeedAllocationReviewAsync(withJournal: false);

        await WithUnitOfWorkAsync(async () =>
        {
            var result = await MakeAllocationReviewService().GetAsync(data.ContractId);
            Assert.Contains("CompletedInvoicePaymentUnjournaled", result.Blockers);
            Assert.Null(result.VerifiedStagedAmount);
            Assert.False(result.CanReduceDebt);
            Assert.False(result.CanPost);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task ALLOCREVIEWDB02_SoftDeleted_Receipt_Is_Still_Audited()
    {
        var data = await SeedAllocationReviewAsync(withJournal: true);

        await WithUnitOfWorkAsync(async () =>
        {
            var before = await MakeAllocationReviewService().GetAsync(data.ContractId);
            Assert.Equal(250000m, before.VerifiedStagedAmount);
            Assert.DoesNotContain("AllocationSourceReceiptMismatch", before.Blockers);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            await GetRequiredService<IRepository<Payment, Guid>>()
                .DeleteAsync(data.PaymentId, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var after = await MakeAllocationReviewService().GetAsync(data.ContractId);
            Assert.Contains("AllocationSourceReceiptMismatch", after.Blockers);
            Assert.Null(after.VerifiedStagedAmount);
            Assert.False(after.CanReduceDebt);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task ALLOCREVIEWDB03_CrossContract_Journal_Claim_Is_Not_Hidden()
    {
        var data = await SeedAllocationReviewAsync(withJournal: true);

        await WithUnitOfWorkAsync(async () =>
        {
            var journalRepository = GetRequiredService<
                IRepository<InvoicePaymentAllocationJournalEntry, Guid>>();
            var journal = await journalRepository.GetAsync(data.JournalId!.Value);
            typeof(InvoicePaymentAllocationJournalEntry)
                .GetProperty(nameof(InvoicePaymentAllocationJournalEntry.ContractId))!
                .SetValue(journal, Guid.NewGuid());
            await journalRepository.UpdateAsync(journal, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var audit = await MakeAllocationReviewService().GetAsync(data.ContractId);
            Assert.Contains("CrossContractAllocationInLedger", audit.Blockers);
            Assert.Contains("AllocationSourceReceiptMismatch", audit.Blockers);
            Assert.Null(audit.VerifiedStagedAmount);
            Assert.False(audit.CanSettleDeposit);
        });
    }

    private InvoicePaymentAllocationLedgerReviewAppService MakeAllocationReviewService()
        => new(
            GetRequiredService<IRepository<InvoicePaymentAllocationJournalEntry, Guid>>(),
            GetRequiredService<IRepository<Payment, Guid>>(),
            GetRequiredService<IDataFilter<ISoftDelete>>());

    private async Task<(Guid ContractId, Guid DraftId, Guid PaymentId, Guid? JournalId)>
        SeedAllocationReviewAsync(bool withJournal)
    {
        var fixture = await SeedAsync();
        var draft = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        Guid? journalId = withJournal ? Guid.NewGuid() : null;

        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(draft, autoSave: true);
            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Invoice Allocation Reviewer",
                    "0900000165", null, null, null,
                    IdentityType.CitizenId, Guid.NewGuid().ToString("N")),
                autoSave: true);

            var payment = new Payment(paymentId,
                "ALLOC-REVIEW-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.InvoicePayment, PaymentMethod.BankTransfer,
                PaymentChannel.Manual, 250000m,
                contractId: fixture.ContractId, invoiceId: draft.Id);
            payment.Complete(DateTime.UtcNow);
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(payment, autoSave: true);

            if (journalId.HasValue)
            {
                var journal = InvoicePaymentAllocationJournalEntry
                    .StageForValidation(journalId.Value, payment, draft.Id,
                        Guid.NewGuid(), DateTime.UtcNow);
                await GetRequiredService<
                    IRepository<InvoicePaymentAllocationJournalEntry, Guid>>()
                    .InsertAsync(journal, autoSave: true);
            }
        });

        return (fixture.ContractId, draft.Id, paymentId, journalId);
    }

    [Fact]
    public async Task ALLOCJOURNALDB01_Staged_Receipt_Attribution_Persists_But_Does_Not_Reduce_Debt()
    {
        var fixture = await SeedAsync();
        var draft = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var journalId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(draft, autoSave: true);
            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Allocation Ledger Payer",
                    "0900000153", null, null, null,
                    IdentityType.CitizenId, Guid.NewGuid().ToString("N")),
                autoSave: true);

            // Synthetic legacy receipt attributed to an unissued draft.
            // Persisted journal is test-only and NEVER recognizes payment.
            var receipt = new Payment(paymentId,
                "ALLOC-DB-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.InvoicePayment, PaymentMethod.BankTransfer,
                PaymentChannel.Manual, 250000m,
                contractId: fixture.ContractId, invoiceId: draft.Id);
            receipt.Complete(DateTime.UtcNow,
                "TRACE-ALLOC-DB-" + Guid.NewGuid().ToString("N"));
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(receipt, autoSave: true);
            var staged = InvoicePaymentAllocationJournalEntry.StageForValidation(
                journalId, receipt, draft.Id, Guid.NewGuid(), DateTime.UtcNow);
            await GetRequiredService<
                IRepository<InvoicePaymentAllocationJournalEntry, Guid>>()
                .InsertAsync(staged, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var receipt = await GetRequiredService<IRepository<Payment, Guid>>()
                .GetAsync(paymentId);
            var staged = await GetRequiredService<
                IRepository<InvoicePaymentAllocationJournalEntry, Guid>>()
                .GetAsync(journalId);
            staged.VerifySourceReceipt(receipt);
            var audit = InvoicePaymentAllocationJournalAuditor.Assess(
                new[] { staged }, new[] { receipt });
            Assert.Equal(1, audit.VerifiedStagedEntryCount);
            Assert.Equal(250000m, audit.VerifiedStagedAmount);
            Assert.False(audit.CanPost);
            Assert.False(audit.CanReduceDebt);
            Assert.False(audit.CanSettleDeposit);
            Assert.False(staged.IsRecognizedAllocation);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    [Fact]
    public async Task ALLOCJOURNALDB02_Second_Entry_For_Same_Payment_Is_Denied_By_Database()
    {
        var fixture = await SeedAsync();
        var draft = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        var tenantId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(draft, autoSave: true);
            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Duplicate Allocation Payer",
                    "0900000154", null, null, null,
                    IdentityType.CitizenId, Guid.NewGuid().ToString("N")),
                autoSave: true);

            var receipt = new Payment(paymentId,
                "ALLOC-DUP-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.InvoicePayment, PaymentMethod.BankTransfer,
                PaymentChannel.Manual, 250000m,
                contractId: fixture.ContractId, invoiceId: draft.Id);
            receipt.Complete(DateTime.UtcNow);
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(receipt, autoSave: true);
            await GetRequiredService<
                IRepository<InvoicePaymentAllocationJournalEntry, Guid>>()
                .InsertAsync(
                    InvoicePaymentAllocationJournalEntry.StageForValidation(
                        Guid.NewGuid(), receipt, draft.Id,
                        Guid.NewGuid(), DateTime.UtcNow), autoSave: true);
        });

        var error = await Record.ExceptionAsync(async () =>
            await WithUnitOfWorkAsync(async () =>
            {
                var receipt =
                    await GetRequiredService<IRepository<Payment, Guid>>()
                        .GetAsync(paymentId);
                await GetRequiredService<
                    IRepository<InvoicePaymentAllocationJournalEntry, Guid>>()
                    .InsertAsync(
                        InvoicePaymentAllocationJournalEntry.StageForValidation(
                            Guid.NewGuid(), receipt, draft.Id,
                            Guid.NewGuid(), DateTime.UtcNow),
                        autoSave: true);
            }));

        Assert.NotNull(error);
        Assert.Contains("UNIQUE", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        await WithUnitOfWorkAsync(async () =>
        {
            var all = await GetRequiredService<
                IRepository<InvoicePaymentAllocationJournalEntry, Guid>>()
                .GetListAsync(x => x.PaymentId == paymentId);
            Assert.Single(all);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    [Fact]
    public async Task ALLOCJOURNALDB03_Journal_Requires_Existing_Source_Receipt()
    {
        var fixture = await SeedAsync();
        var transient = new Payment(Guid.NewGuid(),
            "ALLOC-FK-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), PaymentPurpose.InvoicePayment,
            PaymentMethod.BankTransfer, PaymentChannel.Manual, 250000m,
            contractId: fixture.ContractId, invoiceId: Guid.NewGuid());
        transient.Complete(DateTime.UtcNow);
        var staged = InvoicePaymentAllocationJournalEntry.StageForValidation(
            Guid.NewGuid(), transient, transient.InvoiceId!.Value,
            Guid.NewGuid(), DateTime.UtcNow);

        var failure = await Record.ExceptionAsync(async () =>
            await WithUnitOfWorkAsync(async () =>
                await GetRequiredService<
                    IRepository<InvoicePaymentAllocationJournalEntry, Guid>>()
                    .InsertAsync(staged, autoSave: true)));

        Assert.NotNull(failure);
        Assert.Contains("FOREIGN KEY", failure.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ISSUEREADYDB04_Stored_Historical_Review_Is_Superseded_By_New_Facts(
        bool introduceInvoicePayment)
    {
        var data = await SeedIssuanceReviewAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var repository = GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>();
            var stored = await repository.GetAsync(data.ReviewId);
            // Seed a historically completed signoff to test readback.
            // Domain-level tests separately exercise real scope checks;
            // this EF setup does NOT authorize any issuance workflow.
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.Status))!
                .SetValue(stored, InvoiceIssuanceReviewStatus.ReviewedForDesign);
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedByUserId))!
                .SetValue(stored, Guid.NewGuid());
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedAtUtc))!
                .SetValue(stored, stored.SubmittedAtUtc.AddMinutes(1));
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewReason))!
                .SetValue(stored, "Evidence reviewed before subsequent changes.");
            await repository.UpdateAsync(stored, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var before = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.True(before.StagedEvidenceMatchesCurrentDraft);
            Assert.True(before.IndependentDesignReviewRecorded);
            Assert.True(before.IndependentReviewApplicableToCurrentDraft);
            Assert.False(before.CanIssue);
        });

        if (introduceInvoicePayment)
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var tenantId = Guid.NewGuid();
                await GetRequiredService<IRepository<Tenant, Guid>>()
                    .InsertAsync(new Tenant(
                        tenantId, "Invoice Review Drift Payer",
                        "0900000179", null, null, null,
                        IdentityType.CitizenId, Guid.NewGuid().ToString("N")),
                        autoSave: true);
                var payment = new Payment(
                    Guid.NewGuid(),
                    "DRIFT-PAY-" + Guid.NewGuid().ToString("N"),
                    tenantId, PaymentPurpose.InvoicePayment,
                    PaymentMethod.BankTransfer, PaymentChannel.Manual,
                    100000m, contractId: data.ContractId,
                    invoiceId: data.DraftId);
                payment.Complete(DateTime.UtcNow);
                await GetRequiredService<IRepository<Payment, Guid>>()
                    .InsertAsync(payment, autoSave: true);
            });
        }
        else
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var items = await GetRequiredService<
                    IRepository<InvoiceItem, Guid>>()
                    .GetListAsync(x => x.InvoiceId == data.DraftId);
                var electricityId = items.Single(x =>
                    x.UtilityType == UtilityType.Electricity).Id;
                var sources = GetRequiredService<
                    IRepository<InvoiceItemMeterSource, Guid>>();
                var source = (await sources.GetListAsync(x =>
                    x.InvoiceItemId == electricityId)).Single();
                typeof(InvoiceItemMeterSource)
                    .GetProperty(nameof(InvoiceItemMeterSource.RecordedDate))!
                    .SetValue(source, new DateTime(2026, 10, 30));
                await sources.UpdateAsync(source, autoSave: true);
            });
        }

        await WithUnitOfWorkAsync(async () =>
        {
            var after = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.True(after.IndependentDesignReviewRecorded);
            Assert.False(after.IndependentReviewApplicableToCurrentDraft);
            Assert.Contains(introduceInvoicePayment
                    ? "IndependentInvoiceReviewSupersededByNewAnomalies"
                    : "IndependentInvoiceReviewSupersededByDraftChange",
                after.Blockers);
            Assert.False(after.CanIssue);
            Assert.False(after.CanPostReceivable);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ISSUEREADYDB05_Historical_Review_Is_Superseded_When_Physical_Source_Changes(
        bool mutateRate)
    {
        var data = await SeedIssuanceReviewAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var repository = GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>();
            var ticket = await repository.GetAsync(data.ReviewId);
            // Rehydrate an already completed historical design review.
            // This test-only setup never grants issuance authorization.
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.Status))!
                .SetValue(ticket, InvoiceIssuanceReviewStatus.ReviewedForDesign);
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedByUserId))!
                .SetValue(ticket, Guid.NewGuid());
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedAtUtc))!
                .SetValue(ticket, ticket.SubmittedAtUtc.AddMinutes(1));
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewReason))!
                .SetValue(ticket, "Physical sources were reviewed before a change.");
            await repository.UpdateAsync(ticket, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var before = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.True(before.StagedEvidenceMatchesCurrentDraft);
            Assert.True(before.OriginalSourcesVerified);
            Assert.True(before.IndependentDesignReviewRecorded);
            Assert.True(before.IndependentReviewApplicableToCurrentDraft);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(data.ArchiveId);
            using var json = JsonDocument.Parse(archive.EvidenceJson);
            var electricity = json.RootElement.GetProperty("Utilities")[0];
            if (mutateRate)
            {
                var rateId = electricity.GetProperty("UtilityRateId").GetGuid();
                var rate = await _rates.GetAsync(rateId);
                rate.Update(rate.BuildingId, rate.UtilityType,
                    rate.UnitPrice + 750m, rate.EffectiveFrom, rate.EffectiveTo,
                    "Changed original tariff after design review");
                await _rates.UpdateAsync(rate, autoSave: true);
            }
            else
            {
                var readingId = electricity.GetProperty("Sources")[0]
                    .GetProperty("SourceId").GetGuid();
                var reading = await _readings.GetAsync(readingId);
                reading.Update(reading.CurrentReading + 1m,
                    reading.ReadingDate, "Changed reading after design review");
                await _readings.UpdateAsync(reading, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var after = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.True(after.StagedEvidenceMatchesCurrentDraft);
            Assert.True(after.IndependentDesignReviewRecorded);
            Assert.False(after.IndependentReviewApplicableToCurrentDraft);
            Assert.False(after.OriginalSourcesVerified);
            Assert.Contains(
                "IndependentInvoiceReviewSupersededBySourceChanges",
                after.Blockers);
            Assert.Contains(
                "OriginalInvoiceSourcesChangedOrIncomplete", after.Blockers);
            Assert.Contains(mutateRate
                    ? "OriginalSources:OriginalUtilityRateDiffersFromSnapshot"
                    : "OriginalSources:OriginalMeterReadingDiffersFromSnapshot",
                after.Blockers);
            Assert.False(after.CanIssue);
            Assert.False(after.CanPostReceivable);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task REPLACEMENTAUDITDB01_Original_Closing_And_New_Meter_Reading_Are_Verified()
    {
        var data = await SeedReplacementArchiveAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var source = await MakeSourceProvenanceService()
                .GetAsync(data.ArchiveId);
            Assert.True(source.MatchesPersistedSources);
            Assert.DoesNotContain("OriginalMeterReplacementMissing",
                source.Blockers);
            Assert.False(source.CanIssue);

            var ready = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.True(ready.OriginalSourcesVerified);
            Assert.True(ready.StagedEvidenceMatchesCurrentDraft);
            Assert.False(ready.CanIssue);
            Assert.False(ready.CanPostReceivable);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task REPLACEMENTAUDITDB02_Changed_Handover_Or_New_Opening_Is_Flagged(
        bool changeNewMeter)
    {
        var data = await SeedReplacementArchiveAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            if (changeNewMeter)
            {
                var meter = await _meters.GetAsync(data.NewMeterId);
                meter.Update(meter.MeterCode,
                    meter.InitialReading + 1m, meter.InstalledDate,
                    "Changed new meter initial reading");
                await _meters.UpdateAsync(meter, autoSave: true);
            }
            else
            {
                var repo = GetRequiredService<IRepository<MeterReplacement, Guid>>();
                var handover = await repo.GetAsync(data.ReplacementId);
                typeof(MeterReplacement)
                    .GetProperty(nameof(MeterReplacement.OldFinalReading))!
                    .SetValue(handover, handover.OldFinalReading + 1m);
                await repo.UpdateAsync(handover, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var source = await MakeSourceProvenanceService()
                .GetAsync(data.ArchiveId);
            Assert.False(source.MatchesPersistedSources);
            Assert.Contains(changeNewMeter
                    ? "ReplacementNewMeterOpeningMismatch"
                    : "OriginalMeterReplacementClosingDiffersFromSnapshot",
                source.Blockers);
            var ready = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.False(ready.OriginalSourcesVerified);
            Assert.Contains("OriginalInvoiceSourcesChangedOrIncomplete",
                ready.Blockers);
            Assert.False(ready.CanIssue);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    private async Task<(
        Guid ArchiveId, Guid RequestId, Guid DraftId,
        Guid ReplacementId, Guid NewMeterId)> SeedReplacementArchiveAsync()
    {
        var f = await SeedAsync();
        var replacementId = Guid.NewGuid();
        var newMeterId = Guid.NewGuid();
        var newReadingId = Guid.NewGuid();
        var waterReadingId = Guid.NewGuid();
        var replacementDate = new DateTime(2026, 10, 15);
        var monthEnd = new DateTime(2026, 10, 31);
        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await _contracts.GetAsync(f.ContractId);
            contract.Activate();
            await _contracts.UpdateAsync(contract, autoSave: true);

            // The filtered database UNIQUE index permits only one ACTIVE
            // electricity meter per room. A physical handover must retire
            // the old meter before inserting its replacement; retain the
            // historical old-meter row for source provenance verification.
            var oldMeter = await _meters.GetAsync(f.ElectricityMeterId);
            Assert.True(oldMeter.IsActive);
            oldMeter.Deactivate();
            await _meters.UpdateAsync(oldMeter, autoSave: true);

            await _meters.InsertAsync(new UtilityMeter(
                newMeterId, f.RoomId, UtilityType.Electricity,
                "NEW-" + Guid.NewGuid().ToString("N"),
                2m, replacementDate), autoSave: true);
            await GetRequiredService<IRepository<MeterReplacement, Guid>>()
                .InsertAsync(new MeterReplacement(
                    replacementId, f.ElectricityMeterId, newMeterId,
                    f.RoomId, UtilityType.Electricity,
                    replacementDate, 10m, 20m, 2m,
                    "Replacement with final closing proof"), autoSave: true);
            await _readings.InsertAsync(new MeterReading(
                newReadingId, newMeterId, 2026, 10,
                2m, 6m, monthEnd), autoSave: true);
            await _readings.InsertAsync(new MeterReading(
                waterReadingId, f.WaterMeterId, 2026, 10,
                100m, 105m, monthEnd), autoSave: true);
        });

        var snapshot = InvoiceDraftSnapshot.Capture(
            f.ContractId, f.RoomId, 2026, 10,
            new[]
            {
                new InvoiceDraftUtilityLineSnapshot(
                    UtilityType.Electricity, f.ElectricityRateId,
                    "SingleRateWholeMonth", 14m, 3500m, 49000m,
                    new[]
                    {
                        new InvoiceDraftMeterSegmentSnapshot(
                            replacementId, f.ElectricityMeterId,
                            MeterUsageSourceType.ReplacementClosing,
                            replacementDate, 10m, 20m),
                        new InvoiceDraftMeterSegmentSnapshot(
                            newReadingId, newMeterId,
                            MeterUsageSourceType.MonthlyReading,
                            monthEnd, 2m, 6m)
                    }, Array.Empty<string>()),
                new InvoiceDraftUtilityLineSnapshot(
                    UtilityType.Water, f.WaterRateId,
                    "SingleRateWholeMonth", 5m, 15000m, 75000m,
                    new[]
                    {
                        new InvoiceDraftMeterSegmentSnapshot(
                            waterReadingId, f.WaterMeterId,
                            MeterUsageSourceType.MonthlyReading,
                            monthEnd, 100m, 105m)
                    }, Array.Empty<string>())
            });
        var draft = Invoice.CreateDraft(Guid.NewGuid(), snapshot, Guid.NewGuid);
        var requestId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(
                requestId, f.ContractId, f.RoomId,
                new DateOnly(2026, 10, 31),
                "Final invoice with an in-month meter replacement");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });
        await WithUnitOfWorkAsync(async () =>
        {
            var bundle = await MakeIssuePreflightService()
                .GetEvidenceBundleAsync(requestId);
            var evidence = Assert.IsType<FinalInvoiceEvidencePackage>(
                bundle.Evidence);
            var archive = FinalInvoiceIssueEvidenceRecord.Prepare(
                archiveId, Guid.NewGuid(), DateTime.UtcNow,
                evidence, bundle.Preflight);
            await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .InsertAsync(archive, autoSave: true);
        });

        // The archived evidence must still refer to the deactivated
        // historical electricity meter and the newly active one. Verify
        // database constraints remain enforced in the test fixture.
        await WithUnitOfWorkAsync(async () =>
        {
            var electricityMeters = await _meters.GetListAsync(x =>
                x.RoomId == f.RoomId &&
                x.UtilityType == UtilityType.Electricity);
            Assert.Equal(2, electricityMeters.Count);
            Assert.False(electricityMeters.Single(x =>
                x.Id == f.ElectricityMeterId).IsActive);
            Assert.True(electricityMeters.Single(x =>
                x.Id == newMeterId).IsActive);
            Assert.Single(electricityMeters.Where(x => x.IsActive));
        });

        return (archiveId, requestId, draft.Id, replacementId, newMeterId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SOURCEAUDITDB01_Archived_Draft_Detects_Original_Reading_Or_Rate_Edit(
        bool updatePhysicalRate)
    {
        var data = await SeedIssuanceReviewAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var before = await MakeSourceProvenanceService().GetAsync(data.ArchiveId);
            Assert.True(before.MatchesPersistedSources);
            Assert.False(before.CanIssue);
            var readiness = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.True(readiness.OriginalSourcesVerified);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(data.ArchiveId);
            using var json = JsonDocument.Parse(archive.EvidenceJson);
            var electric = json.RootElement.GetProperty("Utilities")[0];
            if (updatePhysicalRate)
            {
                var rateId = electric.GetProperty("UtilityRateId").GetGuid();
                var rate = await _rates.GetAsync(rateId);
                rate.Update(rate.BuildingId, rate.UtilityType,
                    rate.UnitPrice + 750m, rate.EffectiveFrom, rate.EffectiveTo,
                    "Rate mutated after invoice evidence was staged");
                await _rates.UpdateAsync(rate, autoSave: true);
            }
            else
            {
                var readingId = electric.GetProperty("Sources")[0]
                    .GetProperty("SourceId").GetGuid();
                var reading = await _readings.GetAsync(readingId);
                reading.Update(reading.CurrentReading + 1m,
                    reading.ReadingDate, "Changed original reading");
                await _readings.UpdateAsync(reading, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var audit = await MakeSourceProvenanceService().GetAsync(data.ArchiveId);
            Assert.False(audit.MatchesPersistedSources);
            Assert.Contains(updatePhysicalRate
                    ? "OriginalUtilityRateDiffersFromSnapshot"
                    : "OriginalMeterReadingDiffersFromSnapshot",
                audit.Blockers);
            var ready = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.False(ready.OriginalSourcesVerified);
            Assert.Contains("OriginalInvoiceSourcesChangedOrIncomplete",
                ready.Blockers);
            Assert.True(ready.StagedEvidenceMatchesCurrentDraft);
            Assert.False(ready.CanIssue);
            Assert.False(ready.CanPostReceivable);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task SOURCEAUDITDB02_SoftDeleted_Source_Is_Still_Found_And_Flagged()
    {
        var data = await SeedIssuanceReviewAsync();
        Guid readingId = Guid.Empty;
        await WithUnitOfWorkAsync(async () =>
        {
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(data.ArchiveId);
            using var json = JsonDocument.Parse(archive.EvidenceJson);
            readingId = json.RootElement.GetProperty("Utilities")[0]
                .GetProperty("Sources")[0].GetProperty("SourceId").GetGuid();
            await _readings.DeleteAsync(readingId, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var audit = await MakeSourceProvenanceService().GetAsync(data.ArchiveId);
            Assert.False(audit.MatchesPersistedSources);
            Assert.Contains("OriginalMeterReadingSoftDeleted", audit.Blockers);
            Assert.DoesNotContain("OriginalMeterReadingMissing", audit.Blockers);
            var ready = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.Contains("OriginalSources:OriginalMeterReadingSoftDeleted",
                ready.Blockers);
            Assert.False(ready.OriginalSourcesVerified);
            Assert.False(ready.CanIssue);
        });
    }

    [Fact]
    public async Task ISSUEREADYDB01_Persisted_FinalInvoice_Report_Remains_Unissued()
    {
        var data = await SeedIssuanceReviewAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var report = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);

            Assert.Equal(data.DraftId, report.DraftInvoiceId);
            Assert.Equal(data.ArchiveId, report.StagedEvidenceId);
            Assert.True(report.StagedEvidenceMatchesCurrentDraft);
            Assert.False(report.IndependentDesignReviewRecorded);
            Assert.Contains("IndependentInvoiceReviewNotReady", report.Blockers);
            Assert.Contains("FinancialTerms:AuxiliaryFeeAssessmentNotProvided",
                report.Blockers);
            Assert.Contains("FinancialTerms:InvoiceDueDatePolicyNotProvided",
                report.Blockers);
            Assert.False(report.CanIssue);
            Assert.False(report.CanPostReceivable);
            Assert.False(report.CanSettleDeposit);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task ISSUEREADYDB02_Invoice_Staging_Becomes_Stale_After_Meter_Edit()
    {
        var data = await SeedIssuanceReviewAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var items = await GetRequiredService<IRepository<InvoiceItem, Guid>>()
                .GetListAsync(x => x.InvoiceId == data.DraftId);
            var electricityId = items.Single(x =>
                x.UtilityType == UtilityType.Electricity).Id;
            var sourceRepository = GetRequiredService<
                IRepository<InvoiceItemMeterSource, Guid>>();
            var source = (await sourceRepository.GetListAsync(x =>
                x.InvoiceItemId == electricityId)).Single();
            typeof(InvoiceItemMeterSource)
                .GetProperty(nameof(InvoiceItemMeterSource.RecordedDate))!
                .SetValue(source, new DateTime(2026, 10, 30));
            await sourceRepository.UpdateAsync(source, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var report = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);
            Assert.Contains("StagedInvoiceEvidenceNoLongerCurrent",
                report.Blockers);
            Assert.False(report.StagedEvidenceMatchesCurrentDraft);
            Assert.False(report.CanIssue);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task ISSUEREADYDB03_Unjournaled_Draft_Payment_Still_Blocks_Readiness()
    {
        var data = await SeedIssuanceReviewAsync();
        var tenantId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await GetRequiredService<IRepository<Tenant, Guid>>()
                .InsertAsync(new Tenant(
                    tenantId, "Release Readiness Payer",
                    "0900000169", null, null, null,
                    IdentityType.CitizenId, Guid.NewGuid().ToString("N")),
                    autoSave: true);
            var payment = new Payment(
                Guid.NewGuid(),
                "RELEASE-" + Guid.NewGuid().ToString("N"),
                tenantId, PaymentPurpose.InvoicePayment,
                PaymentMethod.BankTransfer, PaymentChannel.Manual, 100000m,
                contractId: data.ContractId, invoiceId: data.DraftId);
            payment.Complete(DateTime.UtcNow);
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(payment, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var report = await MakeReleaseReadinessService()
                .GetAsync(data.RequestId);

            Assert.Contains("Allocations:CompletedInvoicePaymentUnjournaled",
                report.Blockers);
            Assert.Contains("Preflight:Payment:CompletedPaymentLinkedToUnissuedDraft",
                report.Blockers);
            Assert.False(report.CanPostReceivable);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    private FinalInvoiceSourceProvenanceAppService MakeSourceProvenanceService()
        => new(
            GetRequiredService<IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>(),
            _rooms, _floors, _readings, _meters, _rates,
            GetRequiredService<IRepository<MeterReplacement, Guid>>(),
            GetRequiredService<IDataFilter<ISoftDelete>>());

    private FinalInvoiceReleaseReadinessAppService MakeReleaseReadinessService()
        => new(
            MakeIssuePreflightService(),
            GetRequiredService<IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>(),
            GetRequiredService<IRepository<InvoiceIssuanceReviewTicket, Guid>>(),
            MakeIssueDriftService(),
            MakeSourceProvenanceService(),
            MakeAllocationReviewService());

    [Fact]
    public async Task ISSUEREVIEWDB01_Submitted_Ticket_Persists_And_Draft_Remains_Unissued()
    {
        var data = await SeedIssuanceReviewAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            var review = await GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>()
                .GetAsync(data.ReviewId);
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(data.ArchiveId);
            review.VerifyStagedEvidence(archive);
            Assert.Equal(InvoiceIssuanceReviewStatus.Submitted, review.Status);
            Assert.Equal(data.DraftId, review.DraftInvoiceId);
            Assert.False(review.CanIssue);
            Assert.False(review.CanPostReceivable);
            Assert.False(review.CanReduceDebt);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task ISSUEREVIEWDB02_Database_Rejects_Second_Review_For_Same_Evidence()
    {
        var data = await SeedIssuanceReviewAsync();

        var exception = await Record.ExceptionAsync(async () =>
            await WithUnitOfWorkAsync(async () =>
            {
                var archive = await GetRequiredService<
                    IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                    .GetAsync(data.ArchiveId);
                var second = InvoiceIssuanceReviewTicket.Stage(
                    Guid.NewGuid(), archive, Guid.NewGuid(),
                    Guid.NewGuid(), DateTime.UtcNow,
                    "Competing review for the same evidence");
                await GetRequiredService<
                    IRepository<InvoiceIssuanceReviewTicket, Guid>>()
                    .InsertAsync(second, autoSave: true);
            }));
        Assert.NotNull(exception);
        Assert.Contains("UNIQUE", exception.ToString(),
            StringComparison.OrdinalIgnoreCase);

        await WithUnitOfWorkAsync(async () =>
        {
            var count = await GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>()
                .CountAsync(x => x.StagedEvidenceId == data.ArchiveId);
            Assert.Equal(1, count);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task ISSUEREVIEWDB03_Database_Rejects_Forged_Self_Review()
    {
        var data = await SeedIssuanceReviewAsync();

        var exception = await Record.ExceptionAsync(async () =>
            await WithUnitOfWorkAsync(async () =>
            {
                var repository = GetRequiredService<
                    IRepository<InvoiceIssuanceReviewTicket, Guid>>();
                var review = await repository.GetAsync(data.ReviewId);
                // Simulate bypassing domain workflow through privileged
                // manipulation of a materialized EF entity.
                typeof(InvoiceIssuanceReviewTicket)
                    .GetProperty(nameof(InvoiceIssuanceReviewTicket.Status))!
                    .SetValue(review, InvoiceIssuanceReviewStatus.ReviewedForDesign);
                typeof(InvoiceIssuanceReviewTicket)
                    .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedByUserId))!
                    .SetValue(review, data.SubmittedBy);
                typeof(InvoiceIssuanceReviewTicket)
                    .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedAtUtc))!
                    .SetValue(review, review.SubmittedAtUtc.AddMinutes(1));
                typeof(InvoiceIssuanceReviewTicket)
                    .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewReason))!
                    .SetValue(review, "Forged same-person approval is forbidden.");
                await repository.UpdateAsync(review, autoSave: true);
            }));
        Assert.NotNull(exception);
        Assert.Contains("CHECK", exception.ToString(),
            StringComparison.OrdinalIgnoreCase);

        await WithUnitOfWorkAsync(async () =>
        {
            var review = await GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>()
                .GetAsync(data.ReviewId);
            Assert.Equal(InvoiceIssuanceReviewStatus.Submitted, review.Status);
            Assert.Null(review.ReviewedByUserId);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(data.DraftId)).Status);
        });
    }

    [Fact]
    public async Task ISSUEREVIEWDB04_Rejects_Submission_Before_Evidence_Preparation()
    {
        var data = await SeedIssuanceReviewAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(data.ArchiveId);

            // SQLite may rehydrate UTC as Unspecified: force a valid
            // UTC input so the rejection specifically checks ordering.
            var backdatedUtc = DateTime.SpecifyKind(
                archive.PreparedAtUtc.AddMinutes(-1), DateTimeKind.Utc);
            Assert.Throws<ArgumentException>(() =>
                InvoiceIssuanceReviewTicket.Stage(
                    Guid.NewGuid(), archive, Guid.NewGuid(), Guid.NewGuid(),
                    backdatedUtc, "Backdated independent review attempt"));

            // A submission at the precise preparation instant is valid.
            var sameInstantUtc = DateTime.SpecifyKind(
                archive.PreparedAtUtc, DateTimeKind.Utc);
            var sameInstant = InvoiceIssuanceReviewTicket.Stage(
                Guid.NewGuid(), archive, Guid.NewGuid(), Guid.NewGuid(),
                sameInstantUtc, "Independent review begins at preparation");
            Assert.Equal(archive.Id, sameInstant.StagedEvidenceId);
        });
    }

    [Fact]
    public async Task ISSUEREVIEWDB05_Backdated_Stored_Submission_Fails_Integrity_Verification()
    {
        var data = await SeedIssuanceReviewAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(data.ArchiveId);
            var review = await GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>()
                .GetAsync(data.ReviewId);

            // Simulates an imported/privileged row that bypassed Stage().
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.SubmittedAtUtc))!
                .SetValue(review, archive.PreparedAtUtc.AddMinutes(-1));

            Assert.Throws<InvalidDataException>(() =>
                review.VerifyStagedEvidence(archive));
            Assert.False(review.CanIssue);
            Assert.False(review.CanPostReceivable);
        });
    }

    [Fact]
    public async Task ISSUERELEASEDB01_Candidate_Needs_Approved_Terms_Current_Source_And_Actor_Scope()
    {
        var seeded = await SeedIssuanceReviewAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var archives = GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>();
            var reviews = GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>();
            var archive = await archives.GetAsync(seeded.ArchiveId);
            var review = await reviews.GetAsync(seeded.ReviewId);
            var room = await _rooms.GetAsync(archive.RoomId);
            var floor = await _floors.GetAsync(room.FloorId);
            var building = floor.BuildingId;
            var reviewerId = Guid.NewGuid();
            var issuerId = Guid.NewGuid();

            // This is a disposable EF fixture. A real issuer must derive
            // these exact graph facts under Identity + Building row locks.
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.BuildingId))!
                .SetValue(review, building);
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.Status))!
                .SetValue(review, InvoiceIssuanceReviewStatus.ReviewedForDesign);
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedByUserId))!
                .SetValue(review, reviewerId);
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewedAtUtc))!
                .SetValue(review, review.SubmittedAtUtc.AddMinutes(1));
            typeof(InvoiceIssuanceReviewTicket)
                .GetProperty(nameof(InvoiceIssuanceReviewTicket.ReviewReason))!
                .SetValue(review, "Independent review of the archived billing sources");

            var preflight = await MakeIssuePreflightService().GetAsync(
                seeded.RequestId);
            var provenance = await MakeSourceProvenanceService().GetAsync(
                seeded.ArchiveId);
            var drift = await MakeIssueDriftService().GetAsync(
                seeded.ArchiveId);
            Assert.True(provenance.MatchesPersistedSources);
            Assert.True(drift.MatchesCurrentDraft,
                "Drift: " + string.Join(", ", drift.Blockers));
            var scope = new InvoiceIssuanceScopeFacts(
                AuthenticatedIssuerId: issuerId,
                IssuerPermissionGranted: true,
                RequestedMoveOutId: seeded.RequestId,
                PersistedMoveOutId: seeded.RequestId,
                RequestedDraftInvoiceId: seeded.DraftId,
                PersistedDraftInvoiceId: seeded.DraftId,
                InvoiceContractId: seeded.ContractId,
                InvoiceRoomId: archive.RoomId,
                ContractId: seeded.ContractId,
                ContractRoomId: archive.RoomId,
                RoomId: archive.RoomId,
                RoomFloorId: room.FloorId,
                FloorId: room.FloorId,
                FloorBuildingId: building,
                BuildingId: building,
                IssuerBuildingMemberships: new[] { building },
                PersistedInvoiceStatus: InvoiceStatus.Draft,
                PersistedEvidenceSha256: archive.EvidenceSha256,
                ExistingIssuedInvoiceForPeriod: false);
            var terms = new FinalInvoiceFinancialTermsProposal(
                seeded.DraftId, seeded.ContractId, archive.RoomId,
                archive.EvidenceSha256,
                new DateOnly(2026, 11, 5),
                "APPROVED-DUE-DATE-POLICY",
                AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
                "APPROVED-EXPLICIT-NO-FEES",
                reviewerId,
                Array.Empty<FinalInvoiceAuxiliaryChargeProposal>());
            // Use the persisted archival digest for terms and scope, not
            // the fresh snapshot timestamp-dependent preview digest.
            var anchoredPreflight = new FinalInvoiceIssuePreflight
            {
                MoveOutRequestId = preflight.MoveOutRequestId,
                ContractId = preflight.ContractId,
                RoomId = preflight.RoomId,
                UtilityDraftId = preflight.UtilityDraftId,
                KnownBaseChargeTotal = preflight.KnownBaseChargeTotal,
                BaseCharges = preflight.BaseCharges,
                EvidenceSha256 = archive.EvidenceSha256
            };
            anchoredPreflight.Blockers.AddRange(preflight.Blockers);
            var scopeReview = InvoiceIssuanceScopePolicy.Assess(
                anchoredPreflight, scope);
            var termsReview = FinalInvoiceFinancialTermsEvaluator.Assess(
                anchoredPreflight, terms);
            Assert.True(scopeReview.IsScopeConsistent,
                "Scope: " + string.Join(", ", scopeReview.Blockers));
            Assert.Equal(
                new[] { "FinancialTermsProposalIsNotPostingAuthorization" },
                termsReview.Blockers);
            Assert.Equal(seeded.DraftId, preflight.UtilityDraftId);
            Assert.Equal(archive.KnownBaseChargeTotal,
                preflight.KnownBaseChargeTotal);
            Assert.True(provenance.MatchesPersistedSources,
                "Sources: " + string.Join(", ", provenance.Blockers));
            var candidateId = Guid.NewGuid();
            var issueTime = DateTime.UtcNow.AddMinutes(10);
            var candidate = IssuedInvoiceLedgerRecord.BuildVerifiedCandidate(
                candidateId, "NT-FINAL-" + candidateId.ToString("N"),
                issuerId, issueTime, archive, review, preflight,
                scope, terms, drift, provenance);
            candidate.VerifyStoredTerms(archive, review);
            Assert.Equal(3_110_000m, candidate.BaseAmount);
            Assert.Equal(0m, candidate.AuxiliaryAmount);
            Assert.Equal(3_110_000m, candidate.TotalAmount);
            Assert.Equal(seeded.DraftId, candidate.DraftInvoiceId);
            Assert.Equal(building, candidate.BuildingId);
            Assert.Equal(64, candidate.FinancialTermsSha256.Length);

            Assert.Throws<InvalidOperationException>(() =>
                IssuedInvoiceLedgerRecord.BuildVerifiedCandidate(
                    Guid.NewGuid(), "SCOPE-DENIED", issuerId, issueTime,
                    archive, review, preflight,
                    scope with { IssuerPermissionGranted = false },
                    terms, drift, provenance));
            Assert.Throws<InvalidOperationException>(() =>
                IssuedInvoiceLedgerRecord.BuildVerifiedCandidate(
                    Guid.NewGuid(), "FEE-DENIED", issuerId, issueTime,
                    archive, review, preflight, scope,
                    terms with { AuxiliaryFeeDecision =
                        AuxiliaryFeeAssessmentDecision.NotReviewed },
                    drift, provenance));
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(seeded.DraftId)).Status);
        });
    }

    private async Task<(Guid DraftId, Guid ArchiveId, Guid ReviewId, Guid SubmittedBy,
        Guid RequestId, Guid ContractId)> SeedIssuanceReviewAsync()
    {
        var (fixture, readingId) = await SeedReadyOctoberAsync();
        Guid waterReadingId = Guid.Empty;
        await WithUnitOfWorkAsync(async () =>
        {
            var matched = await _readings.GetListAsync(x =>
                x.UtilityMeterId == fixture.WaterMeterId &&
                x.BillingYear == 2026 && x.BillingMonth == 10);
            waterReadingId = Assert.Single(matched).Id;
        });
        var draft = CreateDraft(fixture, readingId, waterReadingId);
        var requestId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var submittedBy = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var moveOut = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 10, 31),
                "Two-person staged issuance review");
            moveOut.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(moveOut, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var bundle = await MakeIssuePreflightService()
                .GetEvidenceBundleAsync(requestId);
            var source = Assert.IsType<FinalInvoiceEvidencePackage>(
                bundle.Evidence);
            var archive = FinalInvoiceIssueEvidenceRecord.Prepare(
                archiveId, Guid.NewGuid(), DateTime.UtcNow,
                source, bundle.Preflight);
            await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .InsertAsync(archive, autoSave: true);
            var review = InvoiceIssuanceReviewTicket.Stage(
                reviewId, archive, Guid.NewGuid(), submittedBy,
                DateTime.UtcNow, "Evidence prepared for independent review");
            await GetRequiredService<
                IRepository<InvoiceIssuanceReviewTicket, Guid>>()
                .InsertAsync(review, autoSave: true);
        });

        return (draft.Id, archiveId, reviewId, submittedBy,
            requestId, fixture.ContractId);
    }

    [Fact]
    public async Task ISSUEARCHDB01_Staged_Issue_Evidence_Persists_Without_Posting_Invoice()
    {
        var (fixture, electricityReadingId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(fixture, electricityReadingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 10, 31),
                "Staged issue evidence");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var bundle = await MakeIssuePreflightService()
                .GetEvidenceBundleAsync(requestId);
            var source = Assert.IsType<FinalInvoiceEvidencePackage>(
                bundle.Evidence);
            Assert.Equal(source.EvidenceSha256, bundle.Preflight.EvidenceSha256);
            var archive = FinalInvoiceIssueEvidenceRecord.Prepare(
                archiveId, Guid.NewGuid(), DateTime.UtcNow,
                source, bundle.Preflight);
            await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .InsertAsync(archive, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var archive = await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .GetAsync(archiveId);
            archive.VerifyStoredEvidence();
            Assert.Equal(requestId, archive.MoveOutRequestId);
            Assert.Equal(draft.Id, archive.DraftInvoiceId);
            Assert.Equal(fixture.ContractId, archive.ContractId);
            Assert.Equal(2026, archive.BillingYear);
            Assert.Equal(10, archive.BillingMonth);
            Assert.Equal(3_110_000m, archive.KnownBaseChargeTotal);
            Assert.False(archive.IsInvoiceIssued);
            Assert.False(archive.IsReceivablePosted);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    [Fact]
    public async Task ISSUEARCHDB02_Database_Rejects_Second_Archive_For_Same_Draft()
    {
        var (fixture, electricityReadingId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(fixture, electricityReadingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 10, 31),
                "Duplicate issue evidence guard");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        FinalInvoiceIssueEvidenceBundle bundle = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            bundle = await MakeIssuePreflightService()
                .GetEvidenceBundleAsync(requestId);
            var source = Assert.IsType<FinalInvoiceEvidencePackage>(
                bundle.Evidence);
            Assert.Equal(source.EvidenceSha256, bundle.Preflight.EvidenceSha256);
            var first = FinalInvoiceIssueEvidenceRecord.Prepare(
                Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
                source, bundle.Preflight);
            await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .InsertAsync(first, autoSave: true);
        });

        var failure = await Record.ExceptionAsync(async () =>
            await WithUnitOfWorkAsync(async () =>
            {
                var source = Assert.IsType<FinalInvoiceEvidencePackage>(
                    bundle.Evidence);
                var duplicate = FinalInvoiceIssueEvidenceRecord.Prepare(
                    Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
                    source, bundle.Preflight);
                await GetRequiredService<
                    IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                    .InsertAsync(duplicate, autoSave: true);
            }));
        Assert.NotNull(failure);
        Assert.Contains("UNIQUE", failure.ToString(),
            StringComparison.OrdinalIgnoreCase);
        await WithUnitOfWorkAsync(async () =>
        {
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    [Fact]
    public async Task ISSUEARCHDB03_Rehashed_Corrupt_Readings_Are_Denied_After_Database_Reload()
    {
        var (fixture, electricityReadingId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(fixture, electricityReadingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 10, 31),
                "Verify persisted meter evidence");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var bundle = await MakeIssuePreflightService()
                .GetEvidenceBundleAsync(requestId);
            var source = Assert.IsType<FinalInvoiceEvidencePackage>(
                bundle.Evidence);
            var record = FinalInvoiceIssueEvidenceRecord.Prepare(
                archiveId, Guid.NewGuid(), DateTime.UtcNow,
                source, bundle.Preflight);
            await GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>()
                .InsertAsync(record, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var repository = GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>();
            var stored = await repository.GetAsync(archiveId);
            stored.VerifyStoredEvidence();

            // Simulate a privileged/legacy UPDATE with a recomputed hash.
            // The staged SQLite table is NOT append-only; verification
            // must fail closed when it is subsequently read.
            var root = JsonNode.Parse(stored.EvidenceJson)!;
            root["Utilities"]![0]!["Sources"]![0]!["CurrentReading"] = 999999m;
            var modifiedJson = root.ToJsonString();
            var modifiedHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(modifiedJson))).ToLowerInvariant();
            typeof(FinalInvoiceIssueEvidenceRecord)
                .GetProperty(nameof(FinalInvoiceIssueEvidenceRecord.EvidenceJson))!
                .SetValue(stored, modifiedJson);
            typeof(FinalInvoiceIssueEvidenceRecord)
                .GetProperty(nameof(FinalInvoiceIssueEvidenceRecord.EvidenceSha256))!
                .SetValue(stored, modifiedHash);
            await repository.UpdateAsync(stored, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var repository = GetRequiredService<
                IRepository<FinalInvoiceIssueEvidenceRecord, Guid>>();
            var corrupted = await repository.GetAsync(archiveId);
            Assert.Throws<InvalidDataException>(corrupted.VerifyStoredEvidence);
            Assert.False(corrupted.IsInvoiceIssued);
            Assert.False(corrupted.IsReceivablePosted);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    [Fact]
    public async Task ISSUEGATEDB01_Preflight_Reads_Actual_Draft_And_Leaves_It_Unissued()
    {
        var (fixture, electricityReadingId) = await SeedReadyOctoberAsync();
        var draft = CreateDraft(fixture, electricityReadingId, Guid.NewGuid());
        var requestId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 10, 31), "Issuance preflight");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var preflight = await MakeIssuePreflightService().GetAsync(requestId);
            Assert.Equal(draft.Id, preflight.UtilityDraftId);
            Assert.Equal(3_110_000m, preflight.KnownBaseChargeTotal);
            Assert.Equal(3, preflight.BaseCharges.Count);
            Assert.Equal(64, preflight.EvidenceSha256!.Length);
            Assert.Contains("AuxiliaryFeePolicyUndecided", preflight.Blockers);
            Assert.Contains("AuditedPaymentAllocationNotImplemented", preflight.Blockers);
            Assert.False(preflight.CanIssue);
            Assert.False(preflight.IsDebtCalculated);
            Assert.Equal(InvoiceStatus.Draft,
                (await _invoices.GetAsync(draft.Id)).Status);
        });
    }

    [Fact]
    public async Task ISSUEGATEDB02_Pending_MoveOut_Has_No_Issuance_Charge_Lines()
    {
        var data = await SeedAsync();
        var requestId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(new MoveOutRequest(
                    requestId, data.ContractId, data.RoomId,
                    new DateOnly(2026, 11, 30), null), autoSave: true));

        await WithUnitOfWorkAsync(async () =>
        {
            var preflight = await MakeIssuePreflightService().GetAsync(requestId);
            Assert.Null(preflight.UtilityDraftId);
            Assert.Null(preflight.KnownBaseChargeTotal);
            Assert.Empty(preflight.BaseCharges);
            Assert.Contains("Bill:MoveOutNotApproved", preflight.Blockers);
            Assert.Contains("VerifiedFinalInvoiceEvidenceMissing", preflight.Blockers);
            Assert.False(preflight.CanIssue);
        });
    }

    private FinalInvoiceIssuePreflightAppService MakeIssuePreflightService()
    {
        var bill = new FinalMoveOutBillReviewAppService(
            GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
            _contracts, _invoices,
            GetRequiredService<IRepository<InvoiceItem, Guid>>(),
            GetRequiredService<IRepository<InvoiceItemMeterSource, Guid>>());
        var payments = new FinalMoveOutPaymentEvidenceAppService(
            bill, GetRequiredService<IRepository<Payment, Guid>>(),
            GetRequiredService<IDataFilter<ISoftDelete>>());
        return new FinalInvoiceIssuePreflightAppService(
            bill, MakeFinalEvidenceService(), payments);
    }

    private FinalInvoiceEvidencePackageAppService MakeFinalEvidenceService()
        => new(
            GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
            _contracts, _invoices,
            GetRequiredService<IRepository<InvoiceItem, Guid>>(),
            GetRequiredService<IRepository<InvoiceItemMeterSource, Guid>>(),
            GetRequiredService<Volo.Abp.Timing.IClock>());

    [Fact]
    public async Task DEPSTATEDB01_Refund_Disposition_Cannot_Be_Replayed_After_Reload()
    {
        var fixture = await SeedAsync();
        var tenantId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Deposit State Payer", "0900000199",
                    null, null, null, IdentityType.CitizenId,
                    Guid.NewGuid().ToString("N")), autoSave: true);

            var deposit = new Payment(paymentId,
                "DEPSTATE-DB-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
                PaymentChannel.Manual, 3000000m,
                contractId: fixture.ContractId);
            deposit.Complete(DateTime.UtcNow,
                "DEPSTATE-DB-" + Guid.NewGuid().ToString("N"));
            deposit.MarkDepositAsRefunded(); // Domain transition test ONLY.
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(deposit, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var stored = await GetRequiredService<IRepository<Payment, Guid>>()
                .GetAsync(paymentId);
            Assert.Equal(PaymentStatus.Completed, stored.Status);
            Assert.Equal(DepositDisposition.Refunded, stored.DepositDisposition);
            Assert.Throws<InvalidOperationException>(
                stored.MarkDepositAsRefunded);
            Assert.Throws<InvalidOperationException>(
                stored.MarkDepositAsForfeited);
            Assert.Throws<InvalidOperationException>(
                stored.MarkDepositAsAppliedToContract);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var stored = await GetRequiredService<IRepository<Payment, Guid>>()
                .GetAsync(paymentId);
            Assert.Equal(DepositDisposition.Refunded, stored.DepositDisposition);
            Assert.Equal(PaymentStatus.Completed, stored.Status);
        });
    }

    [Fact]
    public async Task PAYSTATEDB01_Completed_Deposit_Remains_Completed_After_Reload()
    {
        var data = await SeedAsync();
        var tenantId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "State Payer", "0900000137", null,
                    null, null, IdentityType.CitizenId,
                    Guid.NewGuid().ToString("N")), autoSave: true);

            var receipt = new Payment(paymentId,
                "PAYSTATE-DB-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
                PaymentChannel.Manual, 3000000m,
                contractId: data.ContractId);
            receipt.Complete(DateTime.UtcNow,
                "PAYSTATE-DB-" + Guid.NewGuid().ToString("N"));
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(receipt, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var stored = await GetRequiredService<IRepository<Payment, Guid>>()
                .GetAsync(paymentId);
            var completedAt = stored.PaidAt;
            Assert.Equal(PaymentStatus.Completed, stored.Status);
            Assert.Equal(DepositDisposition.Unapplied, stored.DepositDisposition);
            Assert.Throws<InvalidOperationException>(stored.Cancel);
            Assert.Throws<InvalidOperationException>(stored.Fail);
            Assert.Throws<InvalidOperationException>(() =>
                stored.Complete(DateTime.UtcNow));
            Assert.Equal(completedAt, stored.PaidAt);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var persisted = await GetRequiredService<IRepository<Payment, Guid>>()
                .GetAsync(paymentId);
            Assert.Equal(PaymentStatus.Completed, persisted.Status);
            Assert.Equal(DepositDisposition.Unapplied, persisted.DepositDisposition);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DEPLEDGERDB01_Reconciles_Completed_Allocation_And_Rejects_Duplicate(
        bool duplicate)
    {
        var fixture = await SeedAsync();
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await _contracts.GetAsync(fixture.ContractId);
            contract.Activate();
            await _contracts.UpdateAsync(contract, autoSave: true);

            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 11, 30), "Deposit audit");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);

            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Ledger Payer", "0900000124",
                    null, null, null, IdentityType.CitizenId,
                    Guid.NewGuid().ToString("N")), autoSave: true);
            var payment = new Payment(paymentId,
                "LEDGER-DB-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.ContractDeposit, PaymentMethod.Cash,
                PaymentChannel.Manual, 500000m,
                contractId: fixture.ContractId);
            payment.Complete(DateTime.UtcNow);
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(payment, autoSave: true);

            var journal = GetRequiredService<IRepository<DepositSettlement, Guid>>();
            var first = new DepositSettlement(Guid.NewGuid(),
                DepositSettlementType.AllocateToContractDeposit,
                duplicate ? 300000m : 500000m,
                paymentId: paymentId, targetContractId: fixture.ContractId);
            first.Complete(DateTime.UtcNow);
            await journal.InsertAsync(first, autoSave: true);
            if (duplicate)
            {
                var second = new DepositSettlement(Guid.NewGuid(),
                    DepositSettlementType.AllocateToContractDeposit, 300000m,
                    paymentId: paymentId, targetContractId: fixture.ContractId);
                second.Complete(DateTime.UtcNow);
                await journal.InsertAsync(second, autoSave: true);
            }
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var service = new ContractDepositLedgerReviewAppService(
                GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
                _contracts,
                GetRequiredService<IRepository<DepositSettlement, Guid>>(),
                GetRequiredService<IRepository<Payment, Guid>>(),
                GetRequiredService<IDataFilter<ISoftDelete>>());
            var result = await service.GetAsync(requestId);
            Assert.Null(result.RefundableBalance);
            Assert.False(result.CanRefund);
            Assert.False(result.CanOffsetDebt);
            Assert.Contains("DepositDeductionPolicyNotApproved", result.Blockers);
            if (duplicate)
            {
                Assert.Null(result.VerifiedIncomingAllocationTotal);
                Assert.Contains("DepositPaymentOverAllocatedOrCrossContract",
                    result.Blockers);
            }
            else
            {
                Assert.Equal(500000m, result.VerifiedIncomingAllocationTotal);
                Assert.Equal(1, result.CompletedIncomingCount);
            }
            var contract = await _contracts.GetAsync(fixture.ContractId);
            Assert.Equal(ContractStatus.Active, contract.Status);
        });
    }

    [Fact]
    public async Task DEPLEDGERDB03_Direct_Completed_Deposit_With_No_Journal_Cannot_Be_Hidden()
    {
        var fixture = await SeedAsync();
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await _contracts.GetAsync(fixture.ContractId);
            contract.Activate();
            await _contracts.UpdateAsync(contract, autoSave: true);

            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 11, 30),
                "Missing deposit allocation audit");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);

            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Missing Allocation Payer", "0900000151",
                    null, null, null, IdentityType.CitizenId,
                    Guid.NewGuid().ToString("N")), autoSave: true);

            var receipt = new Payment(Guid.NewGuid(),
                "DEP-ORPHAN-" + Guid.NewGuid().ToString("N"),
                tenantId, PaymentPurpose.ContractDeposit,
                PaymentMethod.Cash, PaymentChannel.Manual, 500000m,
                contractId: fixture.ContractId);
            receipt.Complete(DateTime.UtcNow);
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(receipt, autoSave: true);
            // Deliberately no DepositSettlement row: this cash must not
            // silently disappear from the contract reconciliation.
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reader = new ContractDepositLedgerReviewAppService(
                GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
                _contracts,
                GetRequiredService<IRepository<DepositSettlement, Guid>>(),
                GetRequiredService<IRepository<Payment, Guid>>(),
                GetRequiredService<IDataFilter<ISoftDelete>>());
            var audit = await reader.GetAsync(requestId);
            Assert.Equal(500000m,
                audit.ObservedCompletedContractDepositReceiptsTotal);
            Assert.Null(audit.VerifiedIncomingAllocationTotal);
            Assert.Contains("CompletedContractDepositNotFullyAllocated", audit.Blockers);
            Assert.False(audit.CanRefund);
            Assert.Null(audit.RefundableBalance);
            Assert.Equal(ContractStatus.Active,
                (await _contracts.GetAsync(fixture.ContractId)).Status);
        });
    }

    [Fact]
    public async Task DEPLEDGERDB02_Refund_And_Allocation_Cannot_Use_Same_Receipt_Twice()
    {
        var fixture = await SeedAsync();
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await _contracts.GetAsync(fixture.ContractId);
            contract.Activate();
            await _contracts.UpdateAsync(contract, autoSave: true);

            var request = new MoveOutRequest(requestId, fixture.ContractId,
                fixture.RoomId, new DateOnly(2026, 11, 30), "Double disposal audit");
            request.Approve(DateTime.UtcNow, null);
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);
            await GetRequiredService<IRepository<Tenant, Guid>>().InsertAsync(
                new Tenant(tenantId, "Double Disposition Payer", "0900000149",
                    null, null, null, IdentityType.CitizenId,
                    Guid.NewGuid().ToString("N")), autoSave: true);

            var receipt = new Payment(paymentId,
                "DEP-MIXED-DB-" + Guid.NewGuid().ToString("N"), tenantId,
                PaymentPurpose.ContractDeposit, PaymentMethod.Cash,
                PaymentChannel.Manual, 500000m,
                contractId: fixture.ContractId);
            receipt.Complete(DateTime.UtcNow);
            await GetRequiredService<IRepository<Payment, Guid>>()
                .InsertAsync(receipt, autoSave: true);

            var journal = GetRequiredService<IRepository<DepositSettlement, Guid>>();
            var allocation = new DepositSettlement(Guid.NewGuid(),
                DepositSettlementType.AllocateToContractDeposit, 500000m,
                paymentId: paymentId, targetContractId: fixture.ContractId);
            allocation.Complete(DateTime.UtcNow);
            await journal.InsertAsync(allocation, autoSave: true);

            var refund = new DepositSettlement(Guid.NewGuid(),
                DepositSettlementType.Refund, 500000m,
                paymentId: paymentId, sourceContractId: fixture.ContractId);
            refund.Complete(DateTime.UtcNow);
            await journal.InsertAsync(refund, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reader = new ContractDepositLedgerReviewAppService(
                GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
                _contracts,
                GetRequiredService<IRepository<DepositSettlement, Guid>>(),
                GetRequiredService<IRepository<Payment, Guid>>(),
                GetRequiredService<IDataFilter<ISoftDelete>>());
            var audit = await reader.GetAsync(requestId);
            Assert.Contains("DepositPaymentDoubleDisposed", audit.Blockers);
            Assert.Contains("MixedDepositDispositionRequiresReview", audit.Blockers);
            Assert.Null(audit.VerifiedIncomingAllocationTotal);
            Assert.Null(audit.RefundableBalance);
            Assert.False(audit.CanRefund);
            Assert.Equal(ContractStatus.Active,
                (await _contracts.GetAsync(fixture.ContractId)).Status);
        });
    }

    [Fact]
    public async Task FINPAYDB02_Pending_MoveOut_Has_No_Draft_Or_Assigned_Debt()
    {
        var data = await SeedAsync();
        var requestId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(new MoveOutRequest(requestId, data.ContractId,
                    data.RoomId, new DateOnly(2026, 11, 30), null),
                    autoSave: true));

        await WithUnitOfWorkAsync(async () =>
        {
            var bill = new FinalMoveOutBillReviewAppService(
                GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
                _contracts, _invoices,
                GetRequiredService<IRepository<InvoiceItem, Guid>>(),
                GetRequiredService<IRepository<InvoiceItemMeterSource, Guid>>());
            var service = new FinalMoveOutPaymentEvidenceAppService(
                bill, GetRequiredService<IRepository<Payment, Guid>>(),
                GetRequiredService<IDataFilter<ISoftDelete>>());

            var result = await service.GetAsync(requestId);
            Assert.Null(result.DraftInvoiceId);
            Assert.Null(result.OutstandingDebt);
            Assert.Null(result.ObservedCompletedReceiptAmount);
            Assert.False(result.IsDebtReconciled);
            Assert.Contains("NoVerifiedFinalBillDraft", result.Blockers);
            Assert.Contains("ActualOutstandingDebtUnknown", result.Blockers);
        });
    }

}
