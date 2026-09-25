using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Our.Umbraco.ErrorDashboard.Alerting;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Jobs;
using Our.Umbraco.ErrorDashboard.Middleware;
using Our.Umbraco.ErrorDashboard.Reporting;
using Our.Umbraco.ErrorDashboard.Services;
using Swashbuckle.AspNetCore.SwaggerGen;
using Umbraco.Cms.Api.Common.OpenApi;
using Umbraco.Cms.Api.Management.OpenApi;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Extensions;

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

    /// <summary>
    ///     Umbraco 17 generates OpenAPI with Swashbuckle; the document is served at
    ///     <c>/umbraco/swagger/errordashboard/swagger.json</c>. See
    ///     https://docs.umbraco.com/umbraco-cms/17.latest/tutorials/creating-a-backoffice-api
    /// </summary>
    private static void RegisterOpenApiDocument(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<IOperationIdHandler, ErrorDashboardOperationIdHandler>();

        builder.Services.Configure<SwaggerGenOptions>(options =>
        {
            options.SwaggerDoc(Constants.ApiName, new OpenApiInfo
            {
                Title = "Error Dashboard Backoffice API",
                Version = "1.0",
            });

            options.OperationFilter<ErrorDashboardOperationSecurityFilter>();
        });
    }

    /// <summary>Marks every operation in this package's document as requiring backoffice authentication.</summary>
    public class ErrorDashboardOperationSecurityFilter : BackOfficeSecurityRequirementsOperationFilterBase
    {
        protected override string ApiName => Constants.ApiName;
    }

    /// <summary>
    ///     Names operations HTTP method + route (<c>GetAlerts</c>, <c>PutSubscription</c>), which is what
    ///     the generated TypeScript client's function names (<c>getAlerts</c>, <c>putSubscription</c>) are
    ///     derived from. The Umbraco 18 line gets the same names from its own OpenAPI generator, so the
    ///     client stays identical across both lines.
    /// </summary>
    /// <remarks>
    ///     The route, not the action: <c>UpdateSubscription</c> is mapped to <c>PUT subscription</c>, and
    ///     naming it after the action would rename the client's <c>putSubscription</c>.
    /// </remarks>
    public class ErrorDashboardOperationIdHandler : OperationIdHandler
    {
        public ErrorDashboardOperationIdHandler(IOptions<ApiVersioningOptions> apiVersioningOptions)
            : base(apiVersioningOptions)
        {
        }

        protected override bool CanHandle(
            ApiDescription apiDescription,
            ControllerActionDescriptor controllerActionDescriptor)
            => controllerActionDescriptor.ControllerTypeInfo.Namespace?.StartsWith(
                "Our.Umbraco.ErrorDashboard.Controllers",
                StringComparison.Ordinal) is true;

        public override string Handle(ApiDescription apiDescription)
        {
            string method = apiDescription.HttpMethod ?? "Get";

            // The last literal route segment - "subscription" from
            // "umbraco/errordashboard/api/v1/subscription" - with kebab-case PascalCased word by word.
            string segment = (apiDescription.RelativePath ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Last(part => part.StartsWith('{') is false);
            string route = string.Concat(
                segment.Split('-', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

            return $"{char.ToUpperInvariant(method[0])}{method[1..].ToLowerInvariant()}{route}";
        }
    }
}
