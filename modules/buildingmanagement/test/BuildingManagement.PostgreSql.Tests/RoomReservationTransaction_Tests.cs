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
    private readonly IContractRenewalHoldAppService _renewalHolds;

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
        _renewalHolds = GetRequiredService<IContractRenewalHoldAppService>();
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
    // The test blocks BOTH Payment rows to exercise shared Active Contract
    // serialization: one Payment waits on its row; the other waits on the
    // Contract gate until the first transaction commits or rolls back.
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
        var (_, _, contractTableName) = GetContractTableAndIdColumn();

        await using var blocker = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();

        Task<Exception?>? firstAttempt = null;
        Task<Exception?>? secondAttempt = null;
        var bothBlockedOnSharedGate = false;

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

            // With the shared Contract lock, only ONE call reaches Payment UPDATE.
            // The other waits on the Contract. Observe both waiters, not two
            // Payment UPDATEs (which would indicate the shared gate is missing).
            bothBlockedOnSharedGate = await WaitForTwoBlockedOperationsAcrossTablesAsync(
                paymentTableName, contractTableName, TimeSpan.FromSeconds(20));
        }
        finally
        {
            // Unblock both requests, regardless of monitoring outcome.
            await blockerTransaction.RollbackAsync();
        }

        if (firstAttempt is null || secondAttempt is null)
            throw new InvalidOperationException("PG06 did not start both payment attempts.");

        var errors = await Task.WhenAll(firstAttempt, secondAttempt);
        bothBlockedOnSharedGate.ShouldBeTrue(
            "PG06 did not observe one Payment waiter and one Contract waiter. " +
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

    // PG07: A tenant's active 24-hour renewal hold has priority over an
    // unpaid third-party Reservation. Force RenewAsync and CompleteAsync to
    // contend on the SAME pending Reservation row in separate transactions.
    // The renewal must succeed, the competing deposit must NOT complete,
    // and the pending Reservation must be cancelled as SupersededByRenewal.
    [Fact]
    public async Task PG07_Should_Prioritize_Renewal_Over_Concurrent_First_Deposit()
    {
        var roomId = await CreateTestRoomAsync();
        var today = GetTodayInVietnam();
        var moveInDate = new DateTime(today.Year, today.Month, 1).AddMonths(1);

        var current = await CreateActiveContractForRoomAsync(
            roomId, today.AddDays(-30), moveInDate.AddDays(-1));
        current.Status.ShouldBe(ContractStatus.Active);

        // B has a pending future Reservation and an unpaid 500k Payment.
        var future = await CreatePendingPaymentForRoomAsync(
            roomId, 500_000m, moveInDate);

        // A acquired renewal priority before B paid.
        var renewalHold = await _renewalHolds.CreateAsync(
            new CreateContractRenewalHoldDto
            {
                CurrentContractId = current.Id,
                Notes = "PG07 concurrent renewal priority"
            });
        renewalHold.Status.ShouldBe(ContractRenewalHoldStatus.Active);

        var (reservationTable, reservationIdColumn, reservationTableName) =
            GetReservationTableAndIdColumn();

        await using var blocker = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();

        Task<Exception?>? renewAttempt = null;
        Task<Exception?>? paymentAttempt = null;
        var renewalReachedReservation = false;

        try
        {
            // Keep the pending Reservation row locked without modifying it.
            // Renewal must reach the row; Payment may fast-fail on active Hold.
            await using (var hold = new NpgsqlCommand(
                $"SELECT 1 FROM {reservationTable} " +
                $"WHERE {reservationIdColumn} = @reservationId FOR UPDATE",
                blocker, blockerTransaction))
            {
                hold.Parameters.AddWithValue("reservationId", future.ReservationId);
                var found = await hold.ExecuteScalarAsync();
                found.ShouldNotBeNull("PG07 Reservation row must exist.");
            }

            renewAttempt = Task.Run(() => RenewInIndependentScopeAsync(
                current.Id, moveInDate, moveInDate.AddMonths(1).AddDays(-1)));
            paymentAttempt = Task.Run(() =>
                CompleteInIndependentScopeAsync(future.PaymentId));

            // Renewal waits on Reservation while Payment may reject an
            // existing Hold before reaching the Reservation lock.
            renewalReachedReservation = await WaitForBlockedTableOperationsAsync(
                reservationTableName, minimumBlocked: 1, timeout: TimeSpan.FromSeconds(20));
        }
        finally
        {
            // Always unblock both requests, including failed monitoring runs.
            await blockerTransaction.RollbackAsync();
        }

        if (renewAttempt is null || paymentAttempt is null)
            throw new InvalidOperationException("PG07 did not start both requests.");

        var errors = await Task.WhenAll(renewAttempt, paymentAttempt);

        renewalReachedReservation.ShouldBeTrue(
            "PG07 did not observe renewal waiting on the Reservation row. " +
            "Do not interpret a sequential run as concurrency evidence. " +
            $"Renew: {errors[0]?.GetType().Name ?? "success"}; " +
            $"Payment: {errors[1]?.GetType().Name ?? "success"}.");

        errors[0].ShouldBeNull(
            $"The current tenant must be able to renew with an active hold. " +
            $"Actual error: {errors[0]}");
        errors[1].ShouldNotBeNull(
            "A first reservation deposit must fail while the tenant's renewal " +
            "hold has priority over the same move-in period.");
        (errors[1] is BusinessException ||
         errors[1] is Volo.Abp.Data.AbpDbConcurrencyException).ShouldBeTrue(
            $"Expected a domain or concurrency conflict, got {errors[1]}");

        // Observe committed state in fresh service calls AFTER both UOWs end.
        var paymentAfter = await _payments.GetAsync(future.PaymentId);
        var reservationAfter = await _reservations.GetAsync(future.ReservationId);
        var holdAfter = await _renewalHolds.GetAsync(renewalHold.Id);
        var currentAfter = await _contracts.GetAsync(current.Id);
        var roomAfter = await WithUnitOfWorkAsync(async () =>
            (await _rooms.GetAsync(roomId)).Status);

        paymentAfter.Status.ShouldBe(PaymentStatus.Pending);
        reservationAfter.Status.ShouldBe(RoomReservationStatus.Cancelled);
        reservationAfter.CancellationReason.ShouldBe(
            ReservationCancellationReason.SupersededByRenewal);
        holdAfter.Status.ShouldBe(ContractRenewalHoldStatus.Completed);
        holdAfter.CompletedContractId.ShouldNotBeNull();
        (await _contracts.GetAsync(holdAfter.CompletedContractId!.Value))
            .Status.ShouldBe(ContractStatus.Signed);
        currentAfter.Status.ShouldBe(ContractStatus.Active);
        roomAfter.ShouldBe(RoomStatus.Occupied);
    }

    // PG08: A first deposit which has acquired the Active Contract row lock
    // must commit before a concurrent renewal Hold can proceed. The Hold may
    // subsequently be created (existing C59/C60 behavior), but RenewAsync
    // must reject a period overlapping B's already Reserved reservation.
    // PG07 separately verifies the Hold-first path.
    [Fact]
    public async Task PG08_Should_Serialize_InFlight_Deposit_And_Renewal_Hold()
    {
        var roomId = await CreateTestRoomAsync();
        var today = GetTodayInVietnam();
        var moveInDate = new DateTime(today.Year, today.Month, 1).AddMonths(1);

        var current = await CreateActiveContractForRoomAsync(
            roomId, today.AddDays(-30), moveInDate.AddDays(-1));
        current.Status.ShouldBe(ContractStatus.Active);

        var future = await CreatePendingPaymentForRoomAsync(
            roomId, 500_000m, moveInDate);

        var (paymentTable, paymentIdColumn, paymentTableName) =
            GetPaymentTableAndIdColumn();
        var (_, _, contractTableName) = GetContractTableAndIdColumn();

        await using var blocker = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();

        Task<Exception?>? paymentAttempt = null;
        Task<ContractRenewalHoldDto>? holdAttempt = null;
        var paymentReachedUpdate = false;
        var holdWaitedForContract = false;

        try
        {
            await using (var lockPayment = new NpgsqlCommand(
                $"SELECT 1 FROM {paymentTable} " +
                $"WHERE {paymentIdColumn} = @paymentId FOR UPDATE",
                blocker, blockerTransaction))
            {
                lockPayment.Parameters.AddWithValue("paymentId", future.PaymentId);
                (await lockPayment.ExecuteScalarAsync()).ShouldNotBeNull(
                    "PG08 pending Payment row must exist.");
            }

            // Payment gets the Contract lock and reaches Payment UPDATE,
            // where the external test transaction forces it to wait.
            paymentAttempt = Task.Run(() =>
                CompleteInIndependentScopeAsync(future.PaymentId));

            paymentReachedUpdate = await WaitForBlockedTableOperationsAsync(
                paymentTableName, minimumBlocked: 1, timeout: TimeSpan.FromSeconds(20));

            if (paymentReachedUpdate)
            {
                holdAttempt = Task.Run(() =>
                    CreateHoldInIndependentScopeAsync(current.Id));

                // Hold must wait on the shared Contract lock instead of
                // overtaking Payment while Payment is blocked on its row.
                holdWaitedForContract = await WaitForBlockedTableOperationsAsync(
                    contractTableName, minimumBlocked: 1, timeout: TimeSpan.FromSeconds(20));
            }
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }

        if (paymentAttempt is null || holdAttempt is null)
            throw new InvalidOperationException(
                "PG08 did not start both Payment and Hold requests.");

        var paymentError = await paymentAttempt;
        var createdHold = await holdAttempt;

        paymentReachedUpdate.ShouldBeTrue(
            "PG08 did not observe an in-flight Payment blocked on UPDATE.");
        holdWaitedForContract.ShouldBeTrue(
            "PG08 expected the renewal Hold to wait on the shared Contract " +
            "row until the in-flight first deposit commits.");
        paymentError.ShouldBeNull(
            $"The Payment holding the Contract lock should finish first: {paymentError}");
        createdHold.Status.ShouldBe(ContractRenewalHoldStatus.Active);

        (await _payments.GetAsync(future.PaymentId)).Status
            .ShouldBe(PaymentStatus.Completed);
        (await _reservations.GetAsync(future.ReservationId)).Status
            .ShouldBe(RoomReservationStatus.Reserved);
        (await _contracts.GetAsync(current.Id)).Status
            .ShouldBe(ContractStatus.Active);
        (await WithUnitOfWorkAsync(async () =>
            (await _rooms.GetAsync(roomId)).Status)).ShouldBe(RoomStatus.Occupied);

        // Consistent with existing C59/C60: creating a Hold does not choose
        // renewal dates. Overlapping renewal is rejected, while a shorter,
        // non-overlapping renewal can still be valid.
        var renewalError = await Assert.ThrowsAsync<BusinessException>(
            () => _contracts.RenewAsync(current.Id, new RenewContractDto
            {
                ContractNumber = $"HD-PG08-RENEW-{Guid.NewGuid():N}",
                StartDate = moveInDate,
                EndDate = moveInDate.AddMonths(1).AddDays(-1),
                MonthlyRent = 3_500_000m
            }));
        renewalError.Code.ShouldBe(BuildingManagementErrorCodes.RoomAlreadyReserved);

        (await _reservations.GetAsync(future.ReservationId)).Status
            .ShouldBe(RoomReservationStatus.Reserved);
        (await _payments.GetAsync(future.PaymentId)).Status
            .ShouldBe(PaymentStatus.Completed);
        (await _renewalHolds.GetAsync(createdHold.Id)).Status
            .ShouldBe(ContractRenewalHoldStatus.Active);
    }

    // PG09: Renewal and first-deposit completion must use the SAME lock order:
    // Active Contract -> Reservation. Block the pending Reservation externally,
    // wait until RenewAsync holds Contract and waits on Reservation, then
    // verify CompleteAsync must wait on Contract rather than overtaking Renew.
    // Releasing the external lock must let both finish WITHOUT a deadlock.
    [Fact]
    public async Task PG09_Should_Serialize_Renewal_And_Deposit_Without_Deadlock()
    {
        var roomId = await CreateTestRoomAsync();
        var today = GetTodayInVietnam();
        var moveInDate = new DateTime(today.Year, today.Month, 1).AddMonths(1);

        var current = await CreateActiveContractForRoomAsync(
            roomId, today.AddDays(-30), moveInDate.AddDays(-1));
        current.Status.ShouldBe(ContractStatus.Active);

        var future = await CreatePendingPaymentForRoomAsync(
            roomId, 500_000m, moveInDate);

        var hold = await _renewalHolds.CreateAsync(
            new CreateContractRenewalHoldDto
            {
                CurrentContractId = current.Id,
                Notes = "PG09 renewal vs deposit lock ordering"
            });
        hold.Status.ShouldBe(ContractRenewalHoldStatus.Active);

        var (reservationTable, reservationIdColumn, reservationTableName) =
            GetReservationTableAndIdColumn();
        var (_, _, contractTableName) = GetContractTableAndIdColumn();

        await using var blocker = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();

        Task<Exception?>? renewAttempt = null;
        Task<Exception?>? paymentAttempt = null;
        var renewalBlockedOnReservation = false;
        var paymentBlockedOnContract = false;

        try
        {
            await using (var lockReservation = new NpgsqlCommand(
                $"SELECT 1 FROM {reservationTable} " +
                $"WHERE {reservationIdColumn} = @id FOR UPDATE",
                blocker, blockerTransaction))
            {
                lockReservation.Parameters.AddWithValue("id", future.ReservationId);
                (await lockReservation.ExecuteScalarAsync()).ShouldNotBeNull(
                    "PG09 pending Reservation row must exist.");
            }

            // Renewal must obtain the Contract gate before touching Reservation.
            renewAttempt = Task.Run(() => RenewInIndependentScopeAsync(
                current.Id, moveInDate, moveInDate.AddMonths(1).AddDays(-1)));

            renewalBlockedOnReservation = await WaitForBlockedTableOperationsAsync(
                reservationTableName,
                minimumBlocked: 1,
                timeout: TimeSpan.FromSeconds(20));

            if (renewalBlockedOnReservation)
            {
                paymentAttempt = Task.Run(() =>
                    CompleteInIndependentScopeAsync(future.PaymentId));

                // Payment must now wait for Contract, not lock Reservation first.
                paymentBlockedOnContract = await WaitForBlockedTableOperationsAsync(
                    contractTableName,
                    minimumBlocked: 1,
                    timeout: TimeSpan.FromSeconds(20));
            }
        }
        finally
        {
            // Always release the test-only lock, even when monitoring fails.
            await blockerTransaction.RollbackAsync();
        }

        if (renewAttempt is null || paymentAttempt is null)
            throw new InvalidOperationException(
                "PG09 did not start both concurrent application operations.");

        var errors = await Task.WhenAll(renewAttempt, paymentAttempt)
            .WaitAsync(TimeSpan.FromSeconds(30));

        renewalBlockedOnReservation.ShouldBeTrue(
            "PG09 did not observe Renewal waiting for Reservation.");
        paymentBlockedOnContract.ShouldBeTrue(
            "PG09 did not observe Payment waiting on the Active Contract " +
            "while Renewal was blocked on Reservation. Verify lock ordering.");

        errors[0].ShouldBeNull(
            $"Renewal should finish after Reservation unlock: {errors[0]}");
        errors[1].ShouldBeOfType<BusinessException>(
            "The first deposit must be rejected after Renewal cancels its Reservation.");

        var paymentAfter = await _payments.GetAsync(future.PaymentId);
        var reservationAfter = await _reservations.GetAsync(future.ReservationId);
        var holdAfter = await _renewalHolds.GetAsync(hold.Id);

        paymentAfter.Status.ShouldBe(PaymentStatus.Pending);
        reservationAfter.Status.ShouldBe(RoomReservationStatus.Cancelled);
        reservationAfter.CancellationReason.ShouldBe(
            ReservationCancellationReason.SupersededByRenewal);
        holdAfter.Status.ShouldBe(ContractRenewalHoldStatus.Completed);
        holdAfter.CompletedContractId.ShouldNotBeNull();

        (await _contracts.GetAsync(holdAfter.CompletedContractId!.Value))
            .Status.ShouldBe(ContractStatus.Signed);
        (await _contracts.GetAsync(current.Id))
            .Status.ShouldBe(ContractStatus.Active);
        (await WithUnitOfWorkAsync(async () =>
            (await _rooms.GetAsync(roomId)).Status))
            .ShouldBe(RoomStatus.Occupied);
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

    private async Task<Exception?> RenewInIndependentScopeAsync(
        Guid currentContractId, DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        try
        {
            var contractService =
                scope.ServiceProvider.GetRequiredService<IContractAppService>();
            await contractService.RenewAsync(currentContractId, new RenewContractDto
            {
                ContractNumber = $"HD-PG-RENEW-{Guid.NewGuid():N}",
                StartDate = startDate.Date,
                EndDate = endDate.Date,
                MonthlyRent = 3_500_000m
            });
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private async Task<ContractRenewalHoldDto> CreateHoldInIndependentScopeAsync(
        Guid currentContractId)
    {
        using var scope = _scopeFactory.CreateScope();
        var holdService =
            scope.ServiceProvider.GetRequiredService<IContractRenewalHoldAppService>();

        return await holdService.CreateAsync(new CreateContractRenewalHoldDto
        {
            CurrentContractId = currentContractId,
            Notes = "PG08 hold committed before in-flight deposit"
        });
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

    private static async Task<bool> WaitForTwoBlockedOperationsAcrossTablesAsync(
        string firstTable, string secondTable, TimeSpan timeout)
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
      AND (query ILIKE @firstPattern OR query ILIKE @secondPattern)";

        await using var command = new NpgsqlCommand(sql, monitor);
        command.Parameters.AddWithValue("firstPattern", "%" + Quote(firstTable) + "%");
        command.Parameters.AddWithValue("secondPattern", "%" + Quote(secondTable) + "%");

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) >= 2)
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    private static Task<bool> WaitForTwoBlockedTableOperationsAsync(
        string tableName, TimeSpan timeout)
    {
        // Retain the PG05-PG07 behaviour (two blocked operations).
        return WaitForBlockedTableOperationsAsync(
            tableName, minimumBlocked: 2, timeout: timeout);
    }

    private static async Task<bool> WaitForBlockedTableOperationsAsync(
        string tableName, int minimumBlocked, TimeSpan timeout)
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
            "roomTablePattern", "%" + Quote(tableName) + "%");

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (count >= minimumBlocked)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static (string QualifiedTable, string IdColumn, string TableName)
        GetContractTableAndIdColumn()
    {
        var connection = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        using var db = new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(connection).Options);

        var entity = db.Model.FindEntityType(typeof(Contract))
            ?? throw new InvalidOperationException("Missing Contract mapping");
        var name = entity.GetTableName()
            ?? throw new InvalidOperationException("Missing Contract table");
        var schema = entity.GetSchema();
        var table = string.IsNullOrWhiteSpace(schema)
            ? Quote(name)
            : $"{Quote(schema)}.{Quote(name)}";

        var property = entity.FindProperty(nameof(Contract.Id))
            ?? throw new InvalidOperationException("Missing Contract Id mapping");
        var column = property.GetColumnName(StoreObjectIdentifier.Table(name, schema))
            ?? throw new InvalidOperationException("Missing Contract Id column");

        return (table, Quote(column), name);
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
        GetReservationTableAndIdColumn()
    {
        var connection = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        using var db = new BuildingManagementDbContext(
            new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(connection).Options);

        var entity = db.Model.FindEntityType(typeof(RoomReservation))
            ?? throw new InvalidOperationException("Missing RoomReservation mapping");
        var name = entity.GetTableName()
            ?? throw new InvalidOperationException("Missing RoomReservation table");
        var schema = entity.GetSchema();
        var table = string.IsNullOrWhiteSpace(schema)
            ? Quote(name)
            : $"{Quote(schema)}.{Quote(name)}";

        var property = entity.FindProperty(nameof(RoomReservation.Id))
            ?? throw new InvalidOperationException("Missing RoomReservation Id mapping");
        var column = property.GetColumnName(StoreObjectIdentifier.Table(name, schema))
            ?? throw new InvalidOperationException("Missing RoomReservation Id column");

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
