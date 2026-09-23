using System.Text.Json;

namespace Claude.AgentSdk.Tests.Replay;

/// <summary>One line of a recorded fixture (see docs/TESTING.md for the format).</summary>
internal sealed record FixtureLine(string Dir, JsonElement? Msg, bool EndInput, int? ExitCode)
{
    public string? Type => Msg is { } m && m.TryGetProperty("type", out var t) ? t.GetString() : null;
}

/// <summary>
/// A JSONL recording of one real CLI session, captured by the integration suite's
/// RecordingTransport: <c>in</c> lines are CLI stdout, <c>out</c> lines are what the
/// SDK wrote to stdin, <c>exit</c> is a non-zero CLI exit.
/// </summary>
internal sealed class Fixture
{
    public required string Name { get; init; }
    public required JsonElement Header { get; init; }
    public required IReadOnlyList<FixtureLine> Lines { get; init; }

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<string> AllNames() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    public static Fixture Load(string name)
    {
        var path = Path.Combine(Directory, name + ".jsonl");
        JsonElement? header = null;
        var lines = new List<FixtureLine>();
        foreach (var raw in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var el = JsonSerializer.Deserialize<JsonElement>(raw);
            if (el.TryGetProperty("fixture", out _))
            {
                header = el;
                continue;
            }
            var dir = el.GetProperty("dir").GetString()!;
            lines.Add(new FixtureLine(
                dir,
                el.TryGetProperty("msg", out var m) ? m : null,
                el.TryGetProperty("end_input", out var e) && e.ValueKind == JsonValueKind.True,
                el.TryGetProperty("exit_code", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : null));
        }
        return new Fixture
        {
            Name = name,
            Header = header ?? throw new InvalidDataException($"{name}: missing fixture header line"),
            Lines = lines,
        };
    }

    /// <summary>CLI → SDK messages (excluding the exit marker).</summary>
    public IEnumerable<JsonElement> Incoming => Lines.Where(l => l.Dir == "in").Select(l => l.Msg!.Value);

    /// <summary>SDK → CLI messages recorded during the live run.</summary>
    public IEnumerable<JsonElement> Outgoing => Lines.Where(l => l.Dir == "out" && l.Msg is not null).Select(l => l.Msg!.Value);

    /// <summary>The first recorded user prompt text.</summary>
    public string FirstPrompt => Outgoing.First(m => m.GetProperty("type").GetString() == "user")
        .GetProperty("message").GetProperty("content").GetString()!;
}
