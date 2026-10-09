using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Http;
using Recrovit.RecroGridFramework.Core.Http;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core.Workflows;
using Recrovit.AI.Runtime;
using Recrovit.AI.Core;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Extensions;
using Xunit;

namespace RGF.Core.Tests;

public sealed class AutomaticRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StandardTestApplicationStartsWithoutAiRegistrationOrProvider(bool? configuredEnabled)
    {
        var enabled = configuredEnabled ?? true;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recrovit:RecroGridFramework:Recroby:Enabled"] = configuredEnabled?.ToString()
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddRGF();
        var credit = new AiCreditTestState();
        credit.Register(builder.Services);
        Assert.Equal(enabled ? 1 : 0, builder.Services.Count(d => d.ImplementationType == typeof(RgfAiCreditRequestGuard)));
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var capabilities = app.Services.GetRequiredService<RgfCapabilitiesResponse>();
        Assert.Equal(enabled, capabilities.RecrobyEnabled);
        var controllers = new ControllerFeature();
        app.Services.GetRequiredService<ApplicationPartManager>().PopulateFeature(controllers);
        Assert.Contains(controllers.Controllers, type => type.AsType() == typeof(RgfCapabilitiesController));
        Assert.Equal(enabled, controllers.Controllers.Any(type => type.AsType() == typeof(RgfRecrobyController)));
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var json = await client.GetStringAsync("/api/rgf/capabilities", TestContext.Current.CancellationToken);
        Assert.Contains(enabled ? "true" : "false", json);
        await using var scope = app.Services.CreateAsyncScope();
        if (enabled)
        {
            Assert.IsType<TestCreditService>(scope.ServiceProvider.GetRequiredService<IAiCreditService>());
            Assert.Single(scope.ServiceProvider.GetServices<IAIProviderRequestGuard>().OfType<RgfAiCreditRequestGuard>());
            var response = await scope.ServiceProvider.GetRequiredService<RgfRecrobyService>()
                .ExecuteAsync("owner", new() { CurrentUserMessage = "Hello" }, TestContext.Current.CancellationToken);
            Assert.False(response.Success);
            Assert.Equal(RgfAiErrorCodes.AiProviderNotConfigured, response.ErrorCode);
            Assert.Empty(response.ConversationId);
            Assert.Null(response.WorkflowRunId);
            Assert.Null(response.ConversationToken);
        }
        else
        {
            Assert.Null(scope.ServiceProvider.GetService<RgfRecrobyService>());
            Assert.Null(app.Services.GetService<IWorkflowClient>());
        }
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandardInitializationRegistersRecrobyOnce(bool useWebHost)
    {
        var builder = WebApplication.CreateBuilder();
        if (useWebHost)
        {
            builder.WebHost.AddRGF();
            builder.WebHost.AddRGF();
        }
        else
        {
            builder.AddRGF();
            builder.AddRGF();
        }
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(RgfRecrobyService));
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IWorkflowClient));
        var guard = Assert.Single(builder.Services, descriptor => descriptor.ImplementationType == typeof(RgfAiCreditRequestGuard));
        Assert.Equal(ServiceLifetime.Scoped, guard.Lifetime);
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal("rgf.recroby", provider.GetRequiredService<WorkflowRegistry>().Get("rgf.recroby").Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationWorkflowsSurviveBothRegistrationOrders(bool applicationFirst)
    {
        var builder = WebApplication.CreateBuilder();
        void AddApplication() => builder.Services.AddRecrovitAI(builder.Configuration,
            components => components.AddComponentsFrom<TestApplicationWorkflow>());
        if (applicationFirst) AddApplication();
        builder.AddRGF();
        if (!applicationFirst) AddApplication();
        AddApplication();
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal("rgf.recroby", provider.GetRequiredService<WorkflowRegistry>().Get("rgf.recroby").Id);
        Assert.Equal("application.workflow", provider.GetRequiredService<WorkflowRegistry>().Get("application.workflow").Id);
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IWorkflowClient));
        Assert.Single(provider.GetServices<IRecrovitAIWorkflowConfigurator>());
    }

    [RecrovitAIComponent(ServiceLifetime.Singleton)]
    public sealed class TestApplicationWorkflow : IRecrovitAIWorkflowConfigurator
    {
        public void Configure(RecrovitWorkflowConfiguration configuration)
        {
            configuration.Contracts.Register<string>("application.response");
            configuration.Workflows.Register(new WorkflowDefinition
            {
                Id = "application.workflow", Name = "Application workflow", EntryAgentId = "application.chat",
                Agents = [new AgentDefinition { Id = "application.chat", Name = "Chat", Type = AgentType.Executor,
                    Instruction = "Respond", OutputContractId = "application.response" }]
            });
        }
    }
}
