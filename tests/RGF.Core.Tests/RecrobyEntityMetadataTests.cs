using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.RecroGridFramework;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Identity;
using Recrovit.RecroGridFramework.Metadata;
using Rgf.Metadata.Tests;
using Xunit;

namespace RGF.Core.Tests;

[CollectionDefinition("Entity metadata identity", DisableParallelization = true)]
public sealed class EntityMetadataIdentityCollection { }

[Collection("Entity metadata identity")]
public sealed class RecrobyEntityMetadataTests
{
    [Theory]
    [InlineData("row")]
    [InlineData("row,column")]
    public async Task Registered_adapter_calls_real_Legacy_reader_and_revalidates_permissions(string level)
    {
        using var f = new MetadataTestFixture();
        var grid = f.CreateGrid();
        f.SetOption(grid, "RGO_RecroSecLevel", level);
        await using var services = CreateServices(f, out _);
        await using var scope = services.CreateAsyncScope();
        var adapter = scope.ServiceProvider.GetRequiredService<RgfRecrobyEntityMetadataService>();
        var result = await adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RgfMetadataReadStatus.Available, result.Status);
        Assert.Equal("Name", Assert.Single(result.Entity.Properties).Name);
        Assert.Equal("Entity\r\ncomment <untrusted>", result.Entity.EntityComment);
        Assert.Contains(grid.GridEntity.EntityName + "/Name", grid.PermissionCalls);
        grid.Denied.Add(grid.GridEntity.EntityName + "/Name");
        Assert.Empty((await adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken)).Entity.Properties);
        grid.Denied.Add(grid.GridEntity.EntityName);
        Assert.Equal(RgfMetadataReadStatus.Denied,
            (await adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Unauthenticated_or_different_owner_cannot_read_selected_context()
    {
        using var f = new MetadataTestFixture();
        var grid = f.CreateGrid();
        await using var services = CreateServices(f, out var http);
        var adapter = services.GetRequiredService<RgfRecrobyEntityMetadataService>();
        http.HttpContext = new DefaultHttpContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken));
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "different-owner")], "test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(grid.PermissionCalls);
    }

    [Fact]
    public async Task Entity_selection_and_owning_grid_context_are_validated_by_Legacy()
    {
        using var f = new MetadataTestFixture();
        var grid = f.CreateGrid();
        await using var services = CreateServices(f, out _);
        var adapter = services.GetRequiredService<RgfRecrobyEntityMetadataService>();
        Assert.Equal(RgfMetadataReadStatus.Unavailable,
            (await adapter.ReadAsync(f.Context, grid, 99999, cancellationToken: TestContext.Current.CancellationToken)).Status);
        var unrelated = DispatchProxy.Create<IRecroGridContext, MetadataTestFixture.GridContextProxy>();
        ((MetadataTestFixture.GridContextProxy)unrelated).Http = f.Http;
        Assert.Equal(RgfMetadataReadStatus.Denied,
            (await adapter.ReadAsync(unrelated, grid, 10001, cancellationToken: TestContext.Current.CancellationToken)).Status);
        Assert.Empty(grid.PermissionCalls);
    }

    private static ServiceProvider CreateServices(MetadataTestFixture fixture, out HttpContextAccessor http)
    {
        http = new HttpContextAccessor { HttpContext = fixture.Http };
        var identity = DispatchProxy.Create<IRgfIdentityService, TestIdentityProxy>();
        ((TestIdentityProxy)identity).UsePrincipalUserId = true;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(http);
        services.AddSingleton(identity);
        if (fixture.OverrideReader != null) services.AddSingleton(fixture.OverrideReader);
        services.AddRgfRecroby();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Captured_identity_can_call_Legacy_with_a_fresh_worker_context()
    {
        using var f = new MetadataTestFixture();
        await using var services = CreateServices(f, out var http);
        var captured = await services.GetRequiredService<IRgfAiExecutionIdentityFactory>()
            .CaptureAsync(TestContext.Current.CancellationToken);
        var workerHttp = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, captured.UserId)], "trusted-worker")) };
        ((MetadataTestFixture.GridContextProxy)f.Context).Http = workerHttp;
        var grid = f.CreateGrid();
        http.HttpContext = null;
        await captured.RunAsync(async cancellationToken =>
        {
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<RgfRecrobyEntityMetadataService>()
                .ReadAsync(f.Context, grid, 10001, cancellationToken: cancellationToken);
            Assert.Equal(RgfMetadataReadStatus.Available, result.Status);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Adapter_returns_automatically_resolved_external_fields_and_rechecks_target_permissions()
    {
        using var f = new MetadataTestFixture();
        using var configuration = new MetadataConfigurationFixture(f);
        var grid = configuration.Product();
        configuration.Option(10002, "RGO_RecroSecLevel", "row,column");
        configuration.Property(10002, 2002, "OtherField", alias: "Supplier_CompanyName");
        await using var services = CreateServices(f, out _);
        await using var scope = services.CreateAsyncScope();
        var adapter = scope.ServiceProvider.GetRequiredService<RgfRecrobyEntityMetadataService>();
        var result = await adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken);
        var field = Assert.Single(result.Entity.Properties, p => p.PropertyId == 1001);
        Assert.Equal("Supplier.CompanyName", field.Name);
        Assert.Equal((10002, 2001), (field.ExternalReference.EntityId, field.ExternalReference.PropertyId));
        configuration.Grant("Supplier/CompanyName", false);
        result = await adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Entity.Properties, p => p.PropertyId == 1001);
        Assert.DoesNotContain("Original external description", System.Text.Json.JsonSerializer.Serialize(result));
        configuration.Grant("Supplier/CompanyName", true);
        configuration.Grant("Supplier/Supplier_CompanyName", false);
        result = await adapter.ReadAsync(f.Context, grid, 10001, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2001, Assert.Single(result.Entity.Properties, p => p.PropertyId == 1001).ExternalReference.PropertyId);
        Assert.Equal(0, configuration.Guard.WritesDuringRead);
        Assert.Contains(configuration.Guard.QueriedTypes, t => t.Name == "CF_RecroGrid_Property");
    }
}
