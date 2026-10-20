using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Recrovit.RecroGridFramework;
using Recrovit.RecroGridFramework.Data;
using Recrovit.RecroGridFramework.Metadata;
using Recrovit.RecroGridFramework.Security;

namespace Rgf.Metadata.Tests;

// Real configuration queries against a focused EF provider; permission grants use the existing RecroSec cache.
internal sealed class MetadataConfigurationFixture : IDisposable
{
    private readonly MetadataTestFixture host;
    private readonly Type contextType = FrameworkType("Data.RecroGridDbContextCF");
    private readonly Type entityType = FrameworkType("CF_RecroGrid_Entity");
    private readonly Type propertyType = FrameworkType("CF_RecroGrid_Property");
    private readonly Type optionType = FrameworkType("CF_RecroGrid_Options");
    private readonly Type permissionType = FrameworkType("CF_RecroSec_ObjectPermission");
    private readonly DbContextOptionsBuilder builder;
    private readonly Dictionary<int, string> names = [];
    private readonly Dictionary<string, int> permissions = [];
    private int nextPermission = 100000;
    private int nextOption = 1;
    private readonly Dictionary<int, Type> objectTypes = [];
    private readonly Array threadServices;
    private readonly object?[] oldServices;
    private readonly DbContext dataContext;
    internal readonly ReadGuard Guard = new();
    internal int ContextsCreated;
    internal IRgfEntityMetadataReader Reader { get; }

    internal MetadataConfigurationFixture(MetadataTestFixture host)
    {
        this.host = host;
        builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        var model = new ModelBuilder();
        foreach (var type in new[] { entityType, propertyType, optionType, permissionType })
        {
            var entity = model.Entity(type);
            foreach (var property in type.GetProperties())
            {
                if (property.PropertyType == typeof(string) || (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType).IsValueType)
                    entity.Property(property.PropertyType, property.Name);
                else entity.Ignore(property.Name);
            }
            var key = type == entityType ? "EntityId" : type == propertyType ? "PropertyId" : type == optionType ? "Id" : "ObjectPermissionId";
            entity.HasKey(key);
            entity.Property<int>(key).ValueGeneratedNever();
        }
        model.Entity(propertyType).HasOne(entityType, "RecroGrid_Entity").WithMany().HasForeignKey("EntityId");
        // Query interceptors are singleton services; do not retain a distinct EF provider for every test guard.
        builder.UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot()).UseModel(model.FinalizeModel()).AddInterceptors(Guard)
            .EnableServiceProviderCaching(false);
        // The normal Entity loader reads Properties and options through TSContext, including cache hits.
        threadServices = (Array)contextType.GetField("_threadSafeDbContext", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        oldServices = threadServices.Cast<object?>().ToArray();
        dataContext = Context();
        MetadataTestFixture.Set(dataContext, "_noTracking", true);
        ((MetadataTestFixture.GridContextProxy)host.Context).Database = (IRGDataContext)dataContext;
        var serviceType = FrameworkType("Data.RGThreadSafeDataService");
        Func<IRGDataContext> dataFactory = () => (IRGDataContext)dataContext;
        var service = Activator.CreateInstance(serviceType, [dataFactory]);
        for (int i = 0; i < threadServices.Length; i++) threadServices.SetValue(service, i);
        Func<DbContext> factory = Context;
        var typed = Expression.Lambda(typeof(Func<>).MakeGenericType(contextType),
            Expression.Convert(Expression.Invoke(Expression.Constant(factory)), contextType)).Compile();
        var readerType = FrameworkType("Metadata.RgfEntityMetadataReader");
        Reader = (IRgfEntityMetadataReader)Activator.CreateInstance(readerType, [typed])!;
        host.OverrideReader = Reader;
        host.UseDatabaseCacheModeWithMemoryPermissions();
    }

    private static Type FrameworkType(string name) => typeof(RecroGrid).Assembly.GetType("Recrovit.RecroGridFramework." + name, true)!;
    private DbContext Context() { ContextsCreated++; return (DbContext)Activator.CreateInstance(contextType, [builder.Options])!; }

    private void Seed(Type type, params (string Name, object? Value)[] values)
    {
        var row = Activator.CreateInstance(type)!;
        foreach (var (name, value) in values) type.GetProperty(name)!.SetValue(row, value);
        Guard.Seeding = true;
        try { using var database = Context(); database.Add(row); database.SaveChanges(); }
        finally { Guard.Seeding = false; }
    }

    internal void Entity(int id, string name, Type objectType, Type? gridType = null)
    {
        gridType ??= typeof(RecroGridDyn<>);
        var definition = $"{gridType.FullName},{gridType.Assembly.GetName().Name},{objectType.FullName},{objectType.Assembly.GetName().Name}";
        names[id] = name;
        objectTypes[id] = objectType;
        Seed(entityType, ("EntityId", id), ("EntityName", name), ("EntityVersion", "1"), ("TypeDefinition", definition),
            ("Category", 1), ("EntityComment", "Target description"));
        Grant(name, true);
        WarmEntity(id);
    }

    internal void Property(int entityId, int id, string name, int? target = null, bool readable = true, string? alias = null)
    {
        Seed(propertyType, ("EntityId", entityId), ("PropertyId", id), ("ColName", name), ("Alias", alias ?? name.Replace('.', '_')),
            ("Readable", readable), ("Editable", false), ("EntityBase", target), ("ListType", 1),
            ("FormType", target != null && !name.Contains('.') ? (int)RecroGrid.RGProperty.FormTypeEnum.Entity : (int)RecroGrid.RGProperty.FormTypeEnum.TextBox),
            ("PropertyComment", "Target field comment"));
        Grant(names[entityId] + "/" + (alias ?? name.Replace('.', '_')), true);
        WarmEntity(entityId);
    }

    internal void Option(int entityId, string key, string value, int? propertyId = null)
    {
        Seed(optionType, ("Id", nextOption++), ("EntityId", entityId), ("PropertyId", propertyId), ("OptionKey", key), ("OptionValue", value));
        WarmEntity(entityId);
    }

    internal void Grant(string name, bool allowed)
    {
        var security = FrameworkType("Security.PermissionForUser");
        var cache = (ConcurrentDictionary<string, ConcurrentDictionary<string, ObjectPermission>>)security
            .GetField("_objectPermissionsNameKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (!permissions.TryGetValue(name, out var id))
        {
            permissions[name] = id = nextPermission++;
            Seed(permissionType, ("ObjectPermissionId", id), ("ObjectName", name));
            cache[name] = new();
            cache[name][""] = new(id, name);
            cache[name]["0"] = new(id, name);
        }
        var cacheType = FrameworkType("RGCache");
        var memory = cacheType.GetMethod("GetMemoryCache", BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(string)], null)!.Invoke(null, ["RecroSec_UserPermissions"])!;
        foreach (var key in new[] { "null", "0" })
            memory.GetType().GetMethod("Set")!.Invoke(memory, [$"{id}_{key}_metadata-test-owner", new UserPermissions(allowed ? "R" : ""), 300]);
    }

    internal MetadataTestFixture.TestGrid Product(string path = "Supplier.CompanyName", int targetId = 10002, Type? gridType = null)
    {
        var grid = host.CreateGrid();
        MetadataTestFixture.Set(grid.GridEntity, "EntityObjectType", typeof(ProductRecord));
        grid.Fields.Clear();
        grid.Fields.Add(new() { PropertyId = 1001, EntityId = 10001, ColName = path, Alias = "Supplier_CompanyName",
            EntityBase = targetId, PropertyComment = "Original external description" });
        grid.Fields.Add(new() { PropertyId = 1002, EntityId = 10001, ColName = "Supplier", Alias = "Supplier", EntityBase = 10002,
            FormType = RecroGrid.RGProperty.FormTypeEnum.Entity });
        host.Seed(grid, "Supplier_CompanyName", "Supplier");
        Entity(10002, "Supplier", typeof(SupplierRecord), gridType);
        Property(10002, 2001, "CompanyName");
        return grid;
    }

    public void Dispose()
    {
        host.OverrideReader = null;
        ((MetadataTestFixture.GridContextProxy)host.Context).Database = null;
        for (int i = 0; i < threadServices.Length; i++) threadServices.SetValue(oldServices[i], i);
        dataContext.Dispose();
        foreach (var id in names.Keys)
            typeof(RecroGrid.RGEntity).GetMethod("RemoveFromCache", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [id]);
        using var context = Context();
        context.Database.EnsureDeleted();
    }

    internal void UpdateProperty(int id, string name, object? value) => Update(propertyType, id, name, value);
    internal void UpdateEntity(int id, string name, object? value) => Update(entityType, id, name, value);
    internal void Processing(string objectName, string column) => Update(permissionType, permissions[objectName], column, "MustNeverExecute");

    private void Update(Type type, int id, string name, object? value)
    {
        Guard.Seeding = true;
        try
        {
            using var context = Context();
            var entity = context.Find(type, id)!;
            type.GetProperty(name)!.SetValue(entity, value);
            context.SaveChanges();
        }
        finally { Guard.Seeding = false; }
        foreach (var entityId in names.Keys) WarmEntity(entityId);
    }

    private void WarmEntity(int id)
    {
        // Simulate the Entity definition cached by normal initialization. Property/option loading remains real.
        using var context = Context();
        var row = context.Find(entityType, id)!;
        var entity = (RecroGrid.RGEntity)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RecroGrid.RGEntity));
        foreach (var name in new[] { "EntityId", "EntityName", "EntityVersion", "EntityComment", "TypeDefinition" })
            MetadataTestFixture.Set(entity, name, entityType.GetProperty(name)!.GetValue(row));
        MetadataTestFixture.Set(entity, "Category", Enum.ToObject(typeof(RecroGrid.RGEntity.CategoryEnum), entityType.GetProperty("Category")!.GetValue(row)!));
        MetadataTestFixture.Set(entity, "EntityObjectType", objectTypes[id]);
        entity.SetTitle((string?)entityType.GetProperty("Title")!.GetValue(row));
        var key = $"{entity.EntityName.ToLowerInvariant()}_{entity.EntityVersion}";
        var map = (ConcurrentDictionary<int, string>)typeof(RecroGrid.RGEntity).GetField("_entityId2NameVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        map[id] = key;
        var cache = typeof(RecroGrid.RGEntity).GetProperty("_rgCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache.GetType().GetMethod("Set")!.Invoke(cache, [key, entity, 300]);
    }

    internal sealed class ProductRecord
    {
        public ProductRecord() => throw new InvalidOperationException("Business constructor forbidden.");
        public SupplierRecord? Supplier => throw new InvalidOperationException("Business getter forbidden.");
    }
    internal sealed class SupplierRecord : IRecroSecPreprocessing, IRecroSecPostprocessing
    {
        public SupplierRecord() => throw new InvalidOperationException("Business constructor forbidden.");
        public string CompanyName => throw new InvalidOperationException("Business getter forbidden.");
        public SupplierRecord? Parent => throw new InvalidOperationException("Business getter forbidden.");
        public int ObjectPermissionId => throw new InvalidOperationException("Record identity forbidden.");
        public int? RecroSecPreprocessing(RecroSecProcessingParam param)
            => throw new InvalidOperationException("Record preprocessing forbidden.");
        public void RecroSecPostprocessing(RecroSecProcessingParam param, UserPermissions permissions)
            => throw new InvalidOperationException("Record postprocessing forbidden.");
    }

    internal sealed class ReadGuard : SaveChangesInterceptor, IQueryExpressionInterceptor
    {
        internal bool Seeding;
        internal int WritesDuringRead;
        internal readonly HashSet<Type> QueriedTypes = [];
        public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
        {
            if (!Seeding) { WritesDuringRead++; throw new InvalidOperationException("Metadata writes forbidden."); }
            return result;
        }
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            new QueryGuard(QueriedTypes).Visit(queryExpression);
            return queryExpression;
        }
        private sealed class QueryGuard(HashSet<Type> queried) : ExpressionVisitor
        {
            protected override Expression VisitExtension(Expression node)
            {
                if (node is EntityQueryRootExpression root)
                {
                    if (!new[] { "CF_RecroGrid_Entity", "CF_RecroGrid_Property", "CF_RecroGrid_Options", "CF_RecroSec_ObjectPermission" }.Contains(root.EntityType.ClrType.Name))
                        throw new InvalidOperationException("Business query forbidden.");
                    queried.Add(root.EntityType.ClrType);
                }
                return base.VisitExtension(node);
            }
        }
    }
}
