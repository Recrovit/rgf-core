using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.RecroGridFramework;
using Recrovit.RecroGridFramework.Data;
using Recrovit.RecroGridFramework.Metadata;
using Recrovit.RecroGridFramework.Security;

namespace Rgf.Metadata.Tests;

// Simulates an already materialized grid. No database/provider exists: any lazy loading fails the test.
// Private setup follows the existing Legacy contract-test pattern; production visibility is unchanged.
internal sealed class MetadataTestFixture : IDisposable
{
    private readonly List<Action> restore = [];
    private readonly Type permissionType = typeof(RecroSec).Assembly.GetType("Recrovit.RecroGridFramework.Security.PermissionForUser", true)!;
    internal readonly ServiceProvider Services = new ServiceCollection().AddLogging().AddRgfEntityMetadataReader().BuildServiceProvider();
    internal readonly HttpContext Http = new DefaultHttpContext();
    internal readonly IRecroGridContext Context;
    internal readonly List<TestGrid> Grids = [];
    internal IRgfEntityMetadataReader? OverrideReader;
    internal IRgfEntityMetadataReader Reader => OverrideReader ?? Services.GetRequiredService<IRgfEntityMetadataReader>();

    internal MetadataTestFixture()
    {
        var applicationServices = typeof(RecroGridConfig).GetProperty("ApplicationServices", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldServices = applicationServices.GetValue(null);
        applicationServices.SetValue(null, Services);
        restore.Add(() => applicationServices.SetValue(null, oldServices));
        Http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "metadata-test-owner")], "test"));
        ReplaceStatic(typeof(RecroSec), "_inited", true);
        ReplaceStatic(typeof(RecroSec), "_enabled", true);
        ReplaceStatic(typeof(RecroSec), "_singleUserMode", false);
        ReplaceStatic(typeof(RecroSec), "FnGetCurrentUserId", (RecroSec.GetUserIdDelegate)(h => h.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        ReplaceStatic(typeof(RecroSec), "FnGetVirtualUserId", (RecroSec.GetUserIdDelegate)(h => h.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        ReplaceStatic(typeof(RecroSec), "FnIsInRole", (RecroSec.UserIsInRoleDelegate)((_, _, _) => false));
        var cache = new ConcurrentDictionary<string, ConcurrentDictionary<string, ObjectPermission>>();
        ReplaceStatic(permissionType, "_objectPermissionsNameKey", cache);
        Context = DispatchProxy.Create<IRecroGridContext, GridContextProxy>();
        ((GridContextProxy)Context).Http = Http;
    }

    internal TestGrid CreateGrid(int id = 10001, string? comment = "Entity\r\ncomment <untrusted>")
    {
        var grid = new TestGrid(Context);
        var entity = (RecroGrid.RGEntity)RuntimeHelpers.GetUninitializedObject(typeof(RecroGrid.RGEntity));
        Set(entity, "EntityId", id);
        Set(entity, "EntityName", $"MetadataEntity{id}");
        Set(entity, "EntityVersion", "2");
        Set(entity, "EntityComment", comment);
        Set(entity, "EntityObjectType", typeof(TestRecord));
        Set(entity, "_options", new Dictionary<string, RecroGrid.RGOption>(StringComparer.OrdinalIgnoreCase));
        Set(grid, "_gridEntity", entity);
        Set(grid, "_inited", true);
        Set(grid, "_rgProperties", grid.Fields);
        grid.Fields.Add(new() { PropertyId = 1001, EntityId = id, ColName = "Name", Alias = "Name", PropertyComment = "Field\ncomment" });
        Grids.Add(grid);
        Seed(grid, "Name");
        return grid;
    }

    internal void Seed(TestGrid grid, params string[] aliases)
        => SeedPermissions(grid.GridEntity.PermissionObjectName, aliases);

    internal void SeedPermissions(string name, params string[] aliases)
    {
        var cache = (ConcurrentDictionary<string, ConcurrentDictionary<string, ObjectPermission>>)permissionType
            .GetField("_objectPermissionsNameKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache[name] = new();
        cache[name][""] = new(0, name);
        foreach (var alias in aliases)
        {
            cache[$"{name}/{alias}"] = new();
            cache[$"{name}/{alias}"][""] = new(0, $"{name}/{alias}");
            cache[$"{name}/{alias}"]["0"] = new(0, $"{name}/{alias}");
        }
    }

    internal void SetOption(TestGrid grid, string name, string value)
    {
        var options = (Dictionary<string, RecroGrid.RGOption>)typeof(RecroGrid.RGEntity)
            .GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(grid.GridEntity)!;
        options[name] = new(name, value);
    }

    internal void SetProcessing(TestGrid grid, string? alias = null)
    {
        var cache = (ConcurrentDictionary<string, ConcurrentDictionary<string, ObjectPermission>>)permissionType
            .GetField("_objectPermissionsNameKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var name = grid.GridEntity.EntityName + (alias == null ? "" : "/" + alias);
        foreach (var value in cache[name].Values) value.Preprocessing = "MustNeverExecute, Missing.Type, Missing.Assembly";
    }

    internal void UseDatabaseCacheModeWithMemoryPermissions()
    {
        var field = typeof(RecroSec).GetField("_cacheMode", BindingFlags.Static | BindingFlags.NonPublic)!;
        ReplaceStatic(typeof(RecroSec), "_cacheMode", Enum.Parse(field.FieldType, "RGCache"));
        var cacheType = typeof(RecroGrid).Assembly.GetType("Recrovit.RecroGridFramework.RGCache", true)!;
        var cache = cacheType.GetMethod("GetMemoryCache", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(string)], null)!.Invoke(null, ["RecroSec_UserPermissions"]);
        var setter = cache!.GetType().GetMethod("Set")!;
        setter.Invoke(cache, ["0_null_metadata-test-owner", new UserPermissions("R"), 300]);
        setter.Invoke(cache, ["0_0_metadata-test-owner", new UserPermissions("R"), 300]);
        restore.Add(() => cacheType.GetMethod("RemoveMemoryCache", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(string)], null)!.Invoke(null, ["RecroSec_UserPermissions"]));
    }

    internal static void Set(object instance, string name, object? value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } field)
            { field.SetValue(instance, value); return; }
            if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } property)
            { property.SetValue(instance, value); return; }
        }
        throw new MissingMemberException(name);
    }

    private void ReplaceStatic(Type type, string name, object? value)
    {
        var field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var old = field.GetValue(null);
        restore.Add(() => field.SetValue(null, old));
        field.SetValue(null, value);
    }

    public void Dispose()
    {
        foreach (var action in restore.AsEnumerable().Reverse()) action();
        Services.Dispose();
    }

    internal sealed class TestRecord { public string? Name { get; set; } }

    internal class TestGrid(IRecroGridContext context) : RecroGrid(context)
    {
        internal readonly List<RGProperty> Fields = [];
        internal readonly HashSet<string> Denied = [];
        internal readonly List<string> PermissionCalls = [];
        internal bool Certified = true;
        internal bool UseBasePermissions { get; set; }
        protected override bool SupportsRecordIndependentMetadata => Certified;
        protected override List<RGProperty> RGProperties => throw new InvalidOperationException("Lazy Property access/generation is forbidden.");
        protected override Task InitializeAsync(IRecroGridContext context) => throw new InvalidOperationException("Initialization is forbidden.");
        protected override UserPermissions GetPermissions(string objectName, string? objectKey = null, object? dataRec = null, int? objectPermissionId = null)
        {
            if (dataRec != null) throw new InvalidOperationException("Business records are forbidden.");
            PermissionCalls.Add(objectName);
            if (UseBasePermissions) return base.GetPermissions(objectName, objectKey, dataRec, objectPermissionId);
            return new UserPermissions(!Denied.Contains(objectName));
        }
    }

    public class GridContextProxy : DispatchProxy
    {
        internal HttpContext Http = null!;
        internal IRGDataContext? Database;
        internal IRGFLogger Logger = new RGFLogger(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_UseAsyncInitialization" => true,
            "get_Logger" => Logger,
            "get_DbContext" => Database,
            "get_HttpContext" => Http,
            "get_IsTemporary" => false,
            _ => throw new InvalidOperationException($"Database, initialization and business access forbidden: {method.Name}")
        };
    }
}
