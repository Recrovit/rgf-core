using Microsoft.AspNetCore.Http;
using Recrovit.RecroGridFramework.Identity;
using System.Globalization;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.ai-credit.integration
/// <summary>Captures a single-use execution identity from the current authenticated HTTP request.</summary>
public interface IRgfAiExecutionIdentityFactory
{
    /// <summary>Resolves and captures the current authenticated RGF user without retaining request services.</summary>
    /// <param name="cancellationToken">Cancellation of capture only.</param>
    /// <returns>A process-local identity for one awaited execution.</returns>
    /// <exception cref="UnauthorizedAccessException">The current HTTP request has no authenticated RGF user.</exception>
    Task<IRgfAiExecutionIdentity> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>A server-created, single-use identity for one awaited asynchronous execution chain.</summary>
/// <remarks>Not a client token or a persisted job credential. Capture a new identity for every conversation turn.</remarks>
public interface IRgfAiExecutionIdentity
{
    /// <summary>The authenticated RGF user captured by the server.</summary>
    string UserId { get; }

    /// <summary>Runs an operation with this identity and invalidates it when the operation exits.</summary>
    /// <param name="operation">The complete awaited turn, including response composition and accounting.</param>
    /// <param name="cancellationToken">An independently managed background execution token.</param>
    /// <returns>Completion of the operation.</returns>
    /// <exception cref="InvalidOperationException">The identity was used previously or its parent execution has closed.</exception>
    /// <exception cref="UnauthorizedAccessException">A different user is already executing in this chain.</exception>
    Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}

internal sealed record RgfAiAuthenticatedUser(string UserId, string? UserName);

internal sealed class RgfAiExecutionIdentityFactory(IRgfIdentityService identity, IHttpContextAccessor http)
    : IRgfAiExecutionIdentityFactory
{
    public async Task<IRgfAiExecutionIdentity> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var user = await RgfAiIdentityResolver.ResolveHttpAsync(identity, http, cancellationToken)
            ?? throw new UnauthorizedAccessException("An authenticated HTTP RGF user is required to capture execution identity.");
        return new ExecutionIdentity(user);
    }

    private sealed class ExecutionIdentity(RgfAiAuthenticatedUser user) : IRgfAiExecutionIdentity
    {
        private int used;
        public string UserId => user.UserId;

        public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (Interlocked.CompareExchange(ref used, 1, 0) != 0)
                throw new InvalidOperationException("An execution identity can only be used once.");
            var previous = RgfAiExecutionContext.Current.Value;
            var execution = new RgfAiExecutionContext(user, previous);
            try
            {
                if (previous is not null)
                {
                    if (!previous.IsActive) throw new InvalidOperationException("The parent execution has closed.");
                    if (previous.User.UserId != UserId)
                        throw new UnauthorizedAccessException("Nested execution cannot change the authenticated user.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                RgfAiExecutionContext.Current.Value = execution;
                await operation(cancellationToken);
            }
            finally
            {
                execution.Close();
                RgfAiExecutionContext.Current.Value = previous;
            }
        }
    }
}

internal sealed class RgfAiExecutionContext(RgfAiAuthenticatedUser user, RgfAiExecutionContext? parent)
{
    internal static readonly AsyncLocal<RgfAiExecutionContext?> Current = new();
    private int closed;
    private int recrobyTurnStarted;
    internal RgfAiAuthenticatedUser User { get; } = user;
    internal bool IsActive => Volatile.Read(ref closed) == 0 && (parent?.IsActive ?? true);
    internal void Close() => Interlocked.Exchange(ref closed, 1);
    internal void BeginRecrobyTurn()
    {
        if (!IsActive || Interlocked.CompareExchange(ref recrobyTurnStarted, 1, 0) != 0)
            throw new InvalidOperationException("Capture a new execution identity for each Recroby turn.");
    }
}

internal static class RgfAiIdentityResolver
{
    internal static async Task<RgfAiAuthenticatedUser?> ResolveAsync(IRgfIdentityService identity,
        IHttpContextAccessor http, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = RgfAiExecutionContext.Current.Value;
        if (execution is { IsActive: false }) return null;
        var current = await ResolveHttpAsync(identity, http, cancellationToken);
        if (execution is null) return current;
        if (!execution.IsActive || (http.HttpContext is not null && current is null)
            || (current is not null && current.UserId != execution.User.UserId)) return null;
        return execution.User;
    }

    internal static async Task<RgfAiAuthenticatedUser?> ResolveHttpAsync(IRgfIdentityService identity,
        IHttpContextAccessor http, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = http.HttpContext;
        var principal = context?.User;
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var userId = await identity.GetUserIdAsync(principal);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(userId)) return null;
        string? name = null;
        try
        {
            var candidate = identity.GetUserCredentials(principal)?.UserName?.Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 200 && !candidate.Any(character =>
                char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)) name = candidate;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(http.HttpContext, context) || !ReferenceEquals(context!.User, principal)
            || principal.Identity?.IsAuthenticated != true) return null;
        return new(userId, name);
    }
}
