using System.Runtime.CompilerServices;
using Recrovit.AI.Core;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.ai-credit.integration
internal sealed class RgfAiCreditIdentityStore
{
    private readonly ConditionalWeakTable<AIProviderRequestContext, Identity> identities = new();

    public void Associate(AIProviderRequestContext context, string userId)
    {
        var identity = identities.GetValue(context, _ => new Identity(userId));
        if (!StringComparer.Ordinal.Equals(identity.UserId, userId))
            throw new InvalidOperationException("The provider request context is already associated with another authenticated user.");
    }

    public string GetUserId(AIProviderRequestContext context)
        => identities.TryGetValue(context, out var identity) ? identity.UserId
            : throw new InvalidOperationException("The provider request context has no authenticated Credit identity.");

    private sealed record Identity(string UserId);
}
