using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;

namespace RGF.Core.Recroby.LiveTests;

internal sealed class LiveApplicationConfiguration : IDisposable
{
    internal string RepositoryRoot { get; }
    internal string WebApiDirectory { get; }
    internal IConfigurationRoot WebApi { get; }
    internal IConfigurationRoot Client { get; }
    internal string? WebApiSecretsId { get; }
    internal string? ClientSecretsId { get; }

    private LiveApplicationConfiguration(string root)
    {
        RepositoryRoot = root;
        WebApiDirectory = Path.Combine(root, "src", "RGFDemo.WebAPI");
        var clientDirectory = Path.Combine(root, "src", "RGFDemo.BlazorApp");
        (WebApi, WebApiSecretsId) = LoadApplication(WebApiDirectory, "JwtBearerOptions:Provider", false, provider: "Duende");
        try
        {
            (Client, ClientSecretsId) = LoadApplication(clientDirectory, "Recrovit:OpenIdConnect:Provider", true, provider: "Duende");
        }
        catch { (WebApi as IDisposable)?.Dispose(); throw; }
    }

    internal static LiveApplicationConfiguration Load() => new(FindRepositoryRoot(
        Environment.GetEnvironmentVariable("RGF_RECROBY_DEMO_ROOT"), AppContext.BaseDirectory));

    internal static (IConfigurationRoot Configuration, string? SecretsId) LoadApplication(
        string directory, string providerKey, bool blazorClient,
        Action<IConfigurationBuilder>? addEnvironment = null,
        Action<IConfigurationBuilder, string>? addSecrets = null, string? provider = null)
    {
        var builder = new ConfigurationBuilder().SetBasePath(directory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
        (addEnvironment ?? (configuration => configuration.AddEnvironmentVariables()))(builder);
        addSecrets ??= (configuration, id) => configuration.AddUserSecrets(id, reloadOnChange: true);
        string? selectedId;
        using (var initial = (ConfigurationRoot)builder.Build())
            selectedId = provider ?? initial[providerKey];
        // A kliens Duende esetén csak a saját projektjéhez tartozó titkokat tölti be.
        if (!string.IsNullOrWhiteSpace(selectedId) && (!blazorClient || selectedId != "Duende"))
            addSecrets(builder, selectedId);
        else selectedId = null;

        if (blazorClient)
        {
            var project = Path.Combine(directory, "RGFDemo.BlazorApp.csproj");
            var assemblySecretsId = File.Exists(project)
                ? XDocument.Load(project).Descendants().FirstOrDefault(element => element.Name.LocalName == "UserSecretsId")?.Value
                : null;
            if (!string.IsNullOrWhiteSpace(assemblySecretsId)) addSecrets(builder, assemblySecretsId);
        }
        if (provider is not null)
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [providerKey] = provider });
        return (builder.Build(), selectedId);
    }

    internal LiveClientSettings GetClientSettings()
    {
        var settings = ReadClientSettings(Client, Path.Combine(RepositoryRoot, "src", "RGFDemo.BlazorApp"), "Duende");
        var backendProvider = Required(WebApi, "JwtBearerOptions:Provider");
        var backendAuthority = Https(Required(WebApi, $"JwtBearerOptions:{backendProvider}:Authority"), "WebAPI authority");
        if (backendAuthority.AbsoluteUri.TrimEnd('/') != settings.Authority.AbsoluteUri.TrimEnd('/'))
            throw new InvalidOperationException("WebAPI and BlazorApp use different authentication authorities.");
        return settings;
    }

    internal static LiveClientSettings ReadClientSettings(IConfiguration configuration, string clientDirectory, string? provider = null)
    {
        const string root = "Recrovit:OpenIdConnect";
        provider ??= Required(configuration, root + ":Provider");
        var providerRoot = root + ":Providers:" + provider;
        var providerApis = configuration.GetSection(providerRoot + ":DownstreamApis");
        var sharedApis = configuration.GetSection(root + ":DownstreamApis");
        var apiNames = sharedApis.GetChildren().Concat(providerApis.GetChildren())
            .Select(section => section.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var downstreamScopes = new List<string>();
        string? apiUrl = null;
        foreach (var name in apiNames)
        {
            var providerApi = providerApis.GetSection(name);
            var sharedApi = sharedApis.GetSection(name);
            if (providerApi.GetValue<bool?>("Disabled") ?? sharedApi.GetValue<bool>("Disabled")) continue;
            var scopeSection = providerApi.GetSection("Scopes");
            var apiScopes = ReadScopes(scopeSection.Exists() ? scopeSection : sharedApi.GetSection("Scopes"));
            if (apiScopes.Length == 0) throw new InvalidOperationException($"BlazorApp downstream API scopes are missing: {name}.");
            downstreamScopes.AddRange(apiScopes);
            if (name.Equals("RgfApi", StringComparison.OrdinalIgnoreCase))
                apiUrl = !string.IsNullOrWhiteSpace(providerApi["BaseUrl"]) ? providerApi["BaseUrl"] : sharedApi["BaseUrl"];
        }
        var loginScopes = ReadScopes(configuration.GetSection(providerRoot + ":Scopes"));
        var scopes = loginScopes.Concat(downstreamScopes).Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!scopes.Contains("openid", StringComparer.Ordinal))
            throw new InvalidOperationException("BlazorApp login scopes must include openid.");
        var callback = Required(configuration, providerRoot + ":CallbackPath");
        if (!callback.StartsWith('/') || callback.StartsWith("//"))
            throw new InvalidOperationException("BlazorApp CallbackPath must be a local absolute path.");
        var origin = ReadClientOrigin(Path.Combine(clientDirectory, "Properties", "launchSettings.json"));
        var method = configuration[providerRoot + ":ClientAuthenticationMethod"];
        if (method is not (null or "0" or "ClientSecretPost"))
            throw new InvalidOperationException("LiveTests support the current Duende ClientSecretPost authentication method only.");
        return new LiveClientSettings(provider,
            Https(Required(configuration, providerRoot + ":Authority"), "BlazorApp authority"),
            Required(configuration, providerRoot + ":ClientId"),
            Required(configuration, providerRoot + ":ClientSecret"),
            new Uri(origin, callback), Https(apiUrl, "BlazorApp RgfApi BaseUrl"), scopes);
    }

    internal static Uri ReadClientOrigin(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var profiles = root.GetProperty("profiles");
        if (profiles.TryGetProperty("https", out var https))
        {
            var urls = https.GetProperty("applicationUrl").GetString()!.Split(';');
            return Https(urls.FirstOrDefault(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)), "BlazorApp HTTPS launch URL");
        }
        if (profiles.TryGetProperty("IIS Express", out _))
        {
            var port = root.GetProperty("iisSettings").GetProperty("iisExpress").GetProperty("sslPort").GetInt32();
            if (port is > 0 and <= 65535) return new Uri($"https://localhost:{port}/");
        }
        throw new InvalidOperationException("BlazorApp has no supported HTTPS launch profile.");
    }

    private static string[] ReadScopes(IConfigurationSection section) => section.Value is { } scalar
        ? scalar.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : section.GetChildren().Select(child => child.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();

    internal static string FindRepositoryRoot(string? demoRoot, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(demoRoot))
        {
            if (!Path.IsPathFullyQualified(demoRoot))
                throw new InvalidOperationException("RGF_RECROBY_DEMO_ROOT must be an absolute directory path.");
            return ValidateDemoRoot(demoRoot);
        }

        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "RGF.Core.csproj")))
                return ValidateDemoRoot(Path.Combine(directory.FullName, "..", "rgf-demo"));

        throw new InvalidOperationException("The rgf-core checkout was not found. Set RGF_RECROBY_DEMO_ROOT to the absolute rgf-demo directory.");
    }

    private static string ValidateDemoRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        string[] requiredFiles =
        [
            Path.Combine("src", "RGFDemo.WebAPI", "appsettings.json"),
            Path.Combine("src", "RGFDemo.BlazorApp", "appsettings.json"),
            Path.Combine("src", "RGFDemo.BlazorApp", "RGFDemo.BlazorApp.csproj"),
            Path.Combine("src", "RGFDemo.BlazorApp", "Properties", "launchSettings.json")
        ];
        foreach (var file in requiredFiles)
            if (!File.Exists(Path.Combine(fullPath, file)))
                throw new InvalidOperationException($"The Demo checkout is missing {file}. Set RGF_RECROBY_DEMO_ROOT to a complete rgf-demo directory.");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string Required(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]!
            : throw new InvalidOperationException($"Required application setting is missing: {key}.");

    private static Uri Https(string? value, string setting) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? uri
        : throw new InvalidOperationException($"{setting} must be an absolute HTTPS URI without credentials.");

    public void Dispose() { (WebApi as IDisposable)?.Dispose(); (Client as IDisposable)?.Dispose(); }
}

internal sealed class LiveClientSettings(string provider, Uri authority, string clientId, string clientSecret,
    Uri redirectUri, Uri apiBaseAddress, string[] scopes)
{
    internal string Provider { get; } = provider;
    internal Uri Authority { get; } = authority;
    internal string ClientId { get; } = clientId;
    internal string ClientSecret { get; } = clientSecret;
    internal Uri RedirectUri { get; } = redirectUri;
    internal Uri ApiBaseAddress { get; } = apiBaseAddress;
    internal string[] Scopes { get; } = scopes;
}
