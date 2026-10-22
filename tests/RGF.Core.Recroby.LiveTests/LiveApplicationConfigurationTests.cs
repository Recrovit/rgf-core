using Microsoft.Extensions.Configuration;
using Xunit;

namespace RGF.Core.Recroby.LiveTests;

public sealed class LiveApplicationConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rgf-live-config-" + Guid.NewGuid().ToString("N"));

    public LiveApplicationConfigurationTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false, "JwtBearerOptions:Provider")]
    [InlineData(true, "Recrovit:OpenIdConnect:Provider")]
    public void ExplicitDuendeOverridesBaseEnvironmentAndSecretsWithoutChangingFiles(bool client, string key)
    {
        Write("appsettings.json", """{"JwtBearerOptions":{"Provider":"Other"},"Recrovit":{"OpenIdConnect":{"Provider":"Other"}}}""");
        Write("RGFDemo.BlazorApp.csproj", "<Project><PropertyGroup><UserSecretsId>assembly-id</UserSecretsId></PropertyGroup></Project>");
        Write("secrets.json", """{"JwtBearerOptions":{"Provider":"SecretProvider"},"Recrovit":{"OpenIdConnect":{"Provider":"SecretProvider"}},"Value":"secret"}""");
        var before = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);
        var stores = new List<string>();
        var loaded = LiveApplicationConfiguration.LoadApplication(directory, key, client,
            builder => builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "EnvironmentProvider" }),
            (builder, id) => { stores.Add(id); builder.AddJsonFile(Path.Combine(directory, "secrets.json")); }, "Duende");
        using var configuration = (ConfigurationRoot)loaded.Configuration;
        Assert.Equal(client ? ["assembly-id"] : new[] { "Duende" }, stores);
        Assert.Equal("Duende", configuration[key]);
        Assert.Equal("secret", configuration["Value"]);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ClientCanExplicitlyUseDuendeWhenAnotherProviderIsSelected()
    {
        WriteLaunchSettings();
        var values = ClientValues();
        values["Recrovit:OpenIdConnect:Provider"] = "Other";
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var settings = LiveApplicationConfiguration.ReadClientSettings(configuration, directory, "Duende");
        Assert.Equal("Duende", settings.Provider);
        Assert.Equal("configured-client", settings.ClientId);
        Assert.Equal("test-only-client-secret", settings.ClientSecret);
    }

    [Fact]
    public void WebApiLoadsSelectedProviderSecretsAfterJsonAndEnvironment()
    {
        // Exercise checkout discovery without requiring a real Demo checkout or user secrets.
        Write("rgf-core/src/RGF.Core.csproj", "<Project />");
        Write("rgf-demo/src/RGFDemo.WebAPI/appsettings.json", "{}");
        Write("rgf-demo/src/RGFDemo.BlazorApp/appsettings.json", "{}");
        Write("rgf-demo/src/RGFDemo.BlazorApp/RGFDemo.BlazorApp.csproj", "<Project />");
        Write("rgf-demo/src/RGFDemo.BlazorApp/Properties/launchSettings.json", "{}");
        var coreOutput = Path.Combine(directory, "rgf-core", "tests", "bin");
        var demoRoot = Path.Combine(directory, "rgf-demo");
        Assert.Equal(demoRoot, LiveApplicationConfiguration.FindRepositoryRoot(null, coreOutput));
        Assert.Equal(demoRoot, LiveApplicationConfiguration.FindRepositoryRoot(demoRoot, directory));
        Assert.Throws<InvalidOperationException>(() => LiveApplicationConfiguration.FindRepositoryRoot("relative-demo", coreOutput));
        Assert.Throws<InvalidOperationException>(() => LiveApplicationConfiguration.FindRepositoryRoot(directory, coreOutput));
        Assert.Throws<InvalidOperationException>(() => LiveApplicationConfiguration.FindRepositoryRoot(Path.Combine(directory, "missing"), coreOutput));
        Assert.Throws<InvalidOperationException>(() => LiveApplicationConfiguration.FindRepositoryRoot(null, directory));

        Write("appsettings.json", """{"JwtBearerOptions":{"Provider":"Base"},"Value":"base","BaseOnly":"base"}""");
        Write("appsettings.Development.json", """{"JwtBearerOptions":{"Provider":"Development"},"Value":"development","DevelopmentOnly":"development"}""");
        var selected = new List<string>();
        var loaded = LiveApplicationConfiguration.LoadApplication(directory, "JwtBearerOptions:Provider", false,
            builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
                { ["JwtBearerOptions:Provider"] = "Duende", ["Value"] = "environment", ["EnvironmentOnly"] = "environment" }),
            (builder, id) =>
            {
                selected.Add(id);
                builder.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["Value"] = "secrets", ["Recrovit:AI:Providers:OpenAI:DefaultModel"] = "configured-alias" });
            });
        using var configuration = (ConfigurationRoot)loaded.Configuration;
        Assert.Equal("Duende", loaded.SecretsId);
        Assert.Equal(["Duende"], selected);
        Assert.Equal("secrets", configuration["Value"]);
        Assert.Equal("base", configuration["BaseOnly"]);
        Assert.Equal("development", configuration["DevelopmentOnly"]);
        Assert.Equal("environment", configuration["EnvironmentOnly"]);
        Assert.Equal("configured-alias", configuration["Recrovit:AI:Providers:OpenAI:DefaultModel"]);
    }

    [Fact]
    public void BlazorDuendeSkipsProviderSecretsButKeepsLateAssemblySecrets()
    {
        Write("appsettings.json", """{"Recrovit":{"OpenIdConnect":{"Provider":"Duende"}},"Value":"base"}""");
        Write("RGFDemo.BlazorApp.csproj", "<Project><PropertyGroup><UserSecretsId>assembly-id</UserSecretsId></PropertyGroup></Project>");
        var selected = new List<string>();
        var loaded = LiveApplicationConfiguration.LoadApplication(directory, "Recrovit:OpenIdConnect:Provider", true,
            _ => { }, (builder, id) =>
            {
                selected.Add(id);
                builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Value"] = "assembly-secrets" });
            });
        using var configuration = (ConfigurationRoot)loaded.Configuration;
        Assert.Null(loaded.SecretsId);
        Assert.Equal(["assembly-id"], selected);
        Assert.Equal("assembly-secrets", configuration["Value"]);
    }

    [Fact]
    public void BlazorOtherProviderLoadsProviderSecretsBeforeAssemblySecrets()
    {
        Write("appsettings.json", """{"Recrovit":{"OpenIdConnect":{"Provider":"Other"}}}""");
        Write("RGFDemo.BlazorApp.csproj", "<Project><PropertyGroup><UserSecretsId>assembly-id</UserSecretsId></PropertyGroup></Project>");
        var selected = new List<string>();
        var loaded = LiveApplicationConfiguration.LoadApplication(directory, "Recrovit:OpenIdConnect:Provider", true,
            _ => { }, (builder, id) =>
            {
                selected.Add(id);
                builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Value"] = id });
            });
        using var configuration = (ConfigurationRoot)loaded.Configuration;
        Assert.Equal("Other", loaded.SecretsId);
        Assert.Equal(["Other", "assembly-id"], selected);
        Assert.Equal("assembly-id", configuration["Value"]);
    }

    [Fact]
    public void MissingProviderDoesNotSelectAnUnrelatedSecretsStore()
    {
        Write("appsettings.json", "{}");
        var loaded = LiveApplicationConfiguration.LoadApplication(directory, "JwtBearerOptions:Provider", false,
            _ => { }, (_, _) => Assert.Fail("No provider-specific secrets should be selected."));
        using var configuration = (ConfigurationRoot)loaded.Configuration;
        Assert.Null(loaded.SecretsId);
    }

    [Theory]
    [InlineData(false, "development")]
    [InlineData(true, "environment")]
    public void DevelopmentAndEnvironmentOverrideEarlierJson(bool addEnvironment, string expected)
    {
        Write("appsettings.json", """{"Value":"base"}""");
        Write("appsettings.Development.json", """{"Value":"development"}""");
        Write("appsettings.Production.json", """{"Value":"production"}""");
        var loaded = LiveApplicationConfiguration.LoadApplication(directory, "JwtBearerOptions:Provider", false,
            builder =>
            {
                if (addEnvironment) builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Value"] = "environment" });
            }, (_, _) => Assert.Fail("No provider secrets are configured."));
        using var configuration = (ConfigurationRoot)loaded.Configuration;
        Assert.Equal(expected, configuration["Value"]);
    }

    [Fact]
    public void ClientLoginIncludesOtherEnabledDownstreamScopesAndHonorsDisabledOverrides()
    {
        WriteLaunchSettings();
        var values = ClientValues();
        values["Recrovit:OpenIdConnect:DownstreamApis:UserInfoApi:Scopes"] = "role";
        values["Recrovit:OpenIdConnect:DownstreamApis:DisabledApi:Scopes"] = "excluded-scope";
        values["Recrovit:OpenIdConnect:Providers:Duende:DownstreamApis:DisabledApi:Disabled"] = "true";
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var settings = LiveApplicationConfiguration.ReadClientSettings(configuration, directory);
        Assert.Contains("role", settings.Scopes);
        Assert.DoesNotContain("excluded-scope", settings.Scopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientSettingsUseSelectedProviderAndDownstreamApi(bool overrideApi)
    {
        WriteLaunchSettings();
        var values = ClientValues();
        if (overrideApi)
        {
            values["Recrovit:OpenIdConnect:Providers:Duende:DownstreamApis:RgfApi:BaseUrl"] = "https://localhost:12345/";
            values["Recrovit:OpenIdConnect:Providers:Duende:DownstreamApis:RgfApi:Scopes:0"] = "provider-api-scope";
        }
        using var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build() as ConfigurationRoot;
        var settings = LiveApplicationConfiguration.ReadClientSettings(configuration!, directory);
        Assert.Equal("Duende", settings.Provider);
        Assert.Equal("configured-client", settings.ClientId);
        Assert.Equal("test-only-client-secret", settings.ClientSecret);
        Assert.Equal(new Uri("https://localhost:11900/"), settings.Authority);
        Assert.Equal(new Uri("https://localhost:44316/signin-oidc"), settings.RedirectUri);
        Assert.Equal(new Uri(overrideApi ? "https://localhost:12345/" : "https://localhost:11913/"), settings.ApiBaseAddress);
        Assert.Equal(new[] { "openid", "profile", overrideApi ? "provider-api-scope" : "api-scope" }
            .Order(StringComparer.Ordinal), settings.Scopes);
    }

    [Fact]
    public void HttpsLaunchProfileTakesItsConfiguredAddress()
    {
        Write("launch.json", """{"profiles":{"https":{"applicationUrl":"http://localhost:5000;https://localhost:7001"}}}""");
        Assert.Equal(new Uri("https://localhost:7001/"), LiveApplicationConfiguration.ReadClientOrigin(Path.Combine(directory, "launch.json")));
    }

    [Theory]
    [InlineData("Recrovit:OpenIdConnect:Providers:Duende:ClientSecret", "")]
    [InlineData("Recrovit:OpenIdConnect:Providers:Duende:Authority", "http://localhost:11900")]
    [InlineData("Recrovit:OpenIdConnect:Providers:Duende:CallbackPath", "//other.invalid/callback")]
    [InlineData("Recrovit:OpenIdConnect:DownstreamApis:RgfApi:BaseUrl", "http://localhost:11913")]
    [InlineData("Recrovit:OpenIdConnect:Providers:Duende:DownstreamApis:RgfApi:Disabled", "true")]
    [InlineData("Recrovit:OpenIdConnect:Providers:Duende:DownstreamApis:RgfApi:Scopes", "")]
    [InlineData("Recrovit:OpenIdConnect:Providers:Duende:ClientAuthenticationMethod", "PrivateKeyJwt")]
    public void InvalidClientConfigurationFailsBeforeLogin(string key, string value)
    {
        WriteLaunchSettings();
        var values = ClientValues();
        values[key] = value;
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<InvalidOperationException>(() => LiveApplicationConfiguration.ReadClientSettings(configuration, directory));
    }

    [Theory]
    [InlineData("https://localhost:44316/signin-oidc?code=one-time-code&state=expected", true)]
    [InlineData("https://localhost:44316/signin-oidc?code=one-time-code&state=wrong", false)]
    [InlineData("https://other.invalid/signin-oidc?code=one-time-code&state=expected", false)]
    [InlineData("https://localhost:44316/signin-oidc?error=access_denied&state=expected", false)]
    [InlineData("https://localhost:44316/signin-oidc?code=one-time-code&state=expected&state=expected", false)]
    public void AuthorizationCallbackRequiresConfiguredDestinationStateAndCode(string callback, bool accepted)
    {
        var expected = new Uri("https://localhost:44316/signin-oidc");
        if (accepted) Assert.Equal("one-time-code", DuendeLogin.ReadCallbackCode(new Uri(callback), expected, "expected"));
        else Assert.Throws<InvalidOperationException>(() => DuendeLogin.ReadCallbackCode(new Uri(callback), expected, "expected"));
    }

    private static Dictionary<string, string?> ClientValues() => new()
    {
        ["Recrovit:OpenIdConnect:Provider"] = "Duende",
        ["Recrovit:OpenIdConnect:Providers:Duende:Authority"] = "https://localhost:11900",
        ["Recrovit:OpenIdConnect:Providers:Duende:ClientId"] = "configured-client",
        ["Recrovit:OpenIdConnect:Providers:Duende:ClientSecret"] = "test-only-client-secret",
        ["Recrovit:OpenIdConnect:Providers:Duende:CallbackPath"] = "/signin-oidc",
        ["Recrovit:OpenIdConnect:Providers:Duende:Scopes:0"] = "openid",
        ["Recrovit:OpenIdConnect:Providers:Duende:Scopes:1"] = "profile",
        ["Recrovit:OpenIdConnect:DownstreamApis:RgfApi:BaseUrl"] = "https://localhost:11913/",
        ["Recrovit:OpenIdConnect:DownstreamApis:RgfApi:Scopes"] = "api-scope"
    };

    private void WriteLaunchSettings() => Write("Properties/launchSettings.json", """
        {
          // Preserve coverage of the client's IIS Express launch profile.
          "iisSettings": { "iisExpress": { "sslPort": 44316 } },
          "profiles": { "IIS Express": { "commandName": "IISExpress" } }
        }
        """);

    private void Write(string path, string content)
    {
        var fullPath = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(directory);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("rgf-live-config-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove an unexpected test directory.");
        Directory.Delete(target, recursive: true);
    }
}
