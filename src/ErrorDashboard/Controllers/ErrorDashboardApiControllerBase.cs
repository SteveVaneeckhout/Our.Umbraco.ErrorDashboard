using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Common.Filters;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;
using UmbConstants = Umbraco.Cms.Core.Constants;

namespace Our.Umbraco.ErrorDashboard.Controllers;

/// <summary>
///     Shared routing, authorization and serialization for every Error Dashboard endpoint.
/// </summary>
/// <remarks>
///     Settings-section access rather than Content: these are operational statistics about the site,
///     not editorial data, and the dashboards live alongside the other Settings tooling.
///     <para>
///         Note that no action anywhere in this API may declare a 401 <c>ProducesResponseType</c>.
///         Umbraco's <c>BackOfficeSecurityRequirementsTransformer</c> adds one to every operation, and a
///         second throws while the OpenAPI document is generated.
///     </para>
/// </remarks>
[ApiController]
[BackOfficeRoute("errordashboard/api/v{version:apiVersion}")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
[MapToApi(Constants.ApiName)]
[JsonOptionsName(UmbConstants.JsonOptionsNames.BackOffice)]
public class ErrorDashboardApiControllerBase : ControllerBase
{
    /// <summary>Ceiling on any page size, so a hand-written query cannot ask for the whole table.</summary>
    protected const int MaxTake = 500;

    /// <summary>Clamps a caller-supplied page size into something the server is willing to serve.</summary>
    protected static int ClampTake(int take) => take switch
    {
        <= 0 => 50,
        > MaxTake => MaxTake,
        _ => take,
    };

    /// <summary>Clamps a caller-supplied offset.</summary>
    protected static int ClampSkip(int skip) => skip < 0 ? 0 : skip;
}
