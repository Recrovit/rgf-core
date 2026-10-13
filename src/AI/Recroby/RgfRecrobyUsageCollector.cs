using System.Diagnostics;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.ai-credit.integration
internal sealed class RgfRecrobyUsageCollector : IDisposable
{
    private static readonly AsyncLocal<RgfRecrobyUsageCollector?> CurrentSlot = new();
    private readonly RgfRecrobyUsageCollector? previous = CurrentSlot.Value;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly object gate = new();
    private readonly Dictionary<string, (AiCreditUsage Usage, AiCreditUsageResult Result)> settlements = new(StringComparer.Ordinal);
    private readonly HashSet<string> unknownRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> calls = new(StringComparer.Ordinal);
    private bool closed;

    public RgfRecrobyUsageCollector() => CurrentSlot.Value = this;
    public static RgfRecrobyUsageCollector? Current => CurrentSlot.Value;

    public void ObserveCall(string requestId)
    {
        lock (gate)
            if (!closed) calls.Add(requestId);
    }

    public void MarkUnknown(string requestId)
    {
        lock (gate)
            if (!closed && !settlements.ContainsKey(requestId)) unknownRequests.Add(requestId);
    }

    public void Record(AiCreditUsage usage, AiCreditUsageResult result)
    {
        lock (gate)
        {
            if (closed) return;
            unknownRequests.Remove(result.RequestId);
            if (result.IsNewSettlement) settlements.TryAdd(result.RequestId, (usage, result));
        }
    }

    public void Close()
    {
        lock (gate) closed = true;
    }

    public RgfAiCreditUsage Complete(bool discloseTokens)
    {
        lock (gate)
        {
            closed = true;
            stopwatch.Stop();
            var values = settlements.Values.ToArray();
            return new RgfAiCreditUsage
            {
                ExecutionTime = stopwatch.Elapsed,
                LlmCallCount = calls.Count,
                ChargedCredit = unknownRequests.Count == 0 ? values.Sum(value => value.Result.ChargedCredit) : null,
                Models = values.GroupBy(value => (value.Usage.ProviderId, value.Usage.ModelId)).Select(group =>
                    new RgfAiModelCreditUsage
                    {
                        ProviderId = group.Key.ProviderId, ModelId = group.Key.ModelId,
                        ChargedCredit = group.Sum(value => value.Result.ChargedCredit),
                        Tokens = discloseTokens ? SumTokens(group.Select(value => value.Usage).ToArray()) : null
                    }).ToArray(),
                Tokens = discloseTokens && values.Length > 0
                    ? unknownRequests.Count == 0 ? SumTokens(values.Select(value => value.Usage).ToArray()) : new RgfAiTokenUsage() : null
            };
        }
    }

    private static RgfAiTokenUsage SumTokens(AiCreditUsage[] usages) => new()
    {
        InputTokens = Sum(usages.Select(usage => usage.InputTokens)),
        OutputTokens = Sum(usages.Select(usage => usage.OutputTokens)),
        TotalTokens = Sum(usages.Select(usage => usage.TotalTokens)),
        CachedInputTokens = Sum(usages.Select(usage => usage.CachedInputTokens)),
        ReasoningTokens = Sum(usages.Select(usage => usage.ReasoningTokens))
    };

    private static long? Sum(IEnumerable<long?> counts)
    {
        long total = 0;
        foreach (var count in counts)
        {
            if (count is null) return null;
            if (count.Value > long.MaxValue - total) return null;
            total += count.Value;
        }
        return total;
    }

    public void Dispose()
    {
        lock (gate)
        {
            closed = true;
            settlements.Clear();
            unknownRequests.Clear();
            calls.Clear();
            stopwatch.Stop();
        }
        CurrentSlot.Value = previous;
    }
}
