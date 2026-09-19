using Umbraco.Cms.Core.Packaging;

namespace Our.Umbraco.ErrorDashboard.Migrations;

/// <summary>
///     Package migration plan for the Error Dashboard tables.
/// </summary>
/// <remarks>
///     Auto-discovered (<c>PackageMigrationPlan</c> is <c>IDiscoverable</c>) and run automatically on
///     boot, so nothing registers it. The gate is
///     <c>Umbraco:CMS:Unattended:PackageMigrationsUnattended</c>, which defaults to true; if an operator
///     has turned it off, the tables are never created and the package silently collects nothing until
///     the migration is run by hand from the backoffice.
///     <para>
///         Note that Umbraco 18 runs package migrations in a background service *after* Kestrel starts,
///         so on the first boot after installing this package the collector is already accepting POSTs
///         while these tables do not yet exist. Everything touching the database guards on
///         <c>IRuntimeState.Level == RuntimeLevel.Run</c> for that reason.
///     </para>
/// </remarks>
public class ErrorDashboardMigrationPlan : PackageMigrationPlan
{
    public ErrorDashboardMigrationPlan()
        : base("ErrorDashboard")
    {
    }

    protected override void DefinePlan() => To<V1InitialSchema>("error-dashboard-v1");
}
