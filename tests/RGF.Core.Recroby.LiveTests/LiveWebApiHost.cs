using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RGF.Core.Recroby.LiveTests;

internal sealed class LiveWebApiHost : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> log = new();
    private Process? process;
    private Task[] readers = [];
    internal string HostId { get; } = Guid.NewGuid().ToString("N");
    internal string LogFilePath { get; private set; } = "";

    internal Task StartAsync(string directory, Uri address) => StartAsync(directory, address,
        async () =>
        {
            await BuildAsync(CreateBuildInfo(directory));
            Console.WriteLine("Live WebAPI Debug build succeeded (local project references, sequential MSBuild).");
        }, () => DiagnosticLog.ResolveTargetDirectoryAsync(directory),
        target => CreateApplicationInfo(directory, Path.Combine(target, "RGFDemo.WebAPI.dll"), address, HostId));

    internal async Task StartAsync(string directory, Uri address, Func<Task> build,
        Func<Task<string>> resolveTarget, Func<string, ProcessStartInfo> application,
        Func<Process, Task>? readiness = null)
    {
        try
        {
            EnsurePortAvailable(address);
            await build();
            var target = await resolveTarget();
            LogFilePath = Path.Combine(target, "logs", "ai", "events.jsonl");
            var dll = Path.Combine(target, "RGFDemo.WebAPI.dll");
            if (!File.Exists(dll)) throw new InvalidOperationException("A lefordított WebAPI DLL hiányzik.");
            EnsurePortAvailable(address);
            process = Process.Start(application(target))
                ?? throw new InvalidOperationException("A WebAPI nem indítható.");
            readers = [ReadAsync(process.StandardOutput), ReadAsync(process.StandardError)];
            Console.WriteLine($"Live WebAPI started: PID={process.Id}, host={HostId}; waiting for trusted HTTPS readiness.");
            using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
            if (readiness is null)
                await WaitForReadyAsync(client, () => process.HasExited, HostId, TimeSpan.FromMinutes(2));
            else await readiness(process);
            Console.WriteLine($"Live WebAPI ready: PID={process.Id}, host={HostId}, Debug/Development, Duende.");
        }
        catch (Exception exception)
        {
            await DisposeAsync();
            throw new InvalidOperationException($"A saját LiveTests WebAPI indítása sikertelen: {exception.Message}\n" +
                "Szűrt processznapló (csak hibakódok és kivételtípusok):\n" + string.Join('\n', log), exception);
        }
    }

    internal static ProcessStartInfo CreateApplicationInfo(string directory, string dll, Uri address, string hostId)
    {
        var start = CreateProcessInfo(directory);
        start.RedirectStandardInput = true;
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(address.GetLeftPart(UriPartial.Authority));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = address.GetLeftPart(UriPartial.Authority);
        start.Environment["JwtBearerOptions__Provider"] = "Duende";
        start.Environment["RGF_RECROBY_LIVE_HOST_ID"] = hostId;
        start.Environment["Recrovit__AI__Diagnostics__Normalized__Enabled"] = "true";
        start.Environment["Recrovit__AI__Diagnostics__Normalized__IncludeContent"] = "true";
        start.Environment["Recrovit__AI__Diagnostics__StructuredOutput__IncludeContent"] = "true";
        start.Environment["Recrovit__AI__Diagnostics__RawHttp__Enabled"] = "false";
        return start;
    }

    private static ProcessStartInfo CreateProcessInfo(string directory) => new("dotnet")
    {
        WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };

    internal static ProcessStartInfo CreateBuildInfo(string directory)
    {
        var build = CreateProcessInfo(directory);
        foreach (var argument in new[] { "build", "RGFDemo.WebAPI.csproj", "-c", "Debug", "-m:1",
            "-p:BuildInParallel=false", "-p:RestoreDisableParallel=true", "--nologo" })
            build.ArgumentList.Add(argument);
        return build;
    }

    internal async Task BuildAsync(ProcessStartInfo start)
    {
        using var build = Process.Start(start) ?? throw new InvalidOperationException("A WebAPI build nem indítható.");
        var output = new[] { ReadAsync(build.StandardOutput), ReadAsync(build.StandardError) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await build.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!build.HasExited) build.Kill(entireProcessTree: true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await build.WaitForExitAsync(stop.Token);
            throw;
        }
        finally { await Task.WhenAll(output).WaitAsync(TimeSpan.FromSeconds(10)); }
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"A WebAPI Debug build sikertelen (exit={build.ExitCode}); alkalmazásindítás nem történt.");
    }

    internal static void EnsurePortAvailable(Uri address)
    {
        if (!address.IsLoopback || address.Scheme != "https")
            throw new InvalidOperationException("A LiveTests saját WebAPI-ja helyi HTTPS-címet igényel.");
        // Mindkét loopback-család ellenőrzése szükséges a localhost Kestrel-kötéséhez.
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
            var listener = new TcpListener(ip, address.Port);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(); }
            catch (SocketException)
            {
                throw new InvalidOperationException($"A {address.Port} port foglalt. A LiveTests saját WebAPI-t indít; a meglévő processzt kézzel kell leállítani.");
            }
            finally { listener.Stop(); }
        }
    }

    internal static async Task WaitForReadyAsync(HttpClient client, Func<bool> exited, string hostId, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                if (exited()) throw new InvalidOperationException("A WebAPI a készenlét előtt kilépett; ellenőrizd az adatbázist és az inicializálást.");
                try
                {
                    using var response = await client.GetAsync("api/rgf/capabilities", cancellation.Token);
                    if (!response.Headers.TryGetValues("X-RGF-Live-Host", out var ids) || ids.SingleOrDefault() != hostId)
                        throw new InvalidOperationException("A címen nem a teszt által indított WebAPI válaszol.");
                    if (!response.Headers.TryGetValues("X-RGF-Live-Provider", out var providers) || providers.SingleOrDefault() != "Duende")
                        throw new InvalidOperationException("A WebAPI végleges hitelesítési providere nem Duende.");
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
                    if (!document.RootElement.GetProperty("recrobyEnabled").GetBoolean())
                        throw new InvalidOperationException("A saját WebAPI-ban a Recroby nincs engedélyezve.");
                    if (exited()) throw new InvalidOperationException("A WebAPI a készenlét közben kilépett.");
                    return;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                await Task.Delay(200, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("A WebAPI nem vált készen a megadott időn belül megbízható HTTPS-en.");
        }
    }

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Contains("Error occurred during LocalDB instance startup", StringComparison.Ordinal))
                log.Enqueue("LocalDB-példánynév feloldási/indítási hiba. Ez futó adatbázis mellett is előfordulhat eltérő Windows-felhasználói vagy izolált futtatási környezetben; ellenőrizd a példány hozzárendelését.");
            // Tetszőleges alkalmazásüzenet titkot is tartalmazhat; csak zárt formájú azonosítókat őrzünk meg.
            var safe = Regex.Matches(line, @"\b(?:[A-Za-z_][A-Za-z0-9_.]*Exception|(?:MSB|NU|CS|SQL)\d{3,5})\b")
                .Select(match => match.Value).Distinct().ToArray();
            if (safe.Length > 0) log.Enqueue(string.Join(' ', safe));
            while (log.Count > 40) log.TryDequeue(out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var owned = process;
        if (owned is null) return;
        try
        {
            if (!owned.HasExited)
            {
                try { await owned.StandardInput.WriteLineAsync("stop"); await owned.StandardInput.FlushAsync(); }
                catch (IOException) { }
                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await owned.WaitForExitAsync(graceful.Token); }
                catch (OperationCanceledException)
                {
                    if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                    using var forced = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await owned.WaitForExitAsync(forced.Token);
                }
            }
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine($"Live WebAPI stopped: PID={owned.Id}, exit={owned.ExitCode}.");
        }
        finally { owned.Dispose(); process = null; }
    }
}
