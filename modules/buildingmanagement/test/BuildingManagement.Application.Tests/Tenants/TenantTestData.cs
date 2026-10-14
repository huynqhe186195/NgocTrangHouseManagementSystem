using System;
using System.Threading;

namespace BuildingManagement.Tenants
{
    internal static class TenantTestData
    {
        private static long _sequence =
            DateTime.UtcNow.Ticks % 100_000_000;

        public static CreateTenantDto Create(
            string? fullName = null)
        {
            var sequence =
                Interlocked.Increment(
                    ref _sequence
                );

            var phoneSuffix =
                sequence % 100_000_000;

            var citizenIdSuffix =
                sequence % 1_000_000_000;

            return new CreateTenantDto
            {
                FullName =
                    fullName
                    ?? $"Test Tenant {sequence}",

                PhoneNumber =
                    $"09{phoneSuffix:D8}",

                IdentityType =
                    IdentityType.CitizenId,

                IdentityNumber =
                    $"079{citizenIdSuffix:D9}"
            };
        }
    }
}