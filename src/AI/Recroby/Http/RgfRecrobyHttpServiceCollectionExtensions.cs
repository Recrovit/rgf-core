using Microsoft.Extensions.DependencyInjection;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Http;

public static class RgfRecrobyHttpServiceCollectionExtensions
{
    /// <summary>Registers the Recroby HTTP controller for custom composition; standard AddRGF initialization calls this automatically.</summary>
    public static IMvcBuilder AddRgfRecrobyHttpApi(this IServiceCollection services)
        => services.AddControllers().AddApplicationPart(typeof(RgfRecrobyController).Assembly);
}
