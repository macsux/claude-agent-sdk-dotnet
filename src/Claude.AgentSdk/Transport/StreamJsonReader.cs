// Claude Agent SDK for .NET
// NDJSON framing/parsing of the CLI's stream-json output, public so custom
// ITransport implementations (e.g. a CLI running over SSH or in a container)
// get the same bounded, forward-compatible parsing as SubprocessTransport.
// Port of _LineFramer / _parse_stdout_line / _read_messages_impl from
// claude-agent-sdk-python/_internal/transport/subprocess_cli.py.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using Microsoft.Extensions.Logging;

namespace Claude.AgentSdk.Transport;

/// <summary>
/// Reads Claude Code's <c>--output-format stream-json</c> output: one JSON
/// message per line.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Lines are bounded while still being received, so a producer
/// that never emits a newline can't grow memory without limit.</description></item>
/// <item><description>Blank lines and non-JSON noise (e.g. <c>[SandboxDebug] ...</c>)
/// are skipped.</description></item>
/// <item><description>A line that starts like JSON but doesn't parse throws
/// <see cref="JsonDecodeException"/>, except a truncated final line at end of
/// stream, which is dropped.</description></item>
/// </list>
/// </remarks>
public static class StreamJsonReader
{
    /// <summary>Default maximum length of one message line (1 MiB of characters).</summary>
    public const int DefaultMaxLineLength = 1024 * 1024;

    private const int ReadChunkSize = 64 * 1024;

    /// <summary>
    /// Read messages from <paramref name="reader"/> until end of stream.
    /// </summary>
    /// <param name="reader">The CLI's stdout (or any stream-json source).</param>
    /// <param name="maxLineLength">Maximum characters in one line, complete or not.</param>
    /// <param name="logger">Optional logger for skipped and dropped lines.</param>
    /// <param name="cancellationToken">Cancels the read; surfaces as <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="JsonDecodeException">A line exceeds <paramref name="maxLineLength"/> or is corrupt JSON.</exception>
    public static async IAsyncEnumerable<JsonElement> ReadAsync(
        TextReader reader,
        int maxLineLength = DefaultMaxLineLength,
        ILogger? logger = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineLength);

        var framer = new LineFramer();
        var buffer = new char[ReadChunkSize];

        void Guard(int length)
        {
            if (length > maxLineLength)
            {
                throw new JsonDecodeException(
                    $"JSON message exceeded maximum buffer size of {maxLineLength} bytes",
                    new InvalidOperationException($"Buffer size {length} exceeds limit {maxLineLength}")
                );
            }
        }

        while (true)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                break;

            foreach (var line in framer.Push(buffer.AsSpan(0, n)))
            {
                Guard(line.Length);
                var data = ParseLine(line, logger);
                if (data.HasValue)
                    yield return data.Value;
            }
            Guard(framer.PendingLength);
        }

        // A residual tail means either a producer that omits the final newline
        // (yield it) or one cut off mid-write (unrecoverable — drop it).
        var tail = framer.Flush();
        JsonElement? trailing = null;
        try
        {
            trailing = ParseLine(tail, logger);
        }
        catch (JsonDecodeException)
        {
            logger?.LogDebug("Dropping truncated JSON at end of CLI stdout ({Length} chars)", tail.Length);
        }
        if (trailing.HasValue)
            yield return trailing.Value;
    }

    /// <summary>
    /// Parse one complete line. Returns null for lines that carry no message
    /// (blank, or not starting with <c>{</c>).
    /// </summary>
    /// <exception cref="JsonDecodeException">The line starts with <c>{</c> but is not valid JSON.</exception>
    public static JsonElement? ParseLine(string line, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        // A complete line: surrounding whitespace (e.g. the \r of CRLF) is meaningless.
        line = line.Trim();
        if (line.Length == 0)
            return null;
        if (!line.StartsWith('{'))
        {
            // Content isn't logged: stdout noise can include file contents.
            logger?.LogDebug("Skipping non-JSON line from CLI stdout ({Length} chars)", line.Length);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, SdkJsonContext.Default.JsonElement);
        }
        catch (JsonException ex)
        {
            throw new JsonDecodeException(line, ex);
        }
    }
}
