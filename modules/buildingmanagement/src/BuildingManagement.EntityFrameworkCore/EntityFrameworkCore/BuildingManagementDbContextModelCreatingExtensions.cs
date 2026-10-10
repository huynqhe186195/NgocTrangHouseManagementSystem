using BuildingManagement.EntityConfigurations;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;

namespace BuildingManagement.EntityFrameworkCore;

public static class BuildingManagementDbContextModelCreatingExtensions
{
    public static void ConfigureBuildingManagement(
        this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        //This code use register configuration entity framework core for entity Building into Dbcontext
        builder.ApplyConfiguration(new BuildingConfiguration());
        //This code use register configuration entity framework core for entity Floor into Dbcontext
        builder.ApplyConfiguration(new FloorConfiguration());
        //This code use register configuration entity framework core for entity Room into Dbcontext
        builder.ApplyConfiguration(new RoomConfiguration());
        //This code use register configuration entity framework core for entity Tenant into Dbcontext
        builder.ApplyConfiguration(new TenantConfiguration());
        //This code use register configuration entity framework core for entity Contract into Dbcontext
        builder.ApplyConfiguration(new ContractConfiguration());
        //This code use register configuration entity framework core for entity Contract Tenant into Dbcontext
        builder.ApplyConfiguration(new ContractTenantConfiguration());
        //This code use register configuration entity framework core for entity UtilityRate Tenant into Dbcontext
        builder.ApplyConfiguration(new UtilityRateConfiguration());
        //This code use register configuration entity framework core for entity UtilityMeter Tenant into Dbcontext
        builder.ApplyConfiguration(new UtilityMeterConfiguration());
        //This code use register configuration entity framework core for entity UtilityReading Tenant into Dbcontext
        builder.ApplyConfiguration(new MeterReadingConfiguration());
        //This code use register configuration entity framework core for entity ContractRenewalHold Tenant into Dbcontext
        builder.ApplyConfiguration(new ContractRenewalHoldConfiguration());
        /* Configure all entities here. Example:

        builder.Entity<Question>(b => 
        {
            //Configure table & schema name
            b.ToTable(BuildingManagementDbProperties.DbTablePrefix + "Questions", BuildingManagementDbProperties.DbSchema);

            b.ConfigureByConvention();

            //Properties
            b.Property(q => q.Title).IsRequired().HasMaxLength(QuestionConsts.MaxTitleLength);

            //Relations
            b.HasMany(question => question.Tags).WithOne().HasForeignKey(qt => qt.QuestionId);

            //Indexes
            b.HasIndex(q => q.CreationTime);
        });
        */
    }
}
