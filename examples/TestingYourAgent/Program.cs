// Unit-test your own agent code without the Claude Code CLI: ScriptedTransport
// plays the CLI's side of the conversation deterministically, for free, and
// pauses on permission/hook/MCP requests until your code answers them.
// (Shown as a console app; in a test project the checks would be assertions.)

using System.Text.Json;
using Claude.AgentSdk;
using Claude.AgentSdk.Testing;
using ClaudeApi = Claude.AgentSdk.Claude;

// The code under test: an agent that must never write outside /workspace.
static ClaudeAgentOptions SafeWriterOptions() => new()
{
    CanUseTool = (tool, input, _, _) =>
    {
        if (tool == "Write" && !input.GetProperty("file_path").GetString()!.StartsWith("/workspace/"))
            return Task.FromResult<PermissionResult>(new PermissionResultDeny("Writes are limited to /workspace."));
        return Task.FromResult<PermissionResult>(new PermissionResultAllow());
    }
};

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

// Test 1: a write outside /workspace is denied, and the denial reason reaches the CLI.
{
    var cli = new ScriptedTransport().Turn(t => t
        .PermissionRequest("Write", """{"file_path":"/etc/hosts","content":"x"}""")
        .AssistantText("I can't write there.")
        .Result("I can't write there."));

    var answer = await ClaudeApi.QueryTextAsync("update /etc/hosts", SafeWriterOptions(), cli);

    var decision = cli.PermissionResponses.Single();
    Check(decision.GetProperty("behavior").GetString() == "deny", "write outside /workspace is denied");
    Check(decision.GetProperty("message").GetString() == "Writes are limited to /workspace.", "denial reason is sent");
    Check(answer == "I can't write there.", "final answer is returned");
}

// Test 2: a write inside /workspace is allowed with the original input.
{
    var cli = new ScriptedTransport().Turn(t => t
        .PermissionRequest("Write", """{"file_path":"/workspace/notes.md","content":"hi"}""")
        .Result("Done."));

    await ClaudeApi.QueryTextAsync("write notes", SafeWriterOptions(), cli);

    var decision = cli.PermissionResponses.Single();
    Check(decision.GetProperty("behavior").GetString() == "allow", "write inside /workspace is allowed");
    Check(decision.GetProperty("updatedInput").GetProperty("file_path").GetString() == "/workspace/notes.md",
        "input is passed through unchanged");
}

// Test 3: the prompt your code builds is what reaches the CLI.
{
    var cli = new ScriptedTransport().Turn(t => t.Result("ok"));
    await ClaudeApi.QueryTextAsync("Summarize README.md in one line.", new ClaudeAgentOptions(), cli);

    var sent = cli.UserMessages.Single().GetProperty("message").GetProperty("content").GetString();
    Check(sent == "Summarize README.md in one line.", "prompt is sent as the user message");
}

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
