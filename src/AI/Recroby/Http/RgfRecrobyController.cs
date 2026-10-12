using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Identity;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Http;

// RGF-DOC: rgf.core.recroby.architecture
/// <summary>Executes Recroby turns for the authenticated server-side user.</summary>
/// <param name="recroby">The scoped server-side execution facade.</param>
/// <param name="identity">Resolves the current HTTP principal to an RGF user ID.</param>
[ApiController]
[Authorize]
[Route("api/rgf/ai/recroby")]
public sealed class RgfRecrobyController(RgfRecrobyService recroby, IRgfIdentityService identity) : ControllerBase
{
    /// <summary>Returns the public catalog of configured providers and models.</summary>
    /// <param name="cancellationToken">Cancellation checked before identity resolution.</param>
    /// <returns>HTTP 200 with catalog data, or 401 for a missing RGF user ID.</returns>
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
    /// <param name="request">The current instruction and optional protected conversation identity.</param>
    /// <param name="cancellationToken">Request cancellation forwarded to execution.</param>
    /// <returns>HTTP 200 with a workflow response, including handled failures; 400 for blank/invalid arguments,
    /// 401 for missing RGF identity, or 403 for rejected identity or application authorization.</returns>
    /// <remarks>The standard adapter supplies no initial application host context. Cancellation propagates.</remarks>
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
