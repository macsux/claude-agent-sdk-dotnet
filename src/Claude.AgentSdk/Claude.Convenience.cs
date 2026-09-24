// Claude Agent SDK for .NET
// One-call convenience helpers over Claude.QueryAsync. Not part of the Python
// SDK; .NET additions built only on the public streaming API.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk;

public static partial class Claude
{
    private const string StructuredReflectionMessage =
        "QueryAsync<T>(prompt, options) derives the JSON schema and deserializes T by reflection, which trimming " +
        "and NativeAOT cannot preserve. Use the overload that takes a JsonTypeInfo<T> from a JsonSerializerContext.";

    /// <summary>
    /// Run a one-shot query and return Claude's final answer as text.
    /// </summary>
    /// <remarks>
    /// Returns the result's text when the CLI reports one, otherwise the text
    /// blocks of the last assistant message. Hooks, permission callbacks, SDK MCP
    /// servers and every other option work as with <see cref="QueryAsync(string, ClaudeAgentOptions?, ITransport?, CancellationToken)"/>.
    /// </remarks>
    /// <exception cref="ResultException">The run ended with an error result (max turns, budget, API error, ...).</exception>
    public static async Task<string> QueryTextAsync(
        string prompt,
        ClaudeAgentOptions? options = null,
        ITransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        var (result, lastAssistant) = await RunToResultAsync(prompt, options, transport, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result.Result))
            return result.Result;

        var text = new StringBuilder();
        if (lastAssistant != null)
        {
            foreach (var block in lastAssistant.Content)
            {
                if (block is TextBlock tb)
                    text.Append(tb.Text);
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// Run a one-shot query whose answer is structured output conforming to
    /// <typeparamref name="T"/>'s JSON schema, and return it deserialized.
    /// Trim- and NativeAOT-safe.
    /// </summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>, e.g.
    /// <c>MyJsonContext.Default.Forecast</c>. The schema sent to the CLI (<c>--json-schema</c>)
    /// is exported from the same metadata, so property names and required members match
    /// what is deserialized. <typeparamref name="T"/> must serialize as a JSON object.</param>
    /// <param name="options">Options; any <see cref="ClaudeAgentOptions.OutputFormat"/> is replaced.</param>
    /// <param name="transport">Optional custom transport.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ResultException">The run ended with an error result.</exception>
    /// <exception cref="ClaudeSDKException">The run succeeded but produced no structured output.</exception>
    public static async Task<T> QueryAsync<T>(
        string prompt,
        JsonTypeInfo<T> typeInfo,
        ClaudeAgentOptions? options = null,
        ITransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var schema = McpSdkServerBuilder.BuildSchema(typeInfo);
        var structured = await QueryStructuredAsync(prompt, schema, options, transport, cancellationToken).ConfigureAwait(false);
        return structured.Deserialize(typeInfo)
               ?? throw new ClaudeSDKException($"Structured output deserialized to null for {typeof(T).Name}.");
    }

    /// <summary>
    /// Reflection-based convenience form of
    /// <see cref="QueryAsync{T}(string, JsonTypeInfo{T}, ClaudeAgentOptions?, ITransport?, CancellationToken)"/>:
    /// the schema and deserialization use <see cref="JsonSerializerOptions.Web"/> (camelCase).
    /// Not trim/NativeAOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode(StructuredReflectionMessage)]
    [RequiresDynamicCode(StructuredReflectionMessage)]
    public static Task<T> QueryAsync<T>(
        string prompt,
        ClaudeAgentOptions? options = null,
        ITransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        var typeInfo = (JsonTypeInfo<T>)JsonSerializerOptions.Web.GetTypeInfo(typeof(T));
        return QueryAsync(prompt, typeInfo, options, transport, cancellationToken);
    }

    private static async Task<JsonElement> QueryStructuredAsync(
        string prompt,
        JsonElement schema,
        ClaudeAgentOptions? options,
        ITransport? transport,
        CancellationToken cancellationToken)
    {
        var outputFormat = SdkJson.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "json_schema",
            ["schema"] = schema
        });
        var structuredOptions = (options ?? new ClaudeAgentOptions()) with { OutputFormat = outputFormat };

        var (result, _) = await RunToResultAsync(prompt, structuredOptions, transport, cancellationToken).ConfigureAwait(false);
        if (result.StructuredOutput is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } structured)
        {
            throw new ClaudeSDKException(
                $"The run completed ({result.Subtype}) without structured output; the model may not have produced " +
                "a response matching the schema.");
        }
        return structured;
    }

    /// <summary>Drain a query to its (last) result, surfacing error results as <see cref="ResultException"/>.</summary>
    private static async Task<(ResultMessage Result, AssistantMessage? LastAssistant)> RunToResultAsync(
        string prompt,
        ClaudeAgentOptions? options,
        ITransport? transport,
        CancellationToken cancellationToken)
    {
        ResultMessage? result = null;
        AssistantMessage? lastAssistant = null;

        await foreach (var message in QueryAsync(prompt, options, transport, cancellationToken).ConfigureAwait(false))
        {
            switch (message)
            {
                case AssistantMessage am:
                    lastAssistant = am;
                    break;
                case ResultMessage rm:
                    result = rm;
                    break;
            }
        }

        if (result == null)
            throw new ClaudeSDKException("The query ended without a result message.");

        // The streaming API normally raises for error results when the CLI exits
        // non-zero; a one-shot helper must never hand back an error as an answer.
        if (result.IsError)
        {
            var errors = result.Errors?.Where(e => !string.IsNullOrWhiteSpace(e)).ToList() ?? [];
            var detail = errors.Count > 0 ? string.Join("; ", errors) : result.Result ?? result.Subtype;
            throw new ResultException(
                $"Claude Code returned an error result: {detail}",
                result.Subtype,
                result.TerminalReason,
                errors)
            {
                Result = result.Result,
                SessionId = result.SessionId
            };
        }

        return (result, lastAssistant);
    }
}
