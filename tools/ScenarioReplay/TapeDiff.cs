using System.Text.Json;
using System.Text.Json.Nodes;

namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// Compares two replay tapes (JSONL produced by <see cref="CaptureWriter"/>)
/// for regression testing. Records are aligned by file order (the runner
/// already emits them in monotonic, deterministic order). A configurable
/// set of top-level keys is stripped from each record before comparison so
/// non-deterministic noise (wall-clock <c>t</c>, engine-allocated
/// <c>orderId</c>, ...) does not produce false positives.
///
/// The diff stops scanning at the first record-level divergence to keep the
/// report compact, but counts add / remove / modify totals across the whole
/// pair so a CI summary line can include the impact at a glance.
/// </summary>
public static class TapeDiff
{
    public sealed record DiffResult(
        int BaselineCount,
        int CandidateCount,
        int Modified,
        int OnlyInBaseline,
        int OnlyInCandidate,
        DiffDivergence? FirstDivergence)
    {
        public bool IsEquivalent => Modified == 0 && OnlyInBaseline == 0 && OnlyInCandidate == 0;
    }

    public sealed record DiffDivergence(
        int Index,
        DivergenceKind Kind,
        string? BaselineLine,
        string? CandidateLine);

    public enum DivergenceKind { Modified, OnlyInBaseline, OnlyInCandidate }

    /// <summary>Loads + normalises every record in a tape file.</summary>
    public static List<NormalisedRecord> Load(string path, IReadOnlySet<string> ignoreFields)
    {
        var list = new List<NormalisedRecord>();
        int lineNo = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(raw);
            }
            catch (JsonException jx)
            {
                throw new FormatException($"{path} line {lineNo}: invalid JSON ({jx.Message})", jx);
            }
            if (node is not JsonObject obj)
                throw new FormatException($"{path} line {lineNo}: expected JSON object");
            var normalized = NormaliseRecord(obj, ignoreFields);
            list.Add(new NormalisedRecord(lineNo, raw, normalized));
        }
        return list;
    }

    private static string NormaliseRecord(JsonObject obj, IReadOnlySet<string> ignoreFields)
    {
        var clone = new JsonObject();
        foreach (var kv in obj)
        {
            if (ignoreFields.Contains(kv.Key)) continue;
            clone[kv.Key] = kv.Value?.DeepClone();
        }
        return clone.ToJsonString();
    }

    public static DiffResult Compare(
        IReadOnlyList<NormalisedRecord> baseline,
        IReadOnlyList<NormalisedRecord> candidate)
    {
        int min = Math.Min(baseline.Count, candidate.Count);
        int modified = 0;
        DiffDivergence? first = null;
        for (int i = 0; i < min; i++)
        {
            if (!string.Equals(baseline[i].Normalised, candidate[i].Normalised, StringComparison.Ordinal))
            {
                modified++;
                first ??= new DiffDivergence(i, DivergenceKind.Modified, baseline[i].Raw, candidate[i].Raw);
            }
        }
        int onlyB = baseline.Count > min ? baseline.Count - min : 0;
        int onlyC = candidate.Count > min ? candidate.Count - min : 0;
        if (first == null && onlyB > 0)
            first = new DiffDivergence(min, DivergenceKind.OnlyInBaseline, baseline[min].Raw, null);
        else if (first == null && onlyC > 0)
            first = new DiffDivergence(min, DivergenceKind.OnlyInCandidate, null, candidate[min].Raw);
        return new DiffResult(baseline.Count, candidate.Count, modified, onlyB, onlyC, first);
    }

    public static void WriteReport(TextWriter @out, DiffResult result, string baselinePath, string candidatePath, IReadOnlySet<string> ignoreFields)
    {
        @out.WriteLine($"baseline:  {baselinePath} ({result.BaselineCount} records)");
        @out.WriteLine($"candidate: {candidatePath} ({result.CandidateCount} records)");
        @out.WriteLine($"ignored fields: {(ignoreFields.Count == 0 ? "(none)" : string.Join(",", ignoreFields))}");
        @out.WriteLine();
        if (result.IsEquivalent)
        {
            @out.WriteLine("OK — tapes are equivalent under the ignored-field policy.");
            return;
        }
        @out.WriteLine($"DIVERGED — modified={result.Modified} onlyInBaseline={result.OnlyInBaseline} onlyInCandidate={result.OnlyInCandidate}");
        @out.WriteLine();
        if (result.FirstDivergence is { } d)
        {
            @out.WriteLine($"first divergence at index {d.Index} ({d.Kind}):");
            switch (d.Kind)
            {
                case DivergenceKind.Modified:
                    @out.WriteLine($"  - baseline:  {d.BaselineLine}");
                    @out.WriteLine($"  + candidate: {d.CandidateLine}");
                    break;
                case DivergenceKind.OnlyInBaseline:
                    @out.WriteLine($"  - baseline:  {d.BaselineLine}");
                    @out.WriteLine("  + candidate: <missing>");
                    break;
                case DivergenceKind.OnlyInCandidate:
                    @out.WriteLine("  - baseline:  <missing>");
                    @out.WriteLine($"  + candidate: {d.CandidateLine}");
                    break;
            }
        }
    }

    public sealed record NormalisedRecord(int LineNumber, string Raw, string Normalised);
}
