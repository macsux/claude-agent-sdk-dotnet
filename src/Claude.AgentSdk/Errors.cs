// Claude Agent SDK for .NET
// Port of claude-agent-sdk-python/_errors.py

using System.Text.Json;

namespace Claude.AgentSdk;

/// <summary>
/// Base exception for all Claude SDK errors.
/// </summary>
public class ClaudeSDKException : Exception
{
    public ClaudeSDKException(string message) : base(message) { }
    public ClaudeSDKException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Raised when unable to connect to Claude Code CLI.
/// </summary>
public class CliConnectionException : ClaudeSDKException
{
    public CliConnectionException(string message) : base(message) { }
    public CliConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Raised when Claude Code CLI is not found or not installed.
/// </summary>
public class CliNotFoundException : CliConnectionException
{
    public string? CliPath { get; }

    public CliNotFoundException(string message, string? cliPath = null)
        : base(cliPath != null ? $"{message}: {cliPath}" : message)
    {
        CliPath = cliPath;
    }
}

/// <summary>
/// Raised when the CLI process fails.
/// </summary>
public class ProcessException : ClaudeSDKException
{
    public int? ExitCode { get; }
    public string? Stderr { get; }

    public ProcessException(string message, int? exitCode = null, string? stderr = null)
        : base(FormatMessage(message, exitCode, stderr))
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }

    /// <summary>Same as the primary constructor, chaining <paramref name="innerException"/>.</summary>
    public ProcessException(string message, int? exitCode, string? stderr, Exception? innerException)
        : base(FormatMessage(message, exitCode, stderr), innerException!)
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }

    private static string FormatMessage(string message, int? exitCode, string? stderr)
    {
        if (exitCode.HasValue)
            message = $"{message} (exit code: {exitCode})";
        if (!string.IsNullOrEmpty(stderr))
            message = $"{message}\nError output: {stderr}";
        return message;
    }
}

/// <summary>
/// Thrown when the CLI exits after reporting a terminal error result.
/// </summary>
/// <remarks>
/// The CLI ends a failed run by emitting a <c>result</c> message with
/// <c>is_error: true</c> and then exiting non-zero. This replaces the bare
/// "exit code 1" <see cref="ProcessException"/> for that case and carries the
/// result's payload, so callers can branch on why the run failed without
/// matching on strings.
///
/// Subclasses <see cref="ProcessException"/>, so existing
/// <c>catch (ProcessException)</c> handlers keep working.
/// </remarks>
public class ResultException : ProcessException
{
    /// <summary>
    /// Result subtype: <c>error_max_turns</c>, <c>error_during_execution</c>, …
    /// or <c>success</c> when the agent loop completed but the last turn was an
    /// API error.
    /// </summary>
    public string Subtype { get; }

    /// <summary>Why the run terminated, when the CLI reported it (e.g. <c>api_error</c>).</summary>
    public string? TerminalReason { get; }

    /// <summary>
    /// Error strings from the result frame, normalized: blanks and non-strings
    /// dropped, so these always agree with the exception message.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>The result text, if any. For API failures this holds the "API Error: ..." prose.</summary>
    public string? Result { get; init; }

    /// <summary>HTTP status of the failing API call, if any.</summary>
    public int? ApiErrorStatus { get; init; }

    /// <summary>Session the result belongs to, if reported.</summary>
    public string? SessionId { get; init; }

    /// <summary>The raw <c>result</c> message payload as emitted by the CLI.</summary>
    public JsonElement? RawResult { get; init; }

    public ResultException(
        string message,
        string subtype,
        string? terminalReason = null,
        IReadOnlyList<string>? errors = null,
        int? exitCode = null,
        string? stderr = null,
        Exception? innerException = null)
        : base(message, exitCode, stderr, innerException)
    {
        Subtype = subtype;
        TerminalReason = terminalReason;
        Errors = errors ?? Array.Empty<string>();
    }

    /// <summary>
    /// Build from a <c>result</c> frame with <c>is_error: true</c>, the way
    /// Python's <c>Query._read_messages</c> replaces the trailing
    /// <c>ProcessError</c>: message <c>"Claude Code returned an error result: …"</c>,
    /// exit code carried over, stderr deliberately dropped (the transport's
    /// value is a generic placeholder; the result text is the real cause).
    /// </summary>
    internal static ResultException FromResultFrame(JsonElement data, int? exitCode, Exception? cause)
    {
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var errors = data.TryGetProperty("errors", out var raw) ? NormalizeErrors(raw) : Array.Empty<string>();
        int? status = data.TryGetProperty("api_error_status", out var s) && s.ValueKind == JsonValueKind.Number &&
                      s.TryGetInt32(out var si)
            ? si
            : null;

        return new ResultException(
            $"Claude Code returned an error result: {ErrorResultText(data)}",
            Str(data, "subtype") ?? string.Empty,
            Str(data, "terminal_reason"),
            errors,
            exitCode,
            stderr: null,
            innerException: cause)
        {
            Result = Str(data, "result"),
            ApiErrorStatus = status,
            SessionId = Str(data, "session_id"),
            RawResult = data.Clone()
        };
    }

    /// <summary>
    /// Pick the most informative text from an error <c>result</c> frame:
    /// <c>errors[]</c>, then <c>result</c>, then a non-success <c>subtype</c>,
    /// then the HTTP status. Python: <c>_error_result_text</c>.
    /// </summary>
    internal static string ErrorResultText(JsonElement data)
    {
        var errors = data.TryGetProperty("errors", out var raw) ? NormalizeErrors(raw) : Array.Empty<string>();
        if (errors.Count > 0)
            return string.Join("; ", errors);
        if (data.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(r.GetString()))
            return r.GetString()!.Trim();
        if (data.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String &&
            st.GetString() is { Length: > 0 } subtype && subtype != "success")
            return subtype;
        if (data.TryGetProperty("api_error_status", out var status) && status.ValueKind != JsonValueKind.Null)
            return $"API error (HTTP {status.GetRawText()})";
        return "unknown error";
    }

    /// <summary>
    /// Normalize a result frame's <c>errors</c> field to clean strings.
    /// </summary>
    /// <remarks>
    /// The CLI emits an array of strings; a bare string is tolerated (older or
    /// buggy emitters) and non-string or blank entries are dropped, so the
    /// structured <see cref="Errors"/> and the exception text always agree.
    /// </remarks>
    public static IReadOnlyList<string> NormalizeErrors(JsonElement raw)
    {
        static string Clean(string s) => s.Trim();

        if (raw.ValueKind == JsonValueKind.String)
        {
            var single = Clean(raw.GetString() ?? string.Empty);
            return single.Length == 0 ? Array.Empty<string>() : new[] { single };
        }

        if (raw.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var entry in raw.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                continue;
            var text = Clean(entry.GetString() ?? string.Empty);
            if (text.Length > 0)
                result.Add(text);
        }
        return result;
    }
}

/// <summary>
/// Reported when <c>CanUseTool</c> is set but some tool calls are auto-approved
/// before it runs.
/// </summary>
/// <remarks>
/// The TypeScript SDK reports the same condition as a process warning with code
/// <c>CLAUDE_SDK_CAN_USE_TOOL_SHADOWED</c>. .NET has no warning stream, so this
/// is surfaced as an exception type callers can raise or inspect rather than
/// something thrown by the SDK on its own.
/// </remarks>
public class CanUseToolShadowedWarning : ClaudeSDKException
{
    public CanUseToolShadowedWarning(string message) : base(message) { }
}

/// <summary>
/// Raised when unable to decode JSON from CLI output.
/// </summary>
public class JsonDecodeException : ClaudeSDKException
{
    public string Line { get; }

    public JsonDecodeException(string line, Exception innerException)
        : base($"Failed to decode JSON: {line[..Math.Min(100, line.Length)]}...", innerException)
    {
        Line = line;
    }
}

/// <summary>
/// Raised when unable to parse a message from CLI output.
/// </summary>
public class MessageParseException : ClaudeSDKException
{
    public JsonElement? RawData { get; }

    public MessageParseException(string message, JsonElement? rawData = null)
        : base(message)
    {
        RawData = rawData;
    }
}
