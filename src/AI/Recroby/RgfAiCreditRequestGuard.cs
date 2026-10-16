using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Identity;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.ai-credit.integration
/// <summary>Checks the authenticated user's Credit before each logical provider request.</summary>
public sealed class RgfAiCreditRequestGuard : IAIProviderRequestGuard
{
    private readonly IAiCreditService credit;
    private readonly IRgfIdentityService identity;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly RgfAiCreditIdentityStore identities;

    /// <summary>Creates a scoped Credit guard using the shared identity association store.</summary>
    /// <param name="credit">Required access-check service.</param>
    /// <param name="identity">Resolves the RGF user from the authenticated HTTP principal.</param>
    /// <param name="httpContextAccessor">Provides the current HTTP principal.</param>
    /// <param name="services">The scope containing the store registered by AddRgfRecroby.</param>
    public RgfAiCreditRequestGuard(IAiCreditService credit, IRgfIdentityService identity,
        IHttpContextAccessor httpContextAccessor, IServiceProvider services)
        => (this.credit, this.identity, this.httpContextAccessor, this.identities)
            = (credit, identity, httpContextAccessor, services.GetRequiredService<RgfAiCreditIdentityStore>());

    private static readonly ConditionalWeakTable<AIProviderRequestRejectedException, string> RejectionUserNames = new();

    /// <summary>Checks authenticated Credit access and associates an allowed request with its user.</summary>
    /// <param name="context">The original provider context later used by usage notification.</param>
    /// <param name="cancellationToken">Cancellation of the access check.</param>
    /// <remarks>Missing authenticated identity is rejected without a Credit query. Denials use stable AiCredit codes.
    /// No credit is reserved or deducted by this hook; custom context cannot substitute for authenticated identity.</remarks>
    /// <exception cref="AIProviderRequestRejectedException">Authenticated identity is missing or the Credit service denies access.</exception>
    /// <exception cref="InvalidOperationException">The context is already associated with another user.</exception>
    public async ValueTask BeforeRequestAsync(AIProviderRequestContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = RgfAiExecutionContext.Current.Value;
        var authenticated = await RgfAiIdentityResolver.ResolveAsync(identity, httpContextAccessor, cancellationToken);
        if (authenticated is null) throw Reject(AiCreditDenialReason.UserNotFound);
        var userId = authenticated.UserId;
        var access = await credit.CheckAccessAsync(userId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (execution is { IsActive: false }) throw Reject(AiCreditDenialReason.UserNotFound);
        if (!access.IsAllowed)
        {
            var reason = access.DenialReason!.Value;
            var userName = reason == AiCreditDenialReason.UserDisabled ? authenticated.UserName : null;
            cancellationToken.ThrowIfCancellationRequested();
            throw Reject(reason, userName);
        }
        identities.Associate(context, userId);
    }

    private static AIProviderRequestRejectedException Reject(AiCreditDenialReason reason, string? userName = null)
    {
        if (!Enum.IsDefined(reason)) reason = AiCreditDenialReason.InvalidConfiguration;
        var rejection = new AIProviderRequestRejectedException(GetMessage(reason, userName), $"AiCredit.{reason}");
        if (reason == AiCreditDenialReason.UserDisabled && userName != null)
            RejectionUserNames.Add(rejection, userName);
        return rejection;
    }

    internal static bool TryGetRejection(Exception exception, out string? code, out string? message)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is AIProviderRequestRejectedException rejected)
            {
                foreach (var reason in Enum.GetValues<AiCreditDenialReason>())
                    if (rejected.ReasonCode == $"AiCredit.{reason}")
                    {
                        code = rejected.ReasonCode;
                        message = GetMessage(reason, RejectionUserNames.TryGetValue(rejected, out var userName) ? userName : null);
                        return true;
                    }
            }
            if (current.InnerException is { } inner) pending.Push(inner);
            if (current is AggregateException aggregate)
                foreach (var nested in aggregate.InnerExceptions) pending.Push(nested);
        }
        code = message = null;
        return false;
    }

    private static string GetMessage(AiCreditDenialReason reason, string? userName = null) => reason switch
    {
        AiCreditDenialReason.UserDisabled => $"AI Credit access is disabled for {userName ?? "this user"}.",
        AiCreditDenialReason.InsufficientCredit => "There is not enough AI Credit to start another model request.",
        AiCreditDenialReason.BalanceExpired => "The AI Credit balance has expired.",
        AiCreditDenialReason.UserNotFound => "An authenticated RGF user is required for AI Credit access.",
        AiCreditDenialReason.InfrastructureFailure => "AI Credit access could not be verified. Please try again later.",
        _ => "Recroby could not complete the request due to a configuration error. Please contact your system administrator."
    };
}
