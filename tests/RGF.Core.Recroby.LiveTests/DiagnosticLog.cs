using System.Text.Json;
using System.Diagnostics;
using Xunit;

namespace RGF.Core.Recroby.LiveTests;

// Kizárólag a beépített ILogger-eseményeket olvassa; nem végez alkalmazás- vagy providerhívást.
internal static class DiagnosticLog
{
    internal static async Task<string> ResolveFilePathAsync(string webApiDirectory)
        => Path.Combine(await ResolveTargetDirectoryAsync(webApiDirectory), "logs", "ai", "events.jsonl");

    internal static async Task<string> ResolveTargetDirectoryAsync(string webApiDirectory)
    {
        // A TargetDir lekérdezése csak projektértékelés: nem indít buildet vagy restore-t.
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = webApiDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "msbuild", "RGFDemo.WebAPI.csproj", "-nologo", "-verbosity:quiet",
            "-property:Configuration=Debug", "-m:1", "-property:BuildInParallel=false", "-getProperty:TargetDir" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Nem indítható az MSBuild projektértékelése.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(stop.Token);
            throw;
        }
        var directory = (await output).Trim();
        if (process.ExitCode != 0 || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException($"Nem oldható fel a WebAPI Debug TargetDir könyvtára: {await error}. Előbb fordítsd le a Demót.");
        return directory;
    }

    internal static long CapturePosition(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    internal static async Task<DiagnosticEvent[]> ReadAsync(LiveResponse response)
    {
        Assert.False(string.IsNullOrWhiteSpace(response.WorkflowRunId), "Hiányzó workflow-azonosító a Recroby válaszában.");
        Assert.False(string.IsNullOrWhiteSpace(response.ConversationId), "Hiányzó conversation-azonosító a Recroby válaszában.");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var path = response.LogFilePath;
        DiagnosticEvent[] events = [];
        do
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < response.LogStart)
                    throw new InvalidOperationException("A diagnosztikai napló futás közben lecserélődött vagy csonkolódott.");
                stream.Position = response.LogStart;
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                // A még írás alatt álló utolsó sort a következő olvasás dolgozza fel.
                events = text.Split('\n').SkipLast(1).Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => new DiagnosticEvent(DiagnosticEvent.Parse(line)))
                    .Where(row => row.String("WorkflowRunId") == response.WorkflowRunId
                        && row.String("ConversationId") == response.ConversationId).ToArray();
                var requests = Traffic(events, "Request");
                var replies = Traffic(events, "Response");
                if (requests.Length > 0 && requests.All(request => replies.Any(reply =>
                        reply.String("InvocationId") == request.String("InvocationId")))
                    && events.Any(row => row.String("Logger") == "Recrovit.AI.Core.Workflows.WorkflowRuntime"
                        && row.String("MessageTemplate").StartsWith("Workflow output ", StringComparison.Ordinal))
                    && events.Any(row => row.String("Logger") == "Recrovit.AI.Core.Workflows.MafWorkflowAIInvoker"
                        && row.Value("StructuredContent").ValueKind == JsonValueKind.String))
                    return events;
            }
            await Task.Delay(150);
        } while (DateTime.UtcNow < deadline);
        throw new InvalidOperationException($"Diagnosztikai hiba: hiányzó normalizált kérés/válasz, feldolgozott agentkimenet vagy workflow-kimenet. " +
            $"Conversation={response.ConversationId}, workflow={response.WorkflowRunId}, események={events.Length}, napló={path}. " +
            "Ellenőrizd a futó Debug/Development WebAPI effektív Diagnostics beállításait, az NLog Trace szabályokat és az IncludeScopes értékét. Nem történt új LLM-kérés.");
    }

    internal static DiagnosticEvent[] Traffic(IEnumerable<DiagnosticEvent> events, string direction) => events
        .Where(row => row.String("Logger") == "Recrovit.AI.Diagnostics.Normalized"
            && row.Value("TrafficContent").ValueKind == JsonValueKind.String)
        .Where(row => row.Traffic.GetProperty("Direction").GetString() == direction).ToArray();

    internal static IEnumerable<JsonElement> Contents(DiagnosticEvent row) => row.Traffic.GetProperty("Payload")
        .GetProperty("Messages").EnumerateArray().SelectMany(message => message.GetProperty("Contents").EnumerateArray());
}

internal sealed class DiagnosticEvent(JsonElement value)
{
    internal JsonElement Value(string name) => value.TryGetProperty(name, out var property) ? property : default;
    internal string String(string name) => Value(name).ValueKind == JsonValueKind.String ? Value(name).GetString()! : "";
    internal JsonElement Traffic => Parse(String("TrafficContent"));
    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
