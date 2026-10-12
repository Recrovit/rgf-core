using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.ai-credit.integration
/// <summary>Forwards known provider token usage to Credit accounting for the guard-associated user.</summary>
public sealed class RgfAiCreditUsageHandler : IAIProviderUsageHandler
{
    private readonly IAiCreditService credit;
    private readonly RgfAiCreditIdentityStore identities;
    private readonly ILogger<RgfAiCreditUsageHandler> logger;

    /// <summary>Creates a scoped usage handler with the shared request identity store.</summary>
    /// <param name="credit">Required usage-settlement service.</param>
    /// <param name="logger">The sink for missing-usage diagnostics.</param>
    /// <param name="services">The scope containing the store registered by AddRgfRecroby.</param>
    public RgfAiCreditUsageHandler(IAiCreditService credit, ILogger<RgfAiCreditUsageHandler> logger,
        IServiceProvider services)
        => (this.credit, this.identities, this.logger) = (credit, services.GetRequiredService<RgfAiCreditIdentityStore>(), logger);

    /// <summary>Forwards reported usage without estimating, pricing or normalizing token counts.</summary>
    /// <param name="context">Provider metadata, usage and the original guard-associated request context.</param>
    /// <param name="cancellationToken">The usage hook token passed to the Credit service.</param>
    /// <remarks>Null usage is logged and skipped. Known usage is forwarded for every completion status,
    /// preserving zeros and nulls. Accounting exceptions propagate to the runtime hook handler.</remarks>
    /// <exception cref="InvalidOperationException">Known usage has no guard-associated identity.</exception>
    public async ValueTask OnUsageAsync(AIProviderUsageContext context, CancellationToken cancellationToken = default)
    {
        if (context.Usage is not { } usage)
        {
            logger.LogDebug("Provider request {RequestId} reported no token usage.", context.RequestId);
            return;
        }

        var userId = identities.GetUserId(context.RequestContext);
        await credit.ApplyUsageAsync(userId, new AiCreditUsage(context.RequestId, context.ProviderId,
            context.ModelId, context.ExecutionRoute, usage.InputTokens, usage.OutputTokens,
            usage.TotalTokens, usage.CachedInputTokens, usage.ReasoningTokens), cancellationToken);
    }
}
