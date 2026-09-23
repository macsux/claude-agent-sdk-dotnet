using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Claude.AgentSdk.IntegrationTests.Infrastructure;

/// <summary>
/// Removes machine- and account-specific data from recorded CLI traffic before
/// it is written to a fixture: home and working-directory paths become
/// <c>{HOME}</c> / <c>{CWD}</c> placeholders (also in their sanitized
/// "-Users-name-..." project-key form), e-mail addresses, account / organization
/// identifiers and the API key source are redacted, and Anthropic API request ids
/// are replaced. Every fixture must still be reviewed by a human before commit.
/// </summary>
internal sealed partial class FixtureScrubber
{
    public const string HomePlaceholder = "{HOME}";
    public const string CwdPlaceholder = "{CWD}";

    // Keys whose values identify the account, organization or machine.
    private static readonly HashSet<string> RedactedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "email", "emailAddress", "email_address",
        "account_uuid", "accountUuid", "account_id", "accountId",
        "organization_uuid", "organizationUuid", "organization_id", "organizationId",
        "organization_name", "organizationName", "org_id", "orgId",
        "user_id", "userId",
        "apiKeySource", "api_key_source",
        "messaging_socket_path",
        "oauthAccount",
        "organization", "subscriptionType",
    };

    // Built-in CLI agents. Other entries in the "agents" lists of the initialize response
    // and init message come from the recording developer's environment (plugins, managed
    // settings) and are dropped; SDK-defined agents are kept via the constructor argument.
    private static readonly HashSet<string> BuiltInAgents = new(StringComparer.Ordinal)
    {
        "general-purpose", "Explore", "Plan", "statusline-setup", "claude-code-guide",
    };

    // Long catalog lists are truncated to keep fixtures small and environment-neutral.
    private const int MaxCatalogEntries = 5;

    private readonly HashSet<string> _keepAgents;

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    // Anthropic API request ids (req_011C...), not the SDK's control request ids (req_1_<guid>).
    [GeneratedRegex(@"\breq_01[0-9A-Za-z]{16,}")]
    private static partial Regex ApiRequestIdRegex();

    private readonly List<(string From, string To)> _replacements = new();

    public FixtureScrubber(string cwd, IEnumerable<string>? sdkAgents = null)
    {
        _keepAgents = new HashSet<string>(BuiltInAgents.Concat(sdkAgents ?? []), StringComparer.Ordinal);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var c in DistinctPaths(cwd))
        {
            _replacements.Add((c, CwdPlaceholder));
            _replacements.Add((Sanitize(c), "-CWD-"));
        }
        foreach (var t in DistinctPaths(Path.GetTempPath().TrimEnd('/')))
        {
            _replacements.Add((t, "{TMP}"));
            _replacements.Add((Sanitize(t), "-TMP-"));
        }
        if (!string.IsNullOrEmpty(home))
        {
            _replacements.Add((home, HomePlaceholder));
            _replacements.Add((Sanitize(home), "-HOME-"));
            var user = Path.GetFileName(home);
            if (user.Length >= 3)
                _replacements.Add(("/" + user + "/", "/USER/"));
        }
        // Longest first so "/private/var/.../cwd" is replaced before "/private/var/...".
        _replacements.Sort((a, b) => b.From.Length.CompareTo(a.From.Length));
    }

    private static IEnumerable<string> DistinctPaths(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { path };
        try { set.Add(global::Claude.AgentSdk.Sessions.SessionPaths.RealPath(path)); } catch { }
        if (path.StartsWith("/private/", StringComparison.Ordinal))
            set.Add(path["/private".Length..]);
        return set.Where(p => p.Length > 1);
    }

    private static string Sanitize(string path) => Regex.Replace(path, "[^a-zA-Z0-9]", "-");

    public string ScrubString(string s)
    {
        foreach (var (from, to) in _replacements)
            s = s.Replace(from, to, StringComparison.Ordinal);
        s = EmailRegex().Replace(s, "user@example.com");
        s = ApiRequestIdRegex().Replace(s, "req_redacted");
        return s;
    }

    /// <summary>
    /// Scrub one recorded line and curate the environment catalogs it may carry
    /// (initialize response: commands / agents; init message: slash_commands / skills /
    /// agents): truncate long lists, drop agents that are neither built in nor SDK-defined.
    /// </summary>
    public JsonObject ScrubLine(JsonObject line)
    {
        var scrubbed = (JsonObject)Scrub(line)!;
        if (scrubbed["msg"] is not JsonObject msg) return scrubbed;

        if (msg["type"]?.GetValue<string>() == "control_response" &&
            msg["response"]?["response"] is JsonObject init && init.ContainsKey("commands"))
        {
            Truncate(init, "commands");
            FilterAgents(init["agents"] as JsonArray, a => (a as JsonObject)?["name"]?.GetValue<string>());
        }
        else if (msg["type"]?.GetValue<string>() == "system" && msg["subtype"]?.GetValue<string>() == "init")
        {
            Truncate(msg, "slash_commands");
            Truncate(msg, "skills");
            FilterAgents(msg["agents"] as JsonArray, a => a?.GetValue<string>());
        }
        return scrubbed;
    }

    private static void Truncate(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray arr) return;
        while (arr.Count > MaxCatalogEntries) arr.RemoveAt(arr.Count - 1);
    }

    private void FilterAgents(JsonArray? agents, Func<JsonNode?, string?> name)
    {
        if (agents is null) return;
        for (var i = agents.Count - 1; i >= 0; i--)
        {
            if (name(agents[i]) is not { } n || !_keepAgents.Contains(n))
                agents.RemoveAt(i);
        }
    }

    /// <summary>Returns a scrubbed copy of <paramref name="node"/> (the input is not modified).</summary>
    public JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    var newKey = ScrubString(key);
                    copy[newKey] = RedactedKeys.Contains(key) && value is not null
                        ? JsonValue.Create("REDACTED")
                        : Scrub(value);
                }
                return copy;
            }
            case JsonArray arr:
            {
                var copy = new JsonArray();
                foreach (var item in arr)
                    copy.Add(Scrub(item));
                return copy;
            }
            case JsonValue val when val.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(ScrubString(val.GetValue<string>()));
            default:
                return node?.DeepClone();
        }
    }
}
