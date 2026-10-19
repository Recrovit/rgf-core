using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;

// RGF-DOC: rgf.core.initialization
// RGF-DOC: rgf.core.recroby.architecture
// RGF-DOC: rgf.core.ai-credit.integration
/// <summary>Registers the Recroby facade, workflow and provider Credit hooks.</summary>
public static class RgfRecrobyServiceCollectionExtensions
{
    /// <summary>Registers the Recroby facade and general workflow over the shared AI runtime.</summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configure">Optional compatibility-workflow configuration; repeated callbacks accumulate.</param>
    /// <returns>The supplied service collection.</returns>
    /// <remarks>Service and default-workflow registrations avoid duplicates. The host supplies the complete
    /// AI runtime/client, Identity and Credit services, and HTTP pipeline. WorkflowId must be nonblank.</remarks>
    public static IServiceCollection AddRgfRecroby(this IServiceCollection services, Action<RgfRecrobyOptions>? configure = null)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(RecrobyRegistration)))
        {
            services.AddSingleton(new RecrobyRegistration());
            Recrovit.AI.Core.DependencyInjection.ServiceCollectionExtensions.AddRecrovitAICore(services,
                Workflows.RgfRecrobyWorkflowConfigurationExtensions.AddDefaultRecrobyWorkflow);
        }
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        Metadata.RgfEntityMetadataServiceCollectionExtensions.AddRgfEntityMetadataReader(services);
        services.TryAddScoped<RgfRecrobyEntityMetadataService>();
        services.TryAddScoped<IRgfAiExecutionIdentityFactory, RgfAiExecutionIdentityFactory>();
        services.TryAddSingleton<RgfAiCreditIdentityStore>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Recrovit.AI.Core.IAIProviderRequestGuard, RgfAiCreditRequestGuard>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Recrovit.AI.Core.IAIProviderUsageHandler, RgfAiCreditUsageHandler>());
        var options = services.AddOptions<RgfRecrobyOptions>()
            .Validate(value => !string.IsNullOrWhiteSpace(value.WorkflowId), "A Recroby workflow is required.");
        if (configure is not null) options.Configure(configure);
        services.TryAddScoped<IRgfRecrobyExtension, DefaultExtension>();
        services.TryAddScoped<RgfRecrobyService>();
        return services;
    }

    private sealed class RecrobyRegistration;

    /// <summary>Registers an application-specific extension without creating another conversation runtime.</summary>
    /// <typeparam name="TExtension">The scoped application extension that selects and validates its own turns.</typeparam>
    /// <param name="services">The application service collection.</param>
    /// <param name="configure">Optional compatibility-workflow configuration.</param>
    /// <returns>The supplied service collection.</returns>
    /// <remarks>Registers each implementation type once; does not remove other selectors.
    /// Core requires exactly one matching extension per application turn.</remarks>
    public static IServiceCollection AddRgfRecroby<TExtension>(this IServiceCollection services,
        Action<RgfRecrobyOptions>? configure = null) where TExtension : class, IRgfRecrobyExtension
    {
        services.AddRgfRecroby(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IRgfRecrobyExtension)
            && descriptor.ImplementationType == typeof(TExtension)))
            services.AddScoped<IRgfRecrobyExtension, TExtension>();
        return services;
    }

    private sealed class DefaultExtension(IOptions<RgfRecrobyOptions> options) : IRgfRecrobyExtension
    {
        public bool CanHandle(string? workflowId, Recrovit.AI.Core.Workflows.IWorkflowHostContext? hostContext)
            => workflowId is not null && workflowId != "rgf.recroby" && workflowId == options.Value.WorkflowId;
        public ValueTask<RgfRecrobyExecution> PrepareAsync(RgfRecrobyContext context,
            Abstraction.Contracts.AI.RgfAiRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new RgfRecrobyExecution(options.Value.WorkflowId, context, []));
    }
}
