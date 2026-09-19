using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Our.Umbraco.ErrorDashboard.Alerting;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Jobs;
using Our.Umbraco.ErrorDashboard.Middleware;
using Our.Umbraco.ErrorDashboard.Reporting;
using Our.Umbraco.ErrorDashboard.Services;
using Umbraco.Cms.Api.Common.OpenApi;
using Umbraco.Cms.Api.Management.OpenApi;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Extensions;
using UmbConstants = Umbraco.Cms.Core.Constants;

namespace Our.Umbraco.ErrorDashboard.Composers;

/// <summary>
///     Wires up the Error Dashboard: configuration, the NEL middleware, the ingest pipeline, the hourly
///     job and the backoffice API document.
/// </summary>
public class ErrorDashboardApiComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services
            .AddOptions<ErrorDashboardOptions>()
            .Bind(builder.Config.GetSection(ErrorDashboardOptions.SectionName));

        RegisterIngest(builder);
        RegisterServices(builder);
        RegisterPipeline(builder);
        RegisterOpenApiDocument(builder);
    }

    /// <summary>The collector, its buffer and the writer that drains it.</summary>
    private static void RegisterIngest(IUmbracoBuilder builder)
    {
        // One queue instance behind two identities: the middleware writes through the interface, the
        // writer service reads the concrete channel.
        builder.Services.AddSingleton<NelReportQueue>();
        builder.Services.AddSingleton<INelReportQueue>(sp => sp.GetRequiredService<NelReportQueue>());

        builder.Services.AddSingleton<IReportHostValidator, ReportHostValidator>();
        builder.Services.AddSingleton<NelRateLimiter>();

        // IMiddleware is resolved from DI per request, so the middleware itself has to be registered.
        // Singleton is safe - the only state it holds is a cache of two header strings.
        builder.Services.AddSingleton<NelPolicyMiddleware>();

        builder.Services.AddHostedService<NelReportWriterService>();
    }

    /// <summary>
    ///     Statistics, subscriptions and alerting.
    /// </summary>
    /// <remarks>
    ///     All singletons, because the recurring job is registered as one and cannot take scoped
    ///     dependencies. None of them hold per-request state; database work happens inside an explicit
    ///     Umbraco scope, which is created per call.
    /// </remarks>
    private static void RegisterServices(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<IErrorStatsService, ErrorStatsService>();
        builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();
        builder.Services.AddSingleton<IAlertService, AlertService>();

        // Registered concretely as well as through the recurring-job collection, so the "Recompute now"
        // endpoint can run exactly the same code path the scheduler does rather than a copy of it.
        builder.Services.AddSingleton<ErrorDashboardAggregationJob>();
        builder.Services.AddRecurringBackgroundJob(sp => sp.GetRequiredService<ErrorDashboardAggregationJob>());
    }

    /// <summary>
    ///     Puts the NEL middleware at the very front of Umbraco's pipeline.
    /// </summary>
    /// <remarks>
    ///     <c>PrePipeline</c> is the first thing inside <c>app.UseUmbraco()</c> - ahead of static files,
    ///     routing, authentication and antiforgery. That is what lets the collector skip all of them,
    ///     and what lets the policy headers reach responses Umbraco's routing never produced, such as
    ///     404s. Doing it here rather than in the host's <c>Program.cs</c> keeps the package
    ///     self-contained.
    /// </remarks>
    private static void RegisterPipeline(IUmbracoBuilder builder) =>
        builder.Services.Configure<UmbracoPipelineOptions>(options =>
            options.AddFilter(new UmbracoPipelineFilter("ErrorDashboard")
            {
                PrePipeline = app => app.UseMiddleware<NelPolicyMiddleware>(),
            }));

    private static void RegisterOpenApiDocument(IUmbracoBuilder builder) =>
        builder.AddBackOfficeOpenApiDocument(
            Constants.ApiName,
            document => document
                .WithTitle("Error Dashboard Backoffice API")
                .WithBackOfficeAuthentication()
                .WithJsonOptions(UmbConstants.JsonOptionsNames.BackOffice)
                .ConfigureOpenApiOptions(options =>
                    options.AddDocumentTransformer((doc, _, _) =>
                    {
                        doc.Info.Version = "1.0";
                        return Task.CompletedTask;
                    })));
}
