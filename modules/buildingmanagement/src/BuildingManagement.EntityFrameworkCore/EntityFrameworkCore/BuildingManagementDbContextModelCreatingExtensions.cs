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
