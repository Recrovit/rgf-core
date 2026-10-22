using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RGF.Core.Recroby.LiveTests;

public sealed class LiveWebApiHostTests
{
    [Fact]
    public void BuildUsesDebugAndSequentialMsBuild()
    {
        var start = LiveWebApiHost.CreateBuildInfo("demo");
        Assert.Equal("dotnet", start.FileName);
        Assert.Equal("demo", start.WorkingDirectory);
        Assert.Equal(new[] { "build", "RGFDemo.WebAPI.csproj", "-c", "Debug", "-m:1",
            "-p:BuildInParallel=false", "-p:RestoreDisableParallel=true", "--nologo" }, start.ArgumentList);
    }

    [Fact]
    public void ApplicationUsesIsolatedDevelopmentDuendeAndDiagnosticsSettings()
    {
        var before = Environment.GetEnvironmentVariable("JwtBearerOptions__Provider");
        var start = LiveWebApiHost.CreateApplicationInfo("demo", "compiled.dll", new Uri("https://localhost:11913/"), "test-id");
        Assert.Equal("dotnet", start.FileName);
        Assert.Equal(new[] { "compiled.dll", "--urls", "https://localhost:11913" }, start.ArgumentList);
        Assert.Equal("demo", start.WorkingDirectory);
        Assert.Equal("Development", start.Environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Development", start.Environment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Duende", start.Environment["JwtBearerOptions__Provider"]);
        Assert.Equal("test-id", start.Environment["RGF_RECROBY_LIVE_HOST_ID"]);
        Assert.Equal("true", start.Environment["Recrovit__AI__Diagnostics__Normalized__IncludeContent"]);
        Assert.Equal("false", start.Environment["Recrovit__AI__Diagnostics__RawHttp__Enabled"]);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Equal(before, Environment.GetEnvironmentVariable("JwtBearerOptions__Provider"));
    }

    [Fact]
    public async Task OccupiedPortFailsBeforeBuildAndLeavesListenerUntouched()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        await using var host = new LiveWebApiHost();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync("unused", address,
            () => throw new Xunit.Sdk.XunitException("Build must not run."),
            () => throw new Xunit.Sdk.XunitException("Target resolution must not run."),
            _ => throw new Xunit.Sdk.XunitException("Application must not start.")));
        Assert.Contains("foglalt", error.Message);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, address.Port, TestContext.Current.CancellationToken);
        Assert.True(connection.Connected);
    }

    [Fact]
    public async Task FailedBuildDoesNotResolveOrLaunchApplication()
    {
        await using var host = new LiveWebApiHost();
        var start = Shell("Write-Output 'System.ComponentModel.Win32Exception Error occurred during LocalDB instance startup; password=private-test-marker'; exit 23");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync("unused", FreeAddress(),
            () => host.BuildAsync(start),
            () => throw new Xunit.Sdk.XunitException("Target resolution must not run."),
            _ => throw new Xunit.Sdk.XunitException("Application must not start.")));
        Assert.Contains("exit=23", error.Message);
        Assert.Contains("LocalDB-példánynév", error.Message);
        Assert.Contains("System.ComponentModel.Win32Exception", error.Message);
        Assert.DoesNotContain("private-test-marker", error.Message);
    }

    [Theory]
    [InlineData("other", "Duende", true, "nem a teszt")]
    [InlineData("owned", "Other", true, "nem Duende")]
    [InlineData("owned", "Duende", false, "nincs engedélyezve")]
    public async Task ReadinessRejectsForeignInstancesWrongProviderAndDisabledRecroby(string id, string provider, bool enabled, string message)
    {
        using var client = Client(() => Response(id, provider, enabled));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiveWebApiHost.WaitForReadyAsync(client, () => false, "owned", TimeSpan.FromSeconds(1)));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task ReadinessAcceptsOnlyOwnedReadyApplication()
    {
        using var client = Client(() => Response("owned", "Duende", true));
        await LiveWebApiHost.WaitForReadyAsync(client, () => false, "owned", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReadinessTimesOutWhenHttpsCannotConnect()
    {
        using var client = Client(() => throw new HttpRequestException("TLS test failure"));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            LiveWebApiHost.WaitForReadyAsync(client, () => false, "owned", TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ReadinessFailsImmediatelyWhenProcessExits()
    {
        using var client = Client(() => throw new Xunit.Sdk.XunitException("No request after process exit."));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiveWebApiHost.WaitForReadyAsync(client, () => true, "owned", TimeSpan.FromSeconds(1)));
        Assert.Contains("kilépett", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OwnedProcessStopsAfterNormalCompletionOrFailedInitialization(bool fail, bool force)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgf-live-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "RGFDemo.WebAPI.dll"), "test-only placeholder");
        int? ownedId = null;
        using var other = Process.Start(Shell("[Console]::In.ReadLine() | Out-Null"))!;
        await using var host = new LiveWebApiHost();
        try
        {
            var startup = host.StartAsync(directory, FreeAddress(), () => Task.CompletedTask,
                () => Task.FromResult(directory), _ => Shell(force ? "Start-Sleep -Seconds 60" : "[Console]::In.ReadLine() | Out-Null"),
                process =>
                {
                    ownedId = process.Id;
                    return fail ? Task.FromException(new InvalidOperationException("test initialization failure")) : Task.CompletedTask;
                });
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => startup);
            else { await startup; await host.DisposeAsync(); }
            Assert.NotNull(ownedId);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(ownedId.Value));
            Assert.False(other.HasExited);
        }
        finally
        {
            if (!other.HasExited) other.Kill(entireProcessTree: true);
            await other.WaitForExitAsync(TestContext.Current.CancellationToken);
            // Csak az ebben a tesztben létrehozott két ismert fájlrendszerelemet töröljük.
            File.Delete(Path.Combine(directory, "RGFDemo.WebAPI.dll"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task DisabledLiveModeDoesNotInitializeExternalDependencies()
    {
        if (LiveTestFixture.LiveEnabled) return;
        await using var fixture = new LiveTestFixture();
        await fixture.InitializeAsync();
        Assert.Equal("", fixture.ExpectedModelId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadCatalogAsync());
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(argument);
        return start;
    }

    private static Uri FreeAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/");
    }

    private static HttpClient Client(Func<HttpResponseMessage> response) => new(new Handler(response))
        { BaseAddress = new Uri("https://localhost/") };

    private static HttpResponseMessage Response(string id, string provider, bool enabled)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"recrobyEnabled\":" + (enabled ? "true" : "false") + "}") };
        response.Headers.Add("X-RGF-Live-Host", id);
        response.Headers.Add("X-RGF-Live-Provider", provider);
        return response;
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }
}
