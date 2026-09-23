// Claude Agent SDK for .NET
// Port of _LineFramer from claude-agent-sdk-python/_internal/transport/subprocess_cli.py

using System.Text;

namespace Claude.AgentSdk.Transport;

/// <summary>
/// Reassembles complete lines from a stream read in arbitrary chunks.
/// </summary>
/// <remarks>
/// Reading in chunks (rather than <see cref="TextReader.ReadLineAsync()"/>) lets the
/// caller bound a line that is still being received: <c>ReadLineAsync</c> buffers an
/// unterminated line without limit before the caller ever sees it.
/// </remarks>
internal sealed class LineFramer
{
    private readonly StringBuilder _pending = new();

    /// <summary>Length of the partial line currently buffered.</summary>
    public int PendingLength => _pending.Length;

    /// <summary>Add a chunk, returning any lines it completed.</summary>
    public List<string> Push(ReadOnlySpan<char> chunk)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != '\n')
                continue;
            _pending.Append(chunk[start..i]);
            lines.Add(_pending.ToString());
            _pending.Clear();
            start = i + 1;
        }
        _pending.Append(chunk[start..]);
        return lines;
    }

    /// <summary>Take the trailing partial line, if any.</summary>
    public string Flush()
    {
        var line = _pending.ToString();
        _pending.Clear();
        return line;
    }
}
