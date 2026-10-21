using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.DepositSettlements;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Floors;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Timing;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

public sealed class RoomReservationTransaction_Tests
    : BuildingManagementApplicationTestBase<BuildingManagementPostgreSqlTestModule>
{
    private static long _tenantSequence = DateTime.UtcNow.Ticks % 100_000_000;

    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<DepositSettlement, Guid> _settlements;
    private readonly ITenantAppService _tenants;
    private readonly IRoomReservationAppService _reservations;
    private readonly IPaymentAppService _payments;
    private readonly IClock _clock;

    public RoomReservationTransaction_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _settlements = GetRequiredService<IRepository<DepositSettlement, Guid>>();
        _tenants = GetRequiredService<ITenantAppService>();
        _reservations = GetRequiredService<IRoomReservationAppService>();
        _payments = GetRequiredService<IPaymentAppService>();
        _clock = GetRequiredService<IClock>();
    }

    // PG01: Reservation cancellation and Forfeit are committed together.
    [Fact]
    public async Task PG01_Should_Cancel_And_Forfeit_500K()
    {
        var id = await CreatePaidReservationAsync(500_000m);

        await _reservations.CancelAsync(id, CustomerCancelled());

        var reservation = await _reservations.GetAsync(id);
        reservation.Status.ShouldBe(RoomReservationStatus.Cancelled);

        var records = await ReadSettlementsAsync(id);
        records.Count.ShouldBe(1);
        records.Single().Type.ShouldBe(DepositSettlementType.Forfeit);
        records.Single().Amount.ShouldBe(500_000m);
        records.Single().Status.ShouldBe(DepositSettlementStatus.Completed);
    }

    // PG02: Full deposit is refunded (recorded) in the same DB transaction.
    [Fact]
    public async Task PG02_Should_Cancel_And_Refund_Full_Deposit()
    {
        var id = await CreatePaidReservationAsync(3_000_000m);

        await _reservations.CancelAsync(id, CustomerCancelled());

        var reservation = await _reservations.GetAsync(id);
        reservation.Status.ShouldBe(RoomReservationStatus.Cancelled);

        var records = await ReadSettlementsAsync(id);
        records.Count.ShouldBe(1);
        records.Single().Type.ShouldBe(DepositSettlementType.Refund);
        records.Single().Amount.ShouldBe(3_000_000m);
        records.Single().Status.ShouldBe(DepositSettlementStatus.Completed);
    }

    // PG03: Inject a database-level failure during real settlement INSERT.
    // The earlier reservation UPDATE must roll back with it.
    [Fact]
    public async Task PG03_Should_Roll_Back_Cancellation_When_Settlement_Insert_Fails()
    {
        var id = await CreatePaidReservationAsync(500_000m);
        var constraint = "pg03_" + Guid.NewGuid().ToString("N")[..20];

        var (table, sourceIdColumn) = GetSettlementTableAndColumn();
        var sqlAdd =
            $"ALTER TABLE {table} ADD CONSTRAINT {Quote(constraint)} " +
            $"CHECK ({sourceIdColumn} IS NULL OR " +
            $"{sourceIdColumn} <> '{id:D}'::uuid)";
        var sqlDrop = $"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {Quote(constraint)}";

        await ExecuteSqlAsync(sqlAdd);
        try
        {
            var error = await Record.ExceptionAsync(
                () => _reservations.CancelAsync(id, CustomerCancelled()));

            error.ShouldNotBeNull();
            HasPostgresCheckViolation(error).ShouldBeTrue(
                "This test must fail because of the injected PostgreSQL constraint, " +
                "not an unrelated application error.");

            // Fresh UoW/read after the failing transaction has ended.
            var after = await _reservations.GetAsync(id);
            after.Status.ShouldBe(RoomReservationStatus.Reserved);

            var records = await ReadSettlementsAsync(id);
            records.ShouldBeEmpty();
        }
        finally
        {
            await ExecuteSqlAsync(sqlDrop);
        }
    }

    // PG04: The second cancellation request must not create another settlement.
    [Fact]
    public async Task PG04_Should_Not_Create_Duplicate_Settlements()
    {
        var id = await CreatePaidReservationAsync(500_000m);
        await _reservations.CancelAsync(id, CustomerCancelled());

        var error = await Record.ExceptionAsync(
            () => _reservations.CancelAsync(id, CustomerCancelled()));

        error.ShouldBeOfType<BusinessException>();
        ((BusinessException)error!).Code.ShouldBe(
            BuildingManagementErrorCodes.RoomReservationCannotBeCancelled);

        var records = await ReadSettlementsAsync(id);
        records.Count.ShouldBe(1);
        records.Single().Amount.ShouldBe(500_000m);
    }

    private static CancelRoomReservationDto CustomerCancelled() => new()
    {
        Reason = ReservationCancellationReason.CustomerCancelled,
        Notes = "PostgreSQL integration test"
    };

    private async Task<Guid> CreatePaidReservationAsync(decimal deposit)
    {
        var roomId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var buildingId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(new Building(
                buildingId,
                $"PostgreSQL test building {Guid.NewGuid():N}",
                "PostgreSQL test address"), autoSave: true);

            await _floors.InsertAsync(new Floor(
                floorId, buildingId, 1, "Floor 1"), autoSave: true);

            await _rooms.InsertAsync(new Room(
                roomId, floorId, "101", "Room 101", 25, 2,
                RoomStatus.Available), autoSave: true);
        });

        var n = Interlocked.Increment(ref _tenantSequence);
        var tenant = await _tenants.CreateAsync(new CreateTenantDto
        {
            FullName = $"PostgreSQL test tenant {Guid.NewGuid():N}",
            PhoneNumber = $"09{n % 100_000_000:D8}",
            IdentityType = IdentityType.CitizenId,
            IdentityNumber = $"079{n % 1_000_000_000:D9}"
        });

        var reservation = await _reservations.CreateAsync(
            new CreateRoomReservationDto
            {
                ReservationNumber = $"RSV-PG-{Guid.NewGuid():N}",
                RoomId = roomId,
                TenantId = tenant.Id,
                ExpectedMoveInDate = _clock.Now.Date.AddDays(30),
                QuotedMonthlyRent = 3_000_000m,
                RequiredDepositAmount = 3_000_000m,
                MinimumReservationDepositAmount = 500_000m
            });

        var payment = await _payments.CreateReservationDepositAsync(
            new CreateReservationDepositPaymentDto
            {
                PaymentNumber = $"PAY-PG-{Guid.NewGuid():N}",
                RoomReservationId = reservation.Id,
                Amount = deposit,
                Method = PaymentMethod.Cash,
                Channel = PaymentChannel.Manual
            });

        await _payments.CompleteAsync(payment.Id, new CompletePaymentDto());
        return reservation.Id;
    }

    private async Task<List<DepositSettlement>> ReadSettlementsAsync(Guid id)
    {
        return await WithUnitOfWorkAsync(async () =>
            await _settlements.GetListAsync(x =>
                x.SourceRoomReservationId == id &&
                (x.Type == DepositSettlementType.Forfeit ||
                 x.Type == DepositSettlementType.Refund)));
    }

    private static (string QualifiedTable, string SourceIdColumn) GetSettlementTableAndColumn()
    {
        var connection = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        using var db = new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(connection).Options);

        var entity = db.Model.FindEntityType(typeof(DepositSettlement))
            ?? throw new InvalidOperationException("Missing DepositSettlement mapping");
        var name = entity.GetTableName()
            ?? throw new InvalidOperationException("Missing DepositSettlement table");
        var schema = entity.GetSchema();
        var table = string.IsNullOrWhiteSpace(schema)
            ? Quote(name)
            : $"{Quote(schema)}.{Quote(name)}";

        var property = entity.FindProperty(nameof(DepositSettlement.SourceRoomReservationId))
            ?? throw new InvalidOperationException("Missing SourceRoomReservationId mapping");
        var column = property.GetColumnName(StoreObjectIdentifier.Table(name, schema))
            ?? throw new InvalidOperationException("Missing SourceRoomReservationId column");

        return (table, Quote(column));
    }

    private static string Quote(string name) =>
        "\"" + name.Replace("\"", "\"\"") + "\"";

    private static async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static bool HasPostgresCheckViolation(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is PostgresException pg && pg.SqlState == "23514")
            {
                return true;
            }
            exception = exception.InnerException;
        }
        return false;
    }
}
