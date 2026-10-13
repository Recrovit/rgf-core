using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.AI.Runtime;
using Recrovit.RecroGridFramework.AI;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.ai-credit.integration
/// <summary>Forwards known provider token usage to Credit accounting for the guard-associated user.</summary>
public sealed class RgfAiCreditUsageHandler : IAIProviderUsageHandler
{
    private readonly IAiCreditService credit;
    private readonly RgfAiCreditIdentityStore identities;
    private readonly ILogger<RgfAiCreditUsageHandler> logger;
    private readonly IServiceProvider services;

    /// <summary>Creates a scoped usage handler with the shared request identity store.</summary>
    /// <param name="credit">Required usage-settlement service.</param>
    /// <param name="logger">The sink for missing-usage diagnostics.</param>
    /// <param name="services">The scope containing the store registered by AddRgfRecroby.</param>
    public RgfAiCreditUsageHandler(IAiCreditService credit, ILogger<RgfAiCreditUsageHandler> logger,
        IServiceProvider services)
        => (this.credit, this.identities, this.logger, this.services) = (credit, services.GetRequiredService<RgfAiCreditIdentityStore>(), logger, services);

    /// <summary>Forwards reported usage without estimating, pricing or normalizing token counts.</summary>
    /// <param name="context">Provider metadata, usage and the original guard-associated request context.</param>
    /// <param name="cancellationToken">The usage hook token passed to the Credit service.</param>
    /// <remarks>Null usage is logged and skipped. Known usage is forwarded for every completion status,
    /// preserving zeros and nulls. The model alias is resolved from the configured catalog and execution route.
    /// Missing or inconsistent aliases fail without accounting. Accounting exceptions propagate to the runtime hook handler.</remarks>
    /// <exception cref="InvalidOperationException">Known usage has no guard-associated identity.</exception>
    /// <exception cref="AiCreditConfigurationException">The route does not match a configured provider/model alias.</exception>
    public async ValueTask OnUsageAsync(AIProviderUsageContext context, CancellationToken cancellationToken = default)
    {
        var collector = identities.GetCollector(context.RequestContext);
        collector?.ObserveCall(context.RequestId);
        if (context.Usage is not { } usage)
        {
            collector?.MarkUnknown(context.RequestId);
            logger.LogDebug("Provider request {RequestId} reported no token usage.", context.RequestId);
            return;
        }

        var userId = identities.GetUserId(context.RequestContext);
        try
        {
            var catalog = services.GetService<AiRouteCatalog>();
            var provider = catalog?.Catalog.Providers.SingleOrDefault(provider =>
                StringComparer.Ordinal.Equals(provider.Id, context.ProviderId));
            var model = provider?.Models.SingleOrDefault(model => StringComparer.Ordinal.Equals(
                AiRouteCatalog.CreateRouteId(provider.Id, model.Id), context.ExecutionRoute));
            if (model is null || string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(context.ProviderId))
                throw new AiCreditConfigurationException("The provider execution route has no matching configured model alias.");
            var creditUsage = new AiCreditUsage(context.RequestId, context.ProviderId,
                model.Id, context.ExecutionRoute, usage.InputTokens, usage.OutputTokens,
                usage.TotalTokens, usage.CachedInputTokens, usage.ReasoningTokens);
            var result = await credit.ApplyUsageAsync(userId, creditUsage, cancellationToken);
            collector?.Record(creditUsage, result);
        }
        catch
        {
            collector?.MarkUnknown(context.RequestId);
            throw;
        }
    }
}
