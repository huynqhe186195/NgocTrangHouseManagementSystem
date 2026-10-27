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
