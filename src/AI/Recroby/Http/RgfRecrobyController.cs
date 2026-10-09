using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Identity;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Http;

/// <summary>Executes Recroby turns for the authenticated server-side user.</summary>
[ApiController]
[Authorize]
[Route("api/rgf/ai/recroby")]
public sealed class RgfRecrobyController(RgfRecrobyService recroby, IRgfIdentityService identity) : ControllerBase
{
    /// <summary>Returns the public catalog of configured providers and models.</summary>
    [HttpGet("catalog")]
    [ProducesResponseType<RgfAiCatalogResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RgfAiCatalogResponse>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = await identity.GetUserIdAsync(User);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        return Ok(recroby.GetCatalog());
    }

    /// <summary>Sends the current user instruction to Recroby.</summary>
    [HttpPost]
    [ProducesResponseType<RgfAiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RgfAiResponse>> ExecuteAsync([FromBody] RgfAiRequest request,
        CancellationToken cancellationToken)
    {
        var userId = await identity.GetUserIdAsync(User);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.CurrentUserMessage))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "A user instruction is required.");

        try
        {
            return Ok(await recroby.ExecuteAsync(userId, request, cancellationToken));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid Recroby request.");
        }
    }
}
