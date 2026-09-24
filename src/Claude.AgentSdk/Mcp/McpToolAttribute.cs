// Claude Agent SDK for .NET
// Attribute-based tool registration for in-process SDK MCP servers.
// .NET addition (Python uses the @tool decorator on functions).

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Claude.AgentSdk.Internal;

namespace Claude.AgentSdk.Mcp;

/// <summary>
/// Marks a method as an MCP tool for <see cref="McpSdkServerBuilder.ToolsFrom{T}(T, JsonSerializerContext)"/>
/// and its overloads.
/// </summary>
/// <example>
/// <code>
/// public sealed class MathTools
/// {
///     [McpTool("add", Description = "Add two numbers", ReadOnly = true)]
///     public string Add(AddArgs args) => (args.A + args.B).ToString();
/// }
///
/// [JsonSerializable(typeof(AddArgs))]
/// partial class ToolJson : JsonSerializerContext;
///
/// var servers = McpServers.Sdk("math", b => b.ToolsFrom(new MathTools(), ToolJson.Default));
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpToolAttribute : Attribute
{
    private bool? _readOnly;
    private bool? _destructive;
    private bool? _idempotent;
    private bool? _openWorld;

    /// <param name="name">Tool name; defaults to the method name without an <c>Async</c> suffix.</param>
    public McpToolAttribute(string? name = null)
    {
        Name = name;
    }

    /// <summary>Tool name; null means "derive from the method name".</summary>
    public string? Name { get; }

    /// <summary>Description shown to the model; falls back to a <see cref="DescriptionAttribute"/> on the method.</summary>
    public string? Description { get; set; }

    /// <summary>Human-readable title (annotation).</summary>
    public string? Title { get; set; }

    /// <summary>Annotation: the tool does not modify state.</summary>
    public bool ReadOnly { get => _readOnly ?? false; set => _readOnly = value; }

    /// <summary>Annotation: the tool may perform destructive updates.</summary>
    public bool Destructive { get => _destructive ?? false; set => _destructive = value; }

    /// <summary>Annotation: repeated calls with the same arguments have no additional effect.</summary>
    public bool Idempotent { get => _idempotent ?? false; set => _idempotent = value; }

    /// <summary>Annotation: the tool interacts with external entities.</summary>
    public bool OpenWorld { get => _openWorld ?? false; set => _openWorld = value; }

    /// <summary>Annotation: maximum characters of tool result the CLI keeps (sent as <c>_meta</c>). 0 = unset.</summary>
    public int MaxResultSizeChars { get; set; }

    internal McpToolAnnotations? ToAnnotations()
    {
        if (Title == null && _readOnly == null && _destructive == null && _idempotent == null &&
            _openWorld == null && MaxResultSizeChars <= 0)
            return null;

        return new McpToolAnnotations
        {
            Title = Title,
            ReadOnlyHint = _readOnly,
            DestructiveHint = _destructive,
            IdempotentHint = _idempotent,
            OpenWorldHint = _openWorld,
            MaxResultSizeChars = MaxResultSizeChars > 0 ? MaxResultSizeChars : null
        };
    }
}

public sealed partial class McpSdkServerBuilder
{
    internal const string AttributeToolsRequiresMessage =
        "ToolsFrom without a JsonSerializerContext infers schemas and binds arguments by reflection, which trimming " +
        "and NativeAOT cannot preserve. Pass a JsonSerializerContext that includes each tool's arguments type.";

    /// <summary>
    /// Register every <see cref="McpToolAttribute"/> method (instance and static) of
    /// <paramref name="instance"/>'s type. Trim- and NativeAOT-safe.
    /// </summary>
    /// <remarks>
    /// Each tool method takes at most one arguments parameter, whose type must be
    /// registered in <paramref name="argsContext"/> and serialize as a JSON object
    /// (its exported schema is the tool's input schema), optionally followed by a
    /// <see cref="CancellationToken"/>. It returns <see cref="McpToolResult"/>,
    /// <see cref="string"/>, or a <see cref="Task{TResult}"/>/<see cref="ValueTask{TResult}"/>
    /// of either. For methods with several scalar parameters, use the reflection-based
    /// <see cref="ToolsFrom{T}(T)"/>.
    /// </remarks>
    public McpSdkServerBuilder ToolsFrom<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(
        T instance, JsonSerializerContext argsContext)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(argsContext);
        return RegisterAttributedTools(typeof(T), instance, argsContext);
    }

    /// <summary>
    /// Register every static <see cref="McpToolAttribute"/> method of <typeparamref name="T"/>
    /// (e.g. a static class). Trim- and NativeAOT-safe; same method rules as
    /// <see cref="ToolsFrom{T}(T, JsonSerializerContext)"/>.
    /// </summary>
    public McpSdkServerBuilder ToolsFrom<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(
        JsonSerializerContext argsContext)
    {
        ArgumentNullException.ThrowIfNull(argsContext);
        return RegisterAttributedTools(typeof(T), null, argsContext);
    }

    /// <summary>
    /// Register every <see cref="McpToolAttribute"/> method (instance and static) of
    /// <paramref name="instance"/>'s type, inferring schemas and binding arguments by
    /// reflection exactly like <see cref="Tool(string, Delegate, string?, McpToolAnnotations?)"/>:
    /// any number of parameters, defaults and nullables are optional, any return type.
    /// Not trim/NativeAOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode(AttributeToolsRequiresMessage)]
    [RequiresDynamicCode(AttributeToolsRequiresMessage)]
    public McpSdkServerBuilder ToolsFrom<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(T instance)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        return RegisterAttributedTools(typeof(T), instance, argsContext: null);
    }

    /// <summary>
    /// Register every static <see cref="McpToolAttribute"/> method of <typeparamref name="T"/>
    /// using reflection-based binding (see <see cref="ToolsFrom{T}(T)"/>). Not trim/NativeAOT-compatible.
    /// </summary>
    [RequiresUnreferencedCode(AttributeToolsRequiresMessage)]
    [RequiresDynamicCode(AttributeToolsRequiresMessage)]
    public McpSdkServerBuilder ToolsFrom<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>()
        => RegisterAttributedTools(typeof(T), null, argsContext: null);

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflection-binding branch (argsContext == null) is only reachable from the " +
                        "ToolsFrom overloads annotated [RequiresUnreferencedCode].")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflection-binding branch (argsContext == null) is only reachable from the " +
                        "ToolsFrom overloads annotated [RequiresDynamicCode].")]
    private McpSdkServerBuilder RegisterAttributedTools(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        object? instance,
        JsonSerializerContext? argsContext)
    {
        // Constant binding flags so the trim analyzer can match them to PublicMethods.
        var methods = instance != null
            ? type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            : type.GetMethods(BindingFlags.Public | BindingFlags.Static);
        var registered = 0;

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<McpToolAttribute>();
            if (attr == null)
                continue;

            var name = attr.Name ?? DefaultToolName(method);
            var description = attr.Description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var annotations = attr.ToAnnotations();
            var target = method.IsStatic ? null : instance;

            ValidateName(name);
            _tools[name] = argsContext == null
                ? DelegateToolRegistration.Create(name, description, method, target, annotations)
                : CreateContextBoundTool(name, description, annotations, method, target, argsContext);
            registered++;
        }

        if (registered == 0)
        {
            throw new ArgumentException(
                $"Type '{type.Name}' has no public {(instance != null ? "" : "static ")}methods marked [McpTool].",
                nameof(type));
        }
        return this;
    }

    private static string DefaultToolName(MethodInfo method)
    {
        var name = method.Name;
        return name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;
    }

    private static ToolRegistration CreateContextBoundTool(
        string name,
        string? description,
        McpToolAnnotations? annotations,
        MethodInfo method,
        object? target,
        JsonSerializerContext argsContext)
    {
        var parameters = method.GetParameters();
        var hasCt = parameters.Length > 0 && parameters[^1].ParameterType == typeof(CancellationToken);
        var logical = hasCt ? parameters[..^1] : parameters;
        if (logical.Length > 1)
        {
            throw new ArgumentException(
                $"[McpTool] method '{method.Name}' has {logical.Length} parameters. With a JsonSerializerContext, a tool " +
                "method takes one arguments object (plus an optional CancellationToken); wrap the parameters in a " +
                "record, or use the reflection-based ToolsFrom overload.");
        }
        EnsureSupportedReturnType(method);

        JsonTypeInfo? argsTypeInfo = null;
        JsonElement schema;
        if (logical.Length == 1)
        {
            var argsType = logical[0].ParameterType;
            argsTypeInfo = argsContext.GetTypeInfo(argsType)
                ?? throw new ArgumentException(
                    $"[McpTool] method '{method.Name}': type '{argsType.Name}' is not registered in " +
                    $"{argsContext.GetType().Name}. Add [JsonSerializable(typeof({argsType.Name}))] to the context.");
            schema = BuildSchema(argsTypeInfo);
        }
        else
        {
            schema = SdkJson.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>()
            });
        }

        return new HandlerToolRegistration(name, description, schema, async (args, ct) =>
        {
            var callArgs = new object?[parameters.Length];
            if (argsTypeInfo != null)
            {
                callArgs[0] = JsonSerializer.Deserialize(args, argsTypeInfo)
                              ?? throw new ArgumentException("Tool arguments must be a JSON object.");
            }
            if (hasCt)
                callArgs[^1] = ct;

            object? result;
            try
            {
                result = method.Invoke(target, callArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }

            return result switch
            {
                McpToolResult r => r,
                string s => McpToolResults.Text(s),
                Task<McpToolResult> t => await t.ConfigureAwait(false),
                Task<string> t => McpToolResults.Text(await t.ConfigureAwait(false)),
                ValueTask<McpToolResult> v => await v.ConfigureAwait(false),
                ValueTask<string> v => McpToolResults.Text(await v.ConfigureAwait(false)),
                null => McpToolResults.Text(""),
                _ => throw new InvalidOperationException($"Unsupported tool result type {result.GetType().Name}.")
            };
        }, annotations);
    }

    private static void EnsureSupportedReturnType(MethodInfo method)
    {
        var rt = method.ReturnType;
        if (rt == typeof(McpToolResult) || rt == typeof(string) ||
            rt == typeof(Task<McpToolResult>) || rt == typeof(Task<string>) ||
            rt == typeof(ValueTask<McpToolResult>) || rt == typeof(ValueTask<string>))
            return;

        throw new ArgumentException(
            $"[McpTool] method '{method.Name}' returns {rt.Name}. With a JsonSerializerContext, tools return " +
            "McpToolResult or string (optionally wrapped in Task/ValueTask).");
    }
}
