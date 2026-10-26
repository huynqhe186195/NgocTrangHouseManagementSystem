using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Tenants;
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

}
