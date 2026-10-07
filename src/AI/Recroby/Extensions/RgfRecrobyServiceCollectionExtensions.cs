using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;

public static class RgfRecrobyServiceCollectionExtensions
{
    /// <summary>Adds the Recroby facade over the host's existing Recrovit.AI runtime and workflow registrations.</summary>
    public static IServiceCollection AddRgfRecroby(this IServiceCollection services, Action<RgfRecrobyOptions>? configure = null)
    {
        services.AddDataProtection();
        var options = services.AddOptions<RgfRecrobyOptions>()
            .Validate(value => !string.IsNullOrWhiteSpace(value.WorkflowId), "A Recroby workflow is required.");
        if (configure is not null) options.Configure(configure);
        services.TryAddScoped<IRgfRecrobyExtension, DefaultExtension>();
        services.TryAddScoped<RgfRecrobyService>();
        return services;
    }

    /// <summary>Registers one application extension for this host without creating another conversation runtime.</summary>
    public static IServiceCollection AddRgfRecroby<TExtension>(this IServiceCollection services,
        Action<RgfRecrobyOptions>? configure = null) where TExtension : class, IRgfRecrobyExtension
    {
        services.AddRgfRecroby(configure);
        services.Replace(ServiceDescriptor.Scoped<IRgfRecrobyExtension, TExtension>());
        return services;
    }

    private sealed class DefaultExtension(IOptions<RgfRecrobyOptions> options) : IRgfRecrobyExtension
    {
        public ValueTask<RgfRecrobyExecution> PrepareAsync(RgfRecrobyContext context,
            Abstraction.Contracts.AI.RgfAiRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new RgfRecrobyExecution(options.Value.WorkflowId, context, []));
    }
}
