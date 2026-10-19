using Microsoft.AspNetCore.Http;
using Recrovit.RecroGridFramework.Identity;
using Recrovit.RecroGridFramework.Metadata;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.legacy.rgf-ai.entity-metadata-reader
/// <summary>Provides the server-side Entity metadata input for future Recroby workflows.</summary>
/// <remarks>The host supplies its selected, already initialized grid and owning RGF context.
/// All metadata and domain permission decisions belong to Legacy. This component invokes no AI workflow.</remarks>
/// <param name="reader">The Legacy metadata reader.</param>
/// <param name="identity">The authenticated RGF identity service.</param>
/// <param name="http">The current request accessor; captured execution identities are also supported.</param>
public sealed class RgfRecrobyEntityMetadataService(IRgfEntityMetadataReader reader,
    IRgfIdentityService identity, IHttpContextAccessor http)
{
    /// <summary>Validates the selected context's owner and returns freshly authorized Legacy metadata.</summary>
    /// <exception cref="UnauthorizedAccessException">The selected context does not belong to the authenticated user.</exception>
    public async Task<RgfEntityMetadataResult> ReadAsync(IRecroGridContext context, RecroGrid selectedGrid,
        int selectedEntityId, IReadOnlyCollection<RecroGrid>? relationTargets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selectedGrid);
        var authenticated = await RgfAiIdentityResolver.ResolveAsync(identity, http, cancellationToken);
        var selectedHttp = context.HttpContext;
        var principal = selectedHttp?.User;
        if (authenticated is null || principal?.Identity?.IsAuthenticated != true ||
            await identity.GetUserIdAsync(principal) != authenticated.UserId)
            throw new UnauthorizedAccessException("The selected RGF context requires its authenticated owner.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(context.HttpContext, selectedHttp) || !ReferenceEquals(selectedHttp!.User, principal) ||
            principal.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("The selected RGF identity changed during validation.");
        return await reader.ReadAsync(context, selectedGrid, selectedEntityId, relationTargets, cancellationToken);
    }
}
