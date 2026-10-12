using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.recroby.architecture
public sealed class RgfAiCreditUsageHandler : IAIProviderUsageHandler
{
    private readonly IAiCreditService credit;
    private readonly RgfAiCreditIdentityStore identities;
    private readonly ILogger<RgfAiCreditUsageHandler> logger;

    public RgfAiCreditUsageHandler(IAiCreditService credit, ILogger<RgfAiCreditUsageHandler> logger,
        IServiceProvider services)
        => (this.credit, this.identities, this.logger) = (credit, services.GetRequiredService<RgfAiCreditIdentityStore>(), logger);

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
