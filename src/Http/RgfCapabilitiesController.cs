using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;

namespace Recrovit.RecroGridFramework.Core.Http;

/// <summary>Returns backend capabilities independently of the AI runtime.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/rgf/capabilities")]
public sealed class RgfCapabilitiesController(RgfCapabilitiesResponse capabilities) : ControllerBase
{
    /// <summary>Returns effective backend feature enablement.</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<RgfCapabilitiesResponse>(StatusCodes.Status200OK)]
    public ActionResult<RgfCapabilitiesResponse> Get() => Ok(capabilities);
}
