using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;

public static class RgfRecrobyServiceCollectionExtensions
{
    /// <summary>Registers the Recroby facade and general workflow over the shared AI runtime.</summary>
    public static IServiceCollection AddRgfRecroby(this IServiceCollection services, Action<RgfRecrobyOptions>? configure = null)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(RecrobyRegistration)))
        {
            services.AddSingleton(new RecrobyRegistration());
            Recrovit.AI.Core.DependencyInjection.ServiceCollectionExtensions.AddRecrovitAICore(services,
                Workflows.RgfRecrobyWorkflowConfigurationExtensions.AddDefaultRecrobyWorkflow);
        }
        services.AddDataProtection();
        var options = services.AddOptions<RgfRecrobyOptions>()
            .Validate(value => !string.IsNullOrWhiteSpace(value.WorkflowId), "A Recroby workflow is required.");
        if (configure is not null) options.Configure(configure);
        services.TryAddScoped<IRgfRecrobyExtension, DefaultExtension>();
        services.TryAddScoped<RgfRecrobyService>();
        return services;
    }

    private sealed class RecrobyRegistration;

    /// <summary>Registers an application-specific extension without creating another conversation runtime.</summary>
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
