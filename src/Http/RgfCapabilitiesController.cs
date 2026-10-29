using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;

namespace Recrovit.RecroGridFramework.Core.Http;

// RGF-DOC: rgf.core.initialization
/// <summary>Returns backend capabilities independently of the AI runtime.</summary>
/// <param name="capabilities">Feature enablement captured during Core initialization.</param>
[ApiController]
[AllowAnonymous]
[Route("api/rgf/capabilities")]
public sealed class RgfCapabilitiesController(RgfCapabilitiesResponse capabilities) : ControllerBase
{
    /// <summary>Returns effective backend feature enablement.</summary>
    /// <returns>HTTP 200 with backend enablement, independent of provider configuration or connectivity.</returns>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<RgfCapabilitiesResponse>(StatusCodes.Status200OK)]
    public ActionResult<RgfCapabilitiesResponse> Get() => Ok(capabilities);
}
