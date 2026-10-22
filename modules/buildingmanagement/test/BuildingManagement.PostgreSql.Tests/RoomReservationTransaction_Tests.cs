using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Floors;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IContractAppService _contracts;

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
        _scopeFactory = GetRequiredService<IServiceScopeFactory>();
        _contracts = GetRequiredService<IContractAppService>();
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

    // PG05: Two independent transactions racing to reserve the SAME room.
    // Lock the Room row externally to force both requests through their
    // availability checks before either Room update can commit.
    [Fact]
    public async Task PG05_Should_Allow_Only_One_Concurrent_Deposit_For_Same_Room()
    {
        var roomId = await CreateTestRoomAsync();
        var first = await CreatePendingPaymentForRoomAsync(roomId, 500_000m);
        var second = await CreatePendingPaymentForRoomAsync(roomId, 500_000m);

        var (roomTable, roomIdColumn, roomTableName) = GetRoomTableAndIdColumn();

        await using var blocker = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();

        Task<Exception?>? firstAttempt = null;
        Task<Exception?>? secondAttempt = null;
        var bothReachedRoomUpdate = false;

        try
        {
            // Row lock is scoped to this transaction only; it changes NO data.
            await using (var hold = new NpgsqlCommand(
                $"SELECT 1 FROM {roomTable} WHERE {roomIdColumn} = @roomId FOR UPDATE",
                blocker, blockerTransaction))
            {
                hold.Parameters.AddWithValue("roomId", roomId);
                var found = await hold.ExecuteScalarAsync();
                found.ShouldNotBeNull("PG05 test Room row must exist.");
            }

            // Separate DI scopes; each invocation gets its own application UOW.
            firstAttempt = Task.Run(() => CompleteInIndependentScopeAsync(first.PaymentId));
            secondAttempt = Task.Run(() => CompleteInIndependentScopeAsync(second.PaymentId));

            // Insist on TWO blocked Room row operations, not merely two launched Tasks.
            bothReachedRoomUpdate = await WaitForTwoBlockedTableOperationsAsync(
                roomTableName,
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            // Release the held Room row so BOTH service calls can finish.
            await blockerTransaction.RollbackAsync();
        }

        if (firstAttempt is null || secondAttempt is null)
        {
            throw new InvalidOperationException("PG05 did not start both payment attempts.");
        }

        var errors = await Task.WhenAll(firstAttempt, secondAttempt);

        bothReachedRoomUpdate.ShouldBeTrue(
            "PG05 did not observe both calls blocked on Room row operations. " +
            "Do not treat a sequential run as a concurrency proof. " +
            $"A: {errors[0]?.GetType().Name ?? "success"}; " +
            $"B: {errors[1]?.GetType().Name ?? "success"}.");

        var firstPayment = await _payments.GetAsync(first.PaymentId);
        var secondPayment = await _payments.GetAsync(second.PaymentId);
        var firstReservation = await _reservations.GetAsync(first.ReservationId);
        var secondReservation = await _reservations.GetAsync(second.ReservationId);

        // The invariant: at most one successful deposit / confirmed hold.
        var completedPaymentCount = new[] { firstPayment, secondPayment }
            .Count(p => p.Status == PaymentStatus.Completed);
        var reservedReservationCount = new[] { firstReservation, secondReservation }
            .Count(r => r.Status == RoomReservationStatus.Reserved);

        completedPaymentCount.ShouldBe(1,
            "Exactly one competing Payment may complete for the same Room.");
        reservedReservationCount.ShouldBe(1,
            "Exactly one competing Reservation may become Reserved.");

        var errorCount = errors.Count(error => error is not null);
        errorCount.ShouldBe(1,
            "The losing Payment must fail, not silently succeed.");
        /*
         * PG05: The losing transaction must fail due to
         * a domain conflict or an optimistic concurrency conflict.
         * ABP handles AbpDbConcurrencyException as HTTP 409.
         */
        var losingError = errors.Single(
            error => error is not null
        )!;

        if (losingError is BusinessException businessError)
        {
            businessError.Code.ShouldBe(
                BuildingManagementErrorCodes.RoomAlreadyReserved
            );
        }
        else
        {
            losingError.ShouldBeOfType<
                Volo.Abp.Data.AbpDbConcurrencyException
            >();
        }

        var roomStatus = await WithUnitOfWorkAsync(async () =>
            (await _rooms.GetAsync(roomId)).Status);
        roomStatus.ShouldBe(RoomStatus.Reserved);

        // A failed completion must roll back both Payment and Reservation.
        if (firstPayment.Status == PaymentStatus.Completed)
        {
            firstReservation.Status.ShouldBe(RoomReservationStatus.Reserved);
            secondPayment.Status.ShouldBe(PaymentStatus.Pending);
            secondReservation.Status.ShouldBe(RoomReservationStatus.PendingPayment);
        }
        else
        {
            secondPayment.Status.ShouldBe(PaymentStatus.Completed);
            secondReservation.Status.ShouldBe(RoomReservationStatus.Reserved);
            firstPayment.Status.ShouldBe(PaymentStatus.Pending);
            firstReservation.Status.ShouldBe(RoomReservationStatus.PendingPayment);
        }
    }

    // PG06: Unlike PG05, an Occupied Room is NOT updated when a future
    // Reservation becomes Reserved. The two independent UOWs must not both
    // commit their first deposits for the same future move-in date.
    //
    // The test deliberately locks BOTH Payment rows (not the Room row) to
    // make both transactions finish their reservation checks before either
    // Payment can be persisted. This test should expose a double-booking bug
    // until Room-scoped serialization or an equivalent guard is implemented.
    [Fact]
    public async Task PG06_Should_Allow_Only_One_Concurrent_Deposit_For_Occupied_Room()
    {
        var roomId = await CreateTestRoomAsync();
        var todayInVietnam = GetTodayInVietnam();
        var firstDayOfNextMonth =
            new DateTime(todayInVietnam.Year, todayInVietnam.Month, 1).AddMonths(1);

        // Current occupant's agreement ends on the last day of this month.
        // A move-in on the 1st of next month is valid under MoveInDatePolicy.
        var oldContract = await CreateActiveContractForRoomAsync(
            roomId,
            todayInVietnam.AddDays(-30),
            firstDayOfNextMonth.AddDays(-1));
        oldContract.Status.ShouldBe(ContractStatus.Active);

        var before = await WithUnitOfWorkAsync(async () =>
            (await _rooms.GetAsync(roomId)).Status);
        before.ShouldBe(RoomStatus.Occupied);

        var first = await CreatePendingPaymentForRoomAsync(
            roomId, 500_000m, firstDayOfNextMonth);
        var second = await CreatePendingPaymentForRoomAsync(
            roomId, 500_000m, firstDayOfNextMonth);

        var (paymentTable, paymentIdColumn, paymentTableName) =
            GetPaymentTableAndIdColumn();

        await using var blocker = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();

        Task<Exception?>? firstAttempt = null;
        Task<Exception?>? secondAttempt = null;
        var bothReachedPaymentUpdate = false;

        try
        {
            // Lock two DIFFERENT Payment rows, leaving all Room and
            // Reservation rows available for the business-rule reads.
            await using (var hold = new NpgsqlCommand(
                $"SELECT {paymentIdColumn} FROM {paymentTable} " +
                $"WHERE {paymentIdColumn} IN (@firstId, @secondId) FOR UPDATE",
                blocker, blockerTransaction))
            {
                hold.Parameters.AddWithValue("firstId", first.PaymentId);
                hold.Parameters.AddWithValue("secondId", second.PaymentId);
                await using var reader = await hold.ExecuteReaderAsync();
                var lockedPaymentRows = 0;
                while (await reader.ReadAsync()) lockedPaymentRows++;
                lockedPaymentRows.ShouldBe(2, "Both pending Payment rows must exist.");
            }

            firstAttempt = Task.Run(() =>
                CompleteInIndependentScopeAsync(first.PaymentId));
            secondAttempt = Task.Run(() =>
                CompleteInIndependentScopeAsync(second.PaymentId));

            // Require evidence that BOTH requests have reached a blocked
            // Payment UPDATE. Otherwise their timing proves nothing.
            bothReachedPaymentUpdate = await WaitForTwoBlockedTableOperationsAsync(
                paymentTableName, TimeSpan.FromSeconds(20));
        }
        finally
        {
            // Unblock both requests, regardless of monitoring outcome.
            await blockerTransaction.RollbackAsync();
        }

        if (firstAttempt is null || secondAttempt is null)
            throw new InvalidOperationException("PG06 did not start both payment attempts.");

        var errors = await Task.WhenAll(firstAttempt, secondAttempt);
        bothReachedPaymentUpdate.ShouldBeTrue(
            "PG06 did not observe two Payment UPDATEs waiting on locks. " +
            "Do not treat a sequential run as concurrency evidence. " +
            $"A: {errors[0]?.GetType().Name ?? "success"}; " +
            $"B: {errors[1]?.GetType().Name ?? "success"}.");

        // Read fresh committed state after BOTH UOWs have ended.
        var firstPayment = await _payments.GetAsync(first.PaymentId);
        var secondPayment = await _payments.GetAsync(second.PaymentId);
        var firstReservation = await _reservations.GetAsync(first.ReservationId);
        var secondReservation = await _reservations.GetAsync(second.ReservationId);

        var completedCount = new[] { firstPayment, secondPayment }
            .Count(p => p.Status == PaymentStatus.Completed);
        var reservedCount = new[] { firstReservation, secondReservation }
            .Count(r => r.Status == RoomReservationStatus.Reserved);

        // RED expectation for current implementation if both transactions
        // see no committed Reserved reservation before their Payment UPDATE.
        completedCount.ShouldBe(1,
            "PG06 DOUBLE BOOKING: Exactly one first deposit may be Completed " +
            "for an Occupied Room with the same future move-in date.");
        reservedCount.ShouldBe(1,
            "PG06 DOUBLE BOOKING: Exactly one future Reservation may be Reserved.");

        errors.Count(error => error is not null).ShouldBe(1,
            "The losing Payment must not silently succeed.");

        var losingError = errors.Single(error => error is not null)!;
        if (losingError is BusinessException businessError)
        {
            businessError.Code.ShouldBe(
                BuildingManagementErrorCodes.RoomAlreadyReserved);
        }
        else
        {
            losingError.ShouldBeOfType<Volo.Abp.Data.AbpDbConcurrencyException>();
        }

        var after = await WithUnitOfWorkAsync(async () =>
            (await _rooms.GetAsync(roomId)).Status);
        after.ShouldBe(RoomStatus.Occupied);

        var contractAfter = await _contracts.GetAsync(oldContract.Id);
        contractAfter.Status.ShouldBe(ContractStatus.Active);

        // The unsuccessful Payment and Reservation must remain pending.
        if (firstPayment.Status == PaymentStatus.Completed)
        {
            firstReservation.Status.ShouldBe(RoomReservationStatus.Reserved);
            secondPayment.Status.ShouldBe(PaymentStatus.Pending);
            secondReservation.Status.ShouldBe(RoomReservationStatus.PendingPayment);
        }
        else
        {
            secondPayment.Status.ShouldBe(PaymentStatus.Completed);
            secondReservation.Status.ShouldBe(RoomReservationStatus.Reserved);
            firstPayment.Status.ShouldBe(PaymentStatus.Pending);
            firstReservation.Status.ShouldBe(RoomReservationStatus.PendingPayment);
        }
    }

    private static CancelRoomReservationDto CustomerCancelled() => new()
    {
        Reason = ReservationCancellationReason.CustomerCancelled,
        Notes = "PostgreSQL integration test"
    };

    private DateTime GetTodayInVietnam()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(
            _clock.Now,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))
            .Date;
    }

    private DateTime GetTomorrowInVietnam() => GetTodayInVietnam().AddDays(1);

    private async Task<Guid> CreateTestRoomAsync()
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

        return roomId;
    }

    private async Task<ContractDto> CreateActiveContractForRoomAsync(
        Guid roomId,
        DateTime startDate,
        DateTime endDate)
    {
        var contract = await _contracts.CreateAsync(new CreateContractDto
        {
            ContractNumber = $"HD-PG-{Guid.NewGuid():N}",
            RoomId = roomId,
            StartDate = startDate.Date,
            EndDate = endDate.Date,
            MonthlyRent = 3_000_000m
        });

        var n = Interlocked.Increment(ref _tenantSequence);
        var tenant = await _tenants.CreateAsync(new CreateTenantDto
        {
            FullName = $"PostgreSQL contract tenant {Guid.NewGuid():N}",
            PhoneNumber = $"09{n % 100_000_000:D8}",
            IdentityType = IdentityType.CitizenId,
            IdentityNumber = $"079{n % 1_000_000_000:D9}"
        });

        await _contracts.AddTenantAsync(contract.Id, new AddContractTenantDto
        {
            TenantId = tenant.Id,
            Role = ContractTenantRole.PrimaryTenant
        });

        return await _contracts.ActivateAsync(contract.Id);
    }

    private async Task<(Guid ReservationId, Guid PaymentId)>
        CreatePendingPaymentForRoomAsync(
            Guid roomId, decimal deposit, DateTime? expectedMoveInDate = null)
    {
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
                ExpectedMoveInDate = (expectedMoveInDate ?? GetTomorrowInVietnam()).Date,
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

        return (reservation.Id, payment.Id);
    }

    private async Task<Guid> CreatePaidReservationAsync(decimal deposit)
    {
        var roomId = await CreateTestRoomAsync();
        var pending = await CreatePendingPaymentForRoomAsync(roomId, deposit);
        await _payments.CompleteAsync(pending.PaymentId, new CompletePaymentDto());
        return pending.ReservationId;
    }

    private async Task<Exception?> CompleteInIndependentScopeAsync(Guid paymentId)
    {
        using var scope = _scopeFactory.CreateScope();
        try
        {
            var paymentService = scope.ServiceProvider.GetRequiredService<IPaymentAppService>();
            await paymentService.CompleteAsync(paymentId, new CompletePaymentDto());
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static async Task<bool> WaitForTwoBlockedTableOperationsAsync(
        string roomTableName,
        TimeSpan timeout)
    {
        await using var monitor = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await monitor.OpenAsync();

        const string sql = @"
    SELECT COUNT(*)
    FROM pg_stat_activity
    WHERE datname = current_database()
      AND state = 'active'
      AND wait_event_type = 'Lock'
      AND query ILIKE @roomTablePattern";

        await using var command = new NpgsqlCommand(sql, monitor);
        command.Parameters.AddWithValue(
            "roomTablePattern", "%" + Quote(roomTableName) + "%");

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (count >= 2)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static (string QualifiedTable, string IdColumn, string TableName)
        GetRoomTableAndIdColumn()
    {
        var connection = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        using var db = new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(connection).Options);

        var entity = db.Model.FindEntityType(typeof(Room))
            ?? throw new InvalidOperationException("Missing Room mapping");
        var name = entity.GetTableName()
            ?? throw new InvalidOperationException("Missing Room table");
        var schema = entity.GetSchema();
        var table = string.IsNullOrWhiteSpace(schema)
            ? Quote(name)
            : $"{Quote(schema)}.{Quote(name)}";

        var property = entity.FindProperty(nameof(Room.Id))
            ?? throw new InvalidOperationException("Missing Room Id mapping");
        var column = property.GetColumnName(StoreObjectIdentifier.Table(name, schema))
            ?? throw new InvalidOperationException("Missing Room Id column");

        return (table, Quote(column), name);
    }

    private static (string QualifiedTable, string IdColumn, string TableName)
        GetPaymentTableAndIdColumn()
    {
        var connection = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        using var db = new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(connection).Options);

        var entity = db.Model.FindEntityType(typeof(Payment))
            ?? throw new InvalidOperationException("Missing Payment mapping");
        var name = entity.GetTableName()
            ?? throw new InvalidOperationException("Missing Payment table");
        var schema = entity.GetSchema();
        var table = string.IsNullOrWhiteSpace(schema)
            ? Quote(name)
            : $"{Quote(schema)}.{Quote(name)}";

        var property = entity.FindProperty(nameof(Payment.Id))
            ?? throw new InvalidOperationException("Missing Payment Id mapping");
        var column = property.GetColumnName(StoreObjectIdentifier.Table(name, schema))
            ?? throw new InvalidOperationException("Missing Payment Id column");

        return (table, Quote(column), name);
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
