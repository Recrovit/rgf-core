using Microsoft.AspNetCore.Http;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Identity;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.recroby.architecture
/// <summary>Checks the authenticated HTTP user's Credit before each logical provider request.</summary>
public sealed class RgfAiCreditRequestGuard(IAiCreditService credit, IRgfIdentityService identity,
    IHttpContextAccessor httpContextAccessor) : IAIProviderRequestGuard
{
    private static readonly ConditionalWeakTable<AIProviderRequestRejectedException, string> RejectionUserNames = new();

    public async ValueTask BeforeRequestAsync(AIProviderRequestContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated != true)
            throw Reject(AiCreditDenialReason.UserNotFound);

        var userId = await identity.GetUserIdAsync(httpContext.User);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(userId))
            throw Reject(AiCreditDenialReason.UserNotFound);

        var access = await credit.CheckAccessAsync(userId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!access.IsAllowed)
        {
            var reason = access.DenialReason!.Value;
            var userName = reason == AiCreditDenialReason.UserDisabled ? GetUserName(httpContext.User) : null;
            cancellationToken.ThrowIfCancellationRequested();
            throw Reject(reason, userName);
        }
    }

    private string? GetUserName(ClaimsPrincipal principal)
    {
        try
        {
            var name = identity.GetUserCredentials(principal)?.UserName?.Trim();
            return !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && !name.Any(character =>
                char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                ? name : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
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
        _ => "AI Credit is not configured correctly for this user."
    };
}
