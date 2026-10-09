using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Recrovit.AI.Core.Workflows;
using Recrovit.AI.Runtime;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using System.Security.Cryptography;
using System.Text.Json;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

/// <summary>Owns authorized conversation turns over the single Recrovit.AI workflow runtime.</summary>
public sealed class RgfRecrobyService(IWorkflowClient workflows, IWorkflowRunInspector runs, IDataProtectionProvider protection,
    IRgfRecrobyExtension extension, ILogger<RgfRecrobyService> logger, AiRouteCatalog? routes = null,
    IEnumerable<IRgfRecrobyExtension>? extensions = null)
{
    private readonly IDataProtector protector = protection.CreateProtector("RGF.Recroby.ConversationIdentity.v1");

    /// <summary>Returns public data from the existing AI catalog.</summary>
    public RgfAiCatalogResponse GetCatalog() => routes is null
        ? new(string.Empty, [])
        : new(routes.Catalog.DefaultProvider, routes.Catalog.Providers.Select(provider =>
            new RgfAiProviderCatalogItem(provider.Id, provider.DefaultModel, provider.Models.Select(model =>
                new RgfAiModelCatalogItem(model.Id, model.Modes.Select(mode => mode.ToString()).ToArray(),
                    model.Efforts, model.DefaultMode?.ToString(), model.DefaultEffort)).ToArray())).ToArray());

    public Task<RgfAiResponse> ExecuteAsync(string userId, RgfAiRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(userId, request, null, cancellationToken);

    /// <summary>Executes a turn with trusted server-side context used only to prepare a new run.
    /// A pending continuation always retains the stored run's original host context.</summary>
    public async Task<RgfAiResponse> ExecuteAsync(string userId, RgfAiRequest request,
        IWorkflowHostContext? initialHostContext, CancellationToken cancellationToken = default)
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
