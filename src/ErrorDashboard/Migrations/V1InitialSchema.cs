using Microsoft.Extensions.Logging;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Persistence.SqlSyntax;

namespace Our.Umbraco.ErrorDashboard.Migrations;

/// <summary>
///     Creates the five Error Dashboard tables.
/// </summary>
/// <remarks>
///     Umbraco 18 has no synchronous <c>MigrationBase</c> - <see cref="AsyncMigrationBase" /> with
///     <see cref="MigrateAsync" /> is the only option - and no <c>TableExists</c> helper, hence the
///     <see cref="ISqlSyntaxProvider.DoesTableExist" />
///     call below. The existence check keeps the migration re-runnable against a database where some
///     tables were created by hand.
/// </remarks>
public class V1InitialSchema : AsyncMigrationBase
{
    public V1InitialSchema(IMigrationContext context)
        : base(context)
    {
    }

    protected override Task MigrateAsync()
    {
        CreateIfMissing<NelReportDto>(NelReportDto.TableName_);
        CreateIfMissing<StatHourlyDto>(StatHourlyDto.TableName_);
        CreateIfMissing<StatUrlDailyDto>(StatUrlDailyDto.TableName_);
        CreateIfMissing<SubscriptionDto>(SubscriptionDto.TableName_);
        CreateIfMissing<AlertDto>(AlertDto.TableName_);

        return Task.CompletedTask;
    }

    private void CreateIfMissing<TDto>(string tableName)
    {
        if (SqlSyntax.DoesTableExist(Context.Database, tableName))
        {
            Logger.LogDebug("Table {TableName} already exists, skipping creation.", tableName);
            return;
        }

        Create.Table<TDto>().Do();
    }
}
