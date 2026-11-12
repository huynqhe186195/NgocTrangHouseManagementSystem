using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Proposed persistence mapping ONLY. Intentionally not applied in
/// BuildingManagementDbContext, and no migration has been generated/applied.
/// The isolated design-test DbContext explicitly opts into this mapping.
/// </summary>
public sealed class BuildingOperatorAccessGrantDesignConfiguration
    : IEntityTypeConfiguration<BuildingOperatorAccessGrant>
{
    public void Configure(EntityTypeBuilder<BuildingOperatorAccessGrant> builder)
    {
        builder.ToTable("BuildingManagementOperatorBuildingGrants");
        builder.ConfigureByConvention();
        builder.HasKey(x => x.Id).HasName("PK_BM_OperatorBuildingGrants");

        builder.Property(x => x.OperatorUserId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.GrantedByUserId).IsRequired();
        builder.Property(x => x.GrantedAtUtc).IsRequired();
        builder.Property(x => x.RevisionNumber).IsRequired().IsConcurrencyToken();

        // Revoked rows remain as history. A second currently unrevoked
        // row cannot be created. Expired-but-unrevoked rows still hold this
        // slot: explicit revocation is required before a new grant.
        builder.HasIndex(x => new { x.OperatorUserId, x.BuildingId })
            .IsUnique()
            .HasFilter("\"RevokedAtUtc\" IS NULL")
            .HasDatabaseName("UX_BM_OperatorBuilding_Unrevoked");

        builder.HasOne<Building>()
            .WithMany()
            .HasForeignKey(x => x.BuildingId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_BM_OperatorBuildingGrants_Building");

        // ABP identity data belongs to a different DbContext. A user FK
        // must not be invented without integration/migration review.
        builder.HasIndex(x => x.BuildingId)
            .HasDatabaseName("IX_BM_OperatorBuilding_BuildingId");
    }
}
