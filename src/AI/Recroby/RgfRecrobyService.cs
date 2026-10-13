using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Recrovit.AI.Core.Workflows;
using Recrovit.AI.Runtime;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.AI;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Identity;
using Recrovit.RecroGridFramework.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.recroby.architecture
// RGF-DOC: rgf.core.ai-credit.integration
/// <summary>Executes protected conversation turns over the shared Recrovit.AI workflow runtime.</summary>
/// <remarks>Application access checks are the selected extension's responsibility; its default validation hook is a no-op.</remarks>
/// <param name="workflows">The shared workflow start and resume facade.</param>
/// <param name="runs">The inspector used to verify pending run identity and retrieve its context.</param>
/// <param name="protection">Data Protection for user-bound conversation tokens.</param>
/// <param name="extension">The fallback extension when an extension collection is not supplied.</param>
/// <param name="logger">The sink for execution failures and safe Credit denial diagnostics.</param>
/// <param name="credit">The server Credit settlement and state service.</param>
/// <param name="backendIdentity">Resolves the authenticated HTTP user's RGF identity.</param>
/// <param name="httpContextAccessor">Provides the authenticated HTTP principal.</param>
/// <param name="routes">Optional catalog for overrides and the configured-provider precheck.</param>
/// <param name="extensions">Application selectors; exactly one must match each application turn.</param>
public sealed class RgfRecrobyService(IWorkflowClient workflows, IWorkflowRunInspector runs, IDataProtectionProvider protection,
    IRgfRecrobyExtension extension, ILogger<RgfRecrobyService> logger, IAiCreditService credit, IRgfIdentityService backendIdentity,
    IHttpContextAccessor httpContextAccessor, AiRouteCatalog? routes = null,
    IEnumerable<IRgfRecrobyExtension>? extensions = null)
{
    private readonly IDataProtector protector = protection.CreateProtector("RGF.Recroby.ConversationIdentity.v1");

    /// <summary>Returns configured provider/model aliases and reasoning capabilities without credentials.</summary>
    /// <returns>The projected catalog, or an empty catalog when none was supplied.</returns>
    public RgfAiCatalogResponse GetCatalog() => routes is null
        ? new(string.Empty, [])
        : new(routes.Catalog.DefaultProvider, routes.Catalog.Providers.Select(provider =>
            new RgfAiProviderCatalogItem(provider.Id, provider.DefaultModel, provider.Models.Select(model =>
                new RgfAiModelCatalogItem(model.Id, model.Modes.Select(mode => mode.ToString()).ToArray(),
                    model.Efforts, model.DefaultMode?.ToString(), model.DefaultEffort)).ToArray())).ToArray());

    /// <summary>Executes a turn without application invocation context.</summary>
    /// <param name="userId">Nonblank user ID from the authenticated caller.</param>
    /// <param name="request">Current instruction and, for later turns, matching conversation ID and protected token.</param>
    /// <param name="cancellationToken">Cancellation propagated to workflow execution.</param>
    /// <returns>The mapped workflow response or a handled provider/Credit/execution failure.</returns>
    /// <remarks>New conversations use the generic workflow. Existing protected application conversations
    /// still require exactly one matching extension and invoke its validation hook.</remarks>
    public Task<RgfAiResponse> ExecuteAsync(string userId, RgfAiRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(userId, request, null, cancellationToken);

    /// <summary>Executes a turn with trusted server-side context used only to prepare a new run.
    /// A pending continuation always retains the stored run's original host context.</summary>
    /// <param name="userId">Nonblank user ID from the authenticated caller; Credit independently resolves HTTP identity.</param>
    /// <param name="request">A nonnull request with a nonblank current instruction and verified identity for later turns.</param>
    /// <param name="initialHostContext">Trusted application selection context, or null for a new generic conversation.</param>
    /// <param name="cancellationToken">Cancellation propagated to validation, preparation and workflow calls.</param>
    /// <returns>A response with protected identity on workflow output and per-turn Credit usage;
    /// handled failures preserve incoming identity and earlier settled usage. Credit state is queried for the authenticated HTTP user.</returns>
    /// <remarks>Application validation is invoked before preparation, but implementations must enforce access.
    /// Pending resume skips preparation and retains execution settings. Validation, preparation and model-resolution
    /// failures occur before the workflow error handler and can propagate. Cancellation and authorization errors propagate.
    /// Token statistics require backend RGF administrator permission; failed permission checks deny disclosure.</remarks>
    /// <exception cref="UnauthorizedAccessException">Identity is invalid, application selection is ambiguous,
    /// or a prepared workflow differs from the protected conversation workflow.</exception>
    /// <exception cref="ArgumentException">Required input is blank or model settings are invalid.</exception>
    public async Task<RgfAiResponse> ExecuteAsync(string userId, RgfAiRequest request,
        IWorkflowHostContext? initialHostContext, CancellationToken cancellationToken = default)
    {
        using var collector = new RgfRecrobyUsageCollector();
        var response = await ExecuteTurnAsync(userId, request, initialHostContext, cancellationToken);
        collector.Close();
        var discloseTokens = false;
        string? authenticatedUserId = null;
        try
        {
            var principal = httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated == true)
                authenticatedUserId = await backendIdentity.GetUserIdAsync(principal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Recroby Credit response identity could not be resolved.");
        }
        if (!string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            try
            {
                discloseTokens = RecroSec.IsAdmin(authenticatedUserId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Recroby token disclosure authorization could not be verified.");
            }
            try
            {
                var mode = await credit.GetCreditModeAsync(authenticatedUserId, cancellationToken);
                response.CreditState = new RgfAiCreditState
                {
                    Mode = mode,
                    Prepaid = mode == AiCreditMode.Prepaid
                        ? await credit.GetPrepaidBalanceAsync(authenticatedUserId, cancellationToken) : null,
                    Periods = mode == AiCreditMode.Periodic
                        ? await credit.GetPeriodCreditsAsync(authenticatedUserId, cancellationToken) : []
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Recroby Credit state could not be retrieved after execution.");
            }
        }
        response.Usage = null;
        response.CreditUsage = collector.Complete(discloseTokens);
        return response;
    }

    private async Task<RgfAiResponse> ExecuteTurnAsync(string userId, RgfAiRequest request,
        IWorkflowHostContext? initialHostContext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CurrentUserMessage);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = ReadIdentity(userId, request);
        WorkflowRunInfo? run = null;
        if (identity?.RequestId is not null)
        {
            run = runs.GetRunInfo(identity.RunId);
            if (run.RunId != identity.RunId || run.ConversationId != identity.ConversationId
                || run.WorkflowId != identity.WorkflowId)
                throw new UnauthorizedAccessException("The workflow run does not match the protected conversation identity.");
        }
        var generic = identity is not null ? identity.WorkflowId == "rgf.recroby" : initialHostContext is null;
        IRgfRecrobyExtension? selectedExtension = null;
        if (!generic)
        {
            var candidates = (extensions ?? [extension])
                .Where(candidate => candidate.CanHandle(identity?.WorkflowId, run?.HostContext ?? initialHostContext))
                .ToArray();
            if (candidates.Length != 1)
                throw new UnauthorizedAccessException("The trusted workflow context must identify exactly one application extension.");
            selectedExtension = candidates[0];
            await selectedExtension.ValidateAsync(new RgfRecrobyValidationContext(userId, identity?.ConversationId,
                identity?.RequestId is not null, run?.HostContext), request, cancellationToken);
        }

        // A pending run keeps its original model settings along with its context and input.
        var modelOverride = string.IsNullOrWhiteSpace(request.AiModelOverride) ? identity?.ModelOverride : request.AiModelOverride;
        if (identity?.RequestId is not null && modelOverride != identity.ModelOverride)
            throw new ArgumentException("Model settings cannot change while resuming a pending workflow.", nameof(request));

        RgfRecrobyExecution? execution = null;
        AIExecutionOptions? executionOptions = null;
        if (identity?.RequestId is null)
        {
            var context = new RgfRecrobyContext(userId, identity?.ConversationId) { InitialHostContext = generic ? null : initialHostContext };
            execution = generic
                ? new RgfRecrobyExecution("rgf.recroby", context, [])
                : await selectedExtension!.PrepareAsync(context, request, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(execution.WorkflowId);
            ArgumentNullException.ThrowIfNull(execution.HostContext);
            ArgumentNullException.ThrowIfNull(execution.InputData);
            if (identity is not null && identity.WorkflowId != execution.WorkflowId)
                throw new UnauthorizedAccessException("A conversation cannot be continued with another workflow.");
            executionOptions = routes is { HasConfiguredProvider: false } ? null
                : ResolveModel(modelOverride, request.AiReasoningEffortOverride);
        }

        if (routes is { HasConfiguredProvider: false })
            return new RgfAiResponse
            {
                Success = false, ErrorCode = RgfAiErrorCodes.AiProviderNotConfigured,
                Message = "No AI provider is configured. Configure an AI provider and model to use Recroby.",
                ConversationId = identity?.ConversationId ?? string.Empty,
                ConversationToken = request.ConversationToken
            };

        try
        {
            var output = identity?.RequestId is { } requestId
                ? await workflows.ResumeWorkflowAsync(identity.RunId, requestId,
                    new ContinuationResponse { UserInput = request.CurrentUserMessage }, cancellationToken)
                : await workflows.StartWorkflowAsync(execution!.WorkflowId, identity?.ConversationId,
                    userInstruction: request.CurrentUserMessage, inputData: execution.InputData,
                    cancellationToken: cancellationToken, hostContext: execution.HostContext, executionOptions: executionOptions);
            return new RgfAiResponse
            {
                Success = output.Status is WorkflowStatus.Completed or WorkflowStatus.WaitingForInput,
                Message = output.Continuation?.Message ?? output.FinalUserMessage ?? string.Empty,
                ConversationId = output.ConversationId,
                WorkflowRunId = output.RunId,
                WorkflowStatus = output.Status.ToString(),
                ConversationToken = protector.Protect(JsonSerializer.Serialize(new RgfRecrobyConversationIdentity(
                    userId, output.ConversationId, output.RunId, output.Continuation?.RequestId,
                    identity?.WorkflowId ?? execution!.WorkflowId, modelOverride)))
            };
        }
        catch (Exception exception) when (RgfAiCreditRequestGuard.TryGetRejection(exception, out var code, out var message))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning("Recroby Credit access was rejected with {ReasonCode} for conversation {ConversationId}.",
                code, identity?.ConversationId);
            return new RgfAiResponse
            {
                Success = false, ErrorCode = code, Message = message!,
                ConversationId = identity?.ConversationId ?? string.Empty,
                WorkflowRunId = identity?.RunId,
                // The run inspector exposes identity only; no execution status is available on this exception path.
                WorkflowStatus = null,
                ConversationToken = request.ConversationToken
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not UnauthorizedAccessException
            && HasCreditConfigurationFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogError(exception, "Recroby Credit configuration failed for conversation {ConversationId}.", identity?.ConversationId);
            return new RgfAiResponse
            {
                Success = false, ErrorCode = "AiCredit.InvalidConfiguration",
                Message = "Recroby could not complete the request due to a configuration error. Please contact your system administrator.",
                ConversationId = identity?.ConversationId ?? string.Empty,
                WorkflowRunId = identity?.RunId, WorkflowStatus = WorkflowStatus.Failed.ToString(),
                ConversationToken = request.ConversationToken
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not UnauthorizedAccessException)
        {
            logger.LogError(exception, "Recroby workflow execution failed for conversation {ConversationId}.", identity?.ConversationId);
            return new RgfAiResponse
            {
                Success = false, Message = "The AI workflow could not complete the request.",
                ConversationId = identity?.ConversationId ?? string.Empty,
                WorkflowRunId = identity?.RunId, WorkflowStatus = WorkflowStatus.Failed.ToString(),
                ConversationToken = request.ConversationToken
            };
        }
    }

    private static bool HasCreditConfigurationFailure(Exception exception)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is AiCreditConfigurationException) return true;
            if (current.InnerException is { } inner) pending.Push(inner);
            if (current is AggregateException aggregate)
                foreach (var nested in aggregate.InnerExceptions) pending.Push(nested);
        }
        return false;
    }

    private RgfRecrobyConversationIdentity? ReadIdentity(string userId, RgfAiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationToken))
        {
            if (!string.IsNullOrWhiteSpace(request.ConversationId))
                throw new UnauthorizedAccessException("A protected conversation identity is required.");
            return null;
        }
        try
        {
            var identity = JsonSerializer.Deserialize<RgfRecrobyConversationIdentity>(protector.Unprotect(request.ConversationToken));
            if (identity is null || identity.UserId != userId || identity.ConversationId != request.ConversationId
                || string.IsNullOrWhiteSpace(identity.ConversationId) || string.IsNullOrWhiteSpace(identity.RunId)
                || string.IsNullOrWhiteSpace(identity.WorkflowId))
                throw new UnauthorizedAccessException("The conversation does not belong to this user.");
            return identity;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new UnauthorizedAccessException("The conversation identity is invalid.", exception);
        }
    }

    private AIExecutionOptions? ResolveModel(string? modelOverride, string? effortOverride)
    {
        if (modelOverride is null && effortOverride is null) return null;
        if (routes is null)
            throw new InvalidOperationException("Model overrides require the configured Recrovit.AI provider catalog.");
        var parts = modelOverride?.Split('/');
        if (parts is null)
        {
            var defaultResolved = routes.Resolve(null, routes.DefaultProvider, null, effortOverride);
            return new AIExecutionOptions { ExecutionRoute = defaultResolved.RouteId, Reasoning = defaultResolved.Reasoning };
        }
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Use a model alias or provider/model alias for AiModelOverride.");
        var resolved = routes.Resolve(null, parts.Length == 2 ? parts[0] : routes.DefaultProvider, parts[^1], effortOverride);
        return new AIExecutionOptions { ExecutionRoute = resolved.RouteId, Reasoning = resolved.Reasoning };
    }
}
