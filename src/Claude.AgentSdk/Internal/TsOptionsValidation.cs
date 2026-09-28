// Claude Agent SDK for .NET
// Pre-flight checks for the TypeScript-SDK options (sdk.mjs option intake).

namespace Claude.AgentSdk.Internal;

internal static class TsOptionsValidation
{
    /// <summary>
    /// Throw <see cref="ArgumentException"/> for option combinations the TS SDK
    /// rejects before spawning the CLI.
    /// </summary>
    public static void Validate(ClaudeAgentOptions options)
    {
        if (options.SessionStore != null && !options.PersistSession)
        {
            throw new ArgumentException(
                "SessionStore cannot be used with PersistSession = false: the storage adapter requires local " +
                "writes to mirror from. Use CLAUDE_CONFIG_DIR pointing at a temp directory for ephemeral local " +
                "writes with external mirroring.",
                nameof(options));
        }

        if (options.SupportedDialogKinds is { Count: > 0 } && options.OnUserDialog == null)
        {
            throw new ArgumentException(
                "SupportedDialogKinds requires an OnUserDialog callback: declaring dialog kinds without a handler " +
                "would park dialogs nothing can answer. Provide OnUserDialog, or omit SupportedDialogKinds.",
                nameof(options));
        }
    }
}
