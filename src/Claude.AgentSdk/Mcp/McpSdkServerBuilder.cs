using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Claude.AgentSdk;
using Claude.AgentSdk.Internal;

namespace Claude.AgentSdk.Mcp;

/// <summary>
/// Builds an in-process ("sdk") MCP server configuration with strongly-typed tool registration.
/// </summary>
/// <remarks>
/// <para>Three ways to register a tool:</para>
/// <list type="bullet">
/// <item><description><see cref="Tool(string, JsonElement, Func{JsonElement, CancellationToken, Task{McpToolResult}}, string?, McpToolAnnotations?)"/>:
/// an explicit JSON Schema and a handler over the raw JSON arguments. Trim- and NativeAOT-safe.</description></item>
/// <item><description><see cref="Tool{TArgs}(string, JsonTypeInfo{TArgs}, Func{TArgs, CancellationToken, Task{McpToolResult}}, string?, McpToolAnnotations?)"/>:
/// a typed arguments object bound through source-generated <see cref="JsonTypeInfo{T}"/> metadata, with the
/// schema derived from the same metadata. Trim- and NativeAOT-safe.</description></item>
/// <item><description><see cref="Tool(string, Delegate, string?, McpToolAnnotations?)"/>: any delegate; schema
/// and binding are inferred by reflection. Convenient, but not trim/AOT-compatible.</description></item>
/// </list>
/// </remarks>
public sealed class McpSdkServerBuilder
{
    internal const string DelegateToolRequiresMessage =
        "Tool(string, Delegate, ...) infers the input schema and binds arguments by reflecting over the delegate's " +
        "parameter types, which trimming and NativeAOT cannot preserve. Use the Tool overload that takes an explicit " +
        "JSON schema, or the one that takes a JsonTypeInfo<TArgs> from a JsonSerializerContext.";

    private readonly string _serverName;
    private readonly Dictionary<string, ToolRegistration> _tools = new(StringComparer.Ordinal);

    internal McpSdkServerBuilder(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name must be non-empty.", nameof(serverName));
        _serverName = serverName;
    }

    /// <summary>
    /// Register a tool by delegate; input schema and argument binding are inferred from the delegate signature.
    /// </summary>
    /// <remarks>
    /// Supported signatures include:
    /// <list type="bullet">
    /// <item><description><c>(T1 a, T2 b, CancellationToken ct) => TResult</c></description></item>
    /// <item><description><c>(T1 a, T2 b) => Task&lt;TResult&gt;</c></description></item>
    /// <item><description><c>(TArgs args) => TResult</c> (a single POCO/record/dictionary param binds from the whole JSON args object;
    /// a single collection, enum, Guid, DateTime, ... param is an ordinary named property)</description></item>
    /// </list>
    /// Parameters with a default value, and nullable parameters (<c>int?</c>, <c>string?</c>), are optional;
    /// nullable ones also accept an explicit JSON <c>null</c>. Recursive parameter types are supported
    /// (the schema is cut at the point of recursion with a permissive <c>{"type": "object"}</c>).
    /// <para>This overload uses reflection and is not trim/NativeAOT-compatible; see the
    /// <see cref="McpSdkServerBuilder"/> remarks for the AOT-safe alternatives.</para>
    /// </remarks>
    [RequiresUnreferencedCode(DelegateToolRequiresMessage)]
    [RequiresDynamicCode(DelegateToolRequiresMessage)]
    public McpSdkServerBuilder Tool(string name, Delegate handler, string? description = null, McpToolAnnotations? annotations = null)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(handler);
        _tools[name] = DelegateToolRegistration.Create(name, description, handler, annotations);
        return this;
    }

    /// <summary>
    /// Register a tool with an explicit input JSON Schema and a handler that receives the raw
    /// JSON arguments. Trim- and NativeAOT-safe.
    /// </summary>
    /// <param name="name">Tool name.</param>
    /// <param name="inputSchema">JSON Schema for the arguments; its root must be an object schema
    /// (<c>{"type": "object", ...}</c>). Arguments are validated against it before the handler runs.</param>
    /// <param name="handler">Receives the arguments object (never <c>null</c>; <c>{}</c> when the
    /// caller sent none) and the request's cancellation token.</param>
    /// <param name="description">Tool description shown to the model.</param>
    /// <param name="annotations">Tool behavior hints.</param>
    public McpSdkServerBuilder Tool(
        string name,
        JsonElement inputSchema,
        Func<JsonElement, CancellationToken, Task<McpToolResult>> handler,
        string? description = null,
        McpToolAnnotations? annotations = null)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (inputSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The input schema must be a JSON object.", nameof(inputSchema));

        _tools[name] = new HandlerToolRegistration(name, description, inputSchema.Clone(), handler, annotations);
        return this;
    }

    /// <summary>
    /// Register a tool whose arguments bind to <typeparamref name="TArgs"/> through source-generated
    /// metadata. The input schema is derived from the same metadata (via
    /// <see cref="JsonSchemaExporter"/>), so property names, required members and enum handling follow
    /// the <see cref="JsonSerializerContext"/> that produced <paramref name="argsTypeInfo"/>.
    /// Trim- and NativeAOT-safe.
    /// </summary>
    /// <param name="name">Tool name.</param>
    /// <param name="argsTypeInfo">Metadata for the arguments type, e.g. <c>MyJsonContext.Default.AddArgs</c>.
    /// The type must serialize as a JSON object.</param>
    /// <param name="handler">Receives the deserialized arguments and the request's cancellation token.</param>
    /// <param name="description">Tool description shown to the model.</param>
    /// <param name="annotations">Tool behavior hints.</param>
    public McpSdkServerBuilder Tool<TArgs>(
        string name,
        JsonTypeInfo<TArgs> argsTypeInfo,
        Func<TArgs, CancellationToken, Task<McpToolResult>> handler,
        string? description = null,
        McpToolAnnotations? annotations = null)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(argsTypeInfo);
        ArgumentNullException.ThrowIfNull(handler);

        var schema = BuildSchema(argsTypeInfo);
        _tools[name] = new HandlerToolRegistration(name, description, schema, async (args, ct) =>
        {
            var value = args.Deserialize(argsTypeInfo)
                        ?? throw new ArgumentException("Tool arguments must be a JSON object.");
            return await handler(value, ct).ConfigureAwait(false);
        }, annotations);
        return this;
    }

    private void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name must be non-empty.", nameof(name));

        if (_tools.ContainsKey(name))
            throw new ArgumentException($"Tool '{name}' is already registered.", nameof(name));
    }

    /// <summary>
    /// Input schema for a typed tool: the exported schema of <paramref name="typeInfo"/>, whose
    /// root must be an object and is never nullable (the CLI always sends an arguments object).
    /// </summary>
    internal static JsonElement BuildSchema(JsonTypeInfo typeInfo)
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(typeInfo, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true
        });

        if (node is not JsonObject root)
            throw new ArgumentException(
                $"Tool arguments type '{typeInfo.Type}' must serialize as a JSON object.", nameof(typeInfo));

        switch (root["type"])
        {
            case JsonValue v when v.TryGetValue<string>(out var t) && t == "object":
                break;
            case JsonArray types when types.Any(x => x?.GetValue<string>() == "object"):
                root["type"] = "object";
                break;
            default:
                throw new ArgumentException(
                    $"Tool arguments type '{typeInfo.Type}' must serialize as a JSON object.", nameof(typeInfo));
        }

        return SdkJson.SerializeToElement(root);
    }

    internal McpSdkServerConfig Build()
    {
        var handlers = new McpServerHandlers
        {
            ListTools = ct => Task.FromResult<IReadOnlyList<McpToolDefinition>>(
                _tools.Values
                    .Select(t => t.Definition)
                    .ToList()
            ),
            // Unknown tools, invalid arguments and handler failures all come back as isError
            // results the model can read, never as protocol errors (Python create_sdk_mcp_server).
            CallTool = async (toolName, args, ct) =>
            {
                if (!_tools.TryGetValue(toolName, out var tool))
                    return McpToolResults.Text($"Tool '{toolName}' not found", isError: true);

                try
                {
                    var validationError = McpInputSchemaValidator.Validate(args, tool.Definition.InputSchema!.Value);
                    if (validationError != null)
                        return McpToolResults.Text($"Input validation error: {validationError}", isError: true);

                    return await tool.InvokeAsync(args, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return McpToolResults.Text(ex.Message, isError: true);
                }
            }
        };

        return new McpSdkServerConfig
        {
            Name = _serverName,
            Handlers = handlers
        };
    }

    private abstract class ToolRegistration
    {
        public McpToolDefinition Definition { get; }

        protected ToolRegistration(string name, string? description, JsonElement inputSchema, McpToolAnnotations? annotations)
        {
            Definition = new McpToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema,
                Annotations = annotations,
                Meta = BuildMeta(annotations)
            };
        }

        public abstract Task<McpToolResult> InvokeAsync(JsonElement args, CancellationToken ct);

        // Client-specific hints travel in _meta under namespaced keys because MCP clients drop
        // annotation fields they do not know (Python _build_meta).
        private static IReadOnlyDictionary<string, object?>? BuildMeta(McpToolAnnotations? annotations)
        {
            if (annotations?.MaxResultSizeChars is not { } maxSize)
                return null;
            return new Dictionary<string, object?> { ["anthropic/maxResultSizeChars"] = maxSize };
        }

        /// <summary>Normalize a handler's result: content is converted as the CLI expects.</summary>
        protected static McpToolResult Normalize(McpToolResult? result) => result == null
            ? McpToolResults.Text("")
            : new McpToolResult { Content = ConvertContent(result.Content), IsError = result.IsError };

        /// <summary>
        /// Map a handler's content to what the CLI renders (Python <c>_convert_tool_content</c>):
        /// text and image blocks pass through, resource links and text resources are flattened to
        /// text, and binary resources and unknown block types are dropped.
        /// </summary>
        internal static IReadOnlyList<McpContent> ConvertContent(IEnumerable<McpContent>? items)
        {
            var content = new List<McpContent>();
            if (items == null)
                return content;

            foreach (var item in items)
            {
                if (item == null)
                    continue;
                switch (item.Type)
                {
                    case "text":
                        content.Add(new McpContent { Type = "text", Text = item.Text ?? "" });
                        break;
                    case "image":
                        content.Add(new McpContent { Type = "image", Data = item.Data, MimeType = item.MimeType });
                        break;
                    case "resource_link":
                        content.Add(McpContents.Text(string.IsNullOrEmpty(item.Text) ? "Resource link" : item.Text));
                        break;
                    case "resource" when item.Text != null:
                        content.Add(McpContents.Text(item.Text));
                        break;
                    case "resource":
                        System.Diagnostics.Trace.TraceWarning("Binary embedded resource cannot be converted to text, skipping");
                        break;
                    default:
                        System.Diagnostics.Trace.TraceWarning($"Unsupported content type '{item.Type}' in tool result, skipping");
                        break;
                }
            }

            return content;
        }
    }

    /// <summary>A tool with an explicit schema and a JSON-arguments handler (AOT-safe).</summary>
    private sealed class HandlerToolRegistration : ToolRegistration
    {
        private readonly Func<JsonElement, CancellationToken, Task<McpToolResult>> _handler;

        public HandlerToolRegistration(
            string name,
            string? description,
            JsonElement inputSchema,
            Func<JsonElement, CancellationToken, Task<McpToolResult>> handler,
            McpToolAnnotations? annotations)
            : base(name, description, inputSchema, annotations)
        {
            _handler = handler;
        }

        public override async Task<McpToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
        {
            // Match the delegate path: an absent/null arguments value binds as {}.
            if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                args = SdkJson.EmptyObject();
            return Normalize(await _handler(args, ct).ConfigureAwait(false));
        }
    }

    /// <summary>A tool whose schema and argument binding are inferred by reflection.</summary>
    [RequiresUnreferencedCode(DelegateToolRequiresMessage)]
    [RequiresDynamicCode(DelegateToolRequiresMessage)]
    private sealed class DelegateToolRegistration : ToolRegistration
    {
        private static JsonSerializerOptions? s_toolJsonOptions;

        private static JsonSerializerOptions ToolJsonOptions => s_toolJsonOptions ??= new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // The schema advertises enums by name, so bind (and report) them by name too.
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly Delegate _handler;
        private readonly BindingPlan _bindingPlan;

        private DelegateToolRegistration(string name, string? description, JsonElement inputSchema, Delegate handler, BindingPlan bindingPlan, McpToolAnnotations? annotations)
            : base(name, description, inputSchema, annotations)
        {
            _handler = handler;
            _bindingPlan = bindingPlan;
        }

        public static DelegateToolRegistration Create(string name, string? description, Delegate handler, McpToolAnnotations? annotations = null)
        {
            var plan = BindingPlan.Create(handler.Method);
            var schema = plan.BuildInputSchema();
            return new DelegateToolRegistration(name, description, schema, handler, plan, annotations);
        }

        public override async Task<McpToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
        {
            var invokeArgs = _bindingPlan.BindArguments(args, ct);
            object? result;
            try
            {
                result = _handler.DynamicInvoke(invokeArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // Surface the handler's own exception (and message), not the reflection wrapper.
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
            var value = await AwaitIfNeededAsync(result).ConfigureAwait(false);
            return ConvertToToolResult(value);
        }

        private static McpToolResult ConvertToToolResult(object? value)
        {
            switch (value)
            {
                case McpToolResult toolResult:
                    return Normalize(toolResult);
                case McpContent content:
                    return new McpToolResult { Content = ConvertContent([content]) };
                case IEnumerable<McpContent> contents:
                    return new McpToolResult { Content = ConvertContent(contents) };
                case null:
                    return McpToolResults.Text("");
                case string s:
                    return McpToolResults.Text(s);
                case JsonElement je:
                    return McpToolResults.Text(je.GetRawText());
                default:
                    if (IsSimpleScalar(value.GetType()))
                        return McpToolResults.Text(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                    return McpToolResults.Text(JsonSerializer.Serialize(value, ToolJsonOptions));
            }
        }

        private static bool IsSimpleScalar(Type type)
        {
            if (type.IsEnum) return true;
            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => true,
                TypeCode.Byte => true,
                TypeCode.SByte => true,
                TypeCode.Int16 => true,
                TypeCode.UInt16 => true,
                TypeCode.Int32 => true,
                TypeCode.UInt32 => true,
                TypeCode.Int64 => true,
                TypeCode.UInt64 => true,
                TypeCode.Single => true,
                TypeCode.Double => true,
                TypeCode.Decimal => true,
                TypeCode.String => true,
                _ => false
            };
        }

        private static async Task<object?> AwaitIfNeededAsync(object? result)
        {
            if (result is null) return null;

            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                    return taskType.GetProperty("Result")?.GetValue(task);
                return null;
            }

            var type = result.GetType();
            if (type.FullName is { } fullName && fullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal))
            {
                var asTask = type.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
                if (asTask != null && asTask.Invoke(result, null) is Task vtTask)
                {
                    await vtTask.ConfigureAwait(false);
                    var vtTaskType = vtTask.GetType();
                    if (vtTaskType.IsGenericType)
                        return vtTaskType.GetProperty("Result")?.GetValue(vtTask);
                    return null;
                }
            }

            return result;
        }

        [RequiresUnreferencedCode(DelegateToolRequiresMessage)]
        [RequiresDynamicCode(DelegateToolRequiresMessage)]
        private sealed class BindingPlan
        {
            private readonly List<BindingParameter> _parameters;
            private readonly bool _hasCancellationToken;
            private readonly bool _bindWholeObject;

            private BindingPlan(List<BindingParameter> parameters, bool hasCancellationToken, bool bindWholeObject)
            {
                _parameters = parameters;
                _hasCancellationToken = hasCancellationToken;
                _bindWholeObject = bindWholeObject;
            }

            public static BindingPlan Create(MethodInfo method)
            {
                var allParams = method.GetParameters();
                var hasCt = allParams.Length > 0 && allParams[^1].ParameterType == typeof(CancellationToken);
                var logicalParams = hasCt ? allParams[..^1] : allParams;

                // Only a POCO/record/dictionary binds from the whole arguments object; a single
                // List<T>, Guid, DateTime, enum, ... is an ordinary named property.
                var bindWhole = logicalParams.Length == 1 &&
                                McpSchemaGenerator.IsObjectLike(logicalParams[0].ParameterType);

                var parameters = logicalParams
                    .Select(p => new BindingParameter(p))
                    .ToList();

                return new BindingPlan(parameters, hasCt, bindWhole);
            }

            public JsonElement BuildInputSchema()
            {
                if (_bindWholeObject && _parameters.Count == 1)
                {
                    // The root of a tool input schema is always an object (never nullable).
                    var p0 = _parameters[0];
                    var schema = McpSchemaGenerator.Generate(p0.ParameterType, allowsNull: false, p0.Nullability);
                    return JsonSerializer.SerializeToElement(schema, ToolJsonOptions);
                }

                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                var required = new List<string>();

                foreach (var p in _parameters)
                {
                    var propSchema = McpSchemaGenerator.Generate(p.ParameterType, p.AllowsNull, p.Nullability);
                    if (!string.IsNullOrWhiteSpace(p.Description))
                        propSchema["description"] = p.Description;
                    properties[p.JsonName] = propSchema;
                    if (p.IsRequired)
                        required.Add(p.JsonName);
                }

                var root = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = properties
                };

                if (required.Count > 0)
                    root["required"] = required;

                return JsonSerializer.SerializeToElement(root, ToolJsonOptions);
            }

            public object?[] BindArguments(JsonElement args, CancellationToken ct)
            {
                var values = new object?[_parameters.Count + (_hasCancellationToken ? 1 : 0)];

                if (_bindWholeObject && _parameters.Count == 1)
                {
                    values[0] = Deserialize(args, _parameters[0].ParameterType);
                }
                else
                {
                    foreach (var (p, idx) in _parameters.Select((p, i) => (p, i)))
                    {
                        if (!TryGetProperty(args, p.JsonName, out var prop))
                        {
                            if (p.HasDefaultValue)
                            {
                                values[idx] = p.DefaultValue;
                                continue;
                            }

                            if (p.AllowsNull)
                            {
                                values[idx] = null;
                                continue;
                            }

                            throw new ArgumentException($"Missing required argument '{p.JsonName}'.");
                        }

                        values[idx] = ConvertElement(prop, p.ParameterType);
                    }
                }

                if (_hasCancellationToken)
                    values[^1] = ct;

                return values;
            }

            private static bool TryGetProperty(JsonElement args, string name, out JsonElement value)
            {
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out value))
                    return true;

                if (args.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in args.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            value = prop.Value;
                            return true;
                        }
                    }
                }

                value = default;
                return false;
            }

            private static object? ConvertElement(JsonElement element, Type targetType)
            {
                if (targetType == typeof(JsonElement))
                    return element.Clone();

                if (element.ValueKind == JsonValueKind.Null)
                {
                    if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                        return null;
                    throw new ArgumentException($"null is not a valid value for {targetType.Name}.");
                }

                targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

                if (targetType == typeof(string))
                    return element.ValueKind == JsonValueKind.Null ? null : element.GetString();

                if (targetType == typeof(bool) || targetType == typeof(bool?))
                    return element.ValueKind == JsonValueKind.Null ? null : element.GetBoolean();

                if (targetType.IsEnum)
                {
                    if (element.ValueKind == JsonValueKind.String)
                        return Enum.Parse(targetType, element.GetString() ?? "", ignoreCase: true);
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i))
                        return Enum.ToObject(targetType, i);
                }

                if (IsNumber(targetType))
                {
                    if (element.ValueKind == JsonValueKind.Null)
                        return null;

                    var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
                    if (underlying == typeof(decimal) && element.TryGetDecimal(out var dec))
                        return dec;

                    var isIntegral = IsIntegralNumber(targetType);
                    if (isIntegral)
                    {
                        if (element.TryGetInt64(out var l))
                            return Convert.ChangeType(l, Nullable.GetUnderlyingType(targetType) ?? targetType, System.Globalization.CultureInfo.InvariantCulture);
                        // jsonschema accepts an integral float (2.0) as an integer; so does binding.
                        if (element.TryGetDecimal(out var integral) && decimal.Truncate(integral) == integral)
                            return Convert.ChangeType(integral, targetType, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        if (element.TryGetDouble(out var d))
                            return Convert.ChangeType(d, Nullable.GetUnderlyingType(targetType) ?? targetType, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                return Deserialize(element, targetType);
            }

            private static object? Deserialize(JsonElement element, Type targetType)
                => JsonSerializer.Deserialize(element, targetType, ToolJsonOptions);

            private static bool IsNumber(Type t)
            {
                t = Nullable.GetUnderlyingType(t) ?? t;
                return Type.GetTypeCode(t) switch
                {
                    TypeCode.Byte => true,
                    TypeCode.SByte => true,
                    TypeCode.Int16 => true,
                    TypeCode.UInt16 => true,
                    TypeCode.Int32 => true,
                    TypeCode.UInt32 => true,
                    TypeCode.Int64 => true,
                    TypeCode.UInt64 => true,
                    TypeCode.Single => true,
                    TypeCode.Double => true,
                    TypeCode.Decimal => true,
                    _ => false
                };
            }

            private static bool IsIntegralNumber(Type t)
            {
                t = Nullable.GetUnderlyingType(t) ?? t;
                return Type.GetTypeCode(t) switch
                {
                    TypeCode.Byte => true,
                    TypeCode.SByte => true,
                    TypeCode.Int16 => true,
                    TypeCode.UInt16 => true,
                    TypeCode.Int32 => true,
                    TypeCode.UInt32 => true,
                    TypeCode.Int64 => true,
                    TypeCode.UInt64 => true,
                    _ => false
                };
            }
        }

        [RequiresUnreferencedCode(DelegateToolRequiresMessage)]
        [RequiresDynamicCode(DelegateToolRequiresMessage)]
        private sealed class BindingParameter
        {
            public string JsonName { get; }
            public Type ParameterType { get; }
            public bool HasDefaultValue { get; }
            public object? DefaultValue { get; }
            public bool AllowsNull { get; }
            public bool IsRequired { get; }
            public NullabilityInfo Nullability { get; }
            public string? Description { get; }

            public BindingParameter(ParameterInfo p)
            {
                ParameterType = p.ParameterType;
                var paramName = p.Name ?? throw new ArgumentException("Delegate parameters must have names.");
                JsonName = JsonNamingPolicy.CamelCase.ConvertName(paramName);

                HasDefaultValue = p.HasDefaultValue;
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null;

                // NullabilityInfoContext is not thread-safe; registration is cheap enough for one per call.
                Nullability = new NullabilityInfoContext().Create(p);
                AllowsNull = McpSchemaGenerator.AllowsNull(ParameterType, Nullability);
                Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description;

                IsRequired = !HasDefaultValue && !AllowsNull;
            }
        }
    }

    /// <summary>
    /// JSON Schema generation for tool parameters (the .NET counterpart of Python's
    /// <c>_python_type_to_json_schema</c> / <c>_typeddict_to_json_schema</c>).
    /// </summary>
    /// <remarks>
    /// Python emits Optional[X] as the schema of X (None is dropped from the union) and leaves the
    /// field out of <c>required</c> only for NotRequired TypedDict keys, so jsonschema rejects an
    /// explicit null. Here a parameter or property that is nullable (<c>int?</c>, or an NRT
    /// <c>string?</c>/<c>Foo?</c>) is both left out of <c>required</c> and allows null
    /// (<c>{"type": ["integer", "null"]}</c>): the model often sends null for an optional
    /// argument, and the handler can accept it.
    /// </remarks>
    [RequiresUnreferencedCode(DelegateToolRequiresMessage)]
    [RequiresDynamicCode(DelegateToolRequiresMessage)]
    internal static class McpSchemaGenerator
    {
        // Beyond this nesting the schema is truncated to a permissive one (keeping the JSON well
        // under System.Text.Json's default max depth of 64), which also stops
        // generic types that expand forever (Foo<T> { Foo<List<T>> Next }).
        private const int MaxDepth = 12;

        /// <summary>Schema for <paramref name="type"/>, allowing null when <paramref name="allowsNull"/>.</summary>
        public static Dictionary<string, object?> Generate(Type type, bool allowsNull, NullabilityInfo? nullability = null)
            => Generate(type, allowsNull, nullability, new Stack<Type>());

        private static Dictionary<string, object?> Generate(Type type, bool allowsNull, NullabilityInfo? nullability, Stack<Type> path)
        {
            var schema = GenerateCore(type, nullability, path);
            return allowsNull ? MakeNullable(schema) : schema;
        }

        private static Dictionary<string, object?> GenerateCore(Type type, NullabilityInfo? nullability, Stack<Type> path)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (IsPermissive(type))
                return new Dictionary<string, object?>();

            if (type.IsEnum)
                return new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = Enum.GetNames(type)
                };

            if (type == typeof(string) || type == typeof(char))
                return new Dictionary<string, object?> { ["type"] = "string" };

            if (type == typeof(bool))
                return new Dictionary<string, object?> { ["type"] = "boolean" };

            if (type == typeof(Guid))
                return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "uuid" };

            if (type == typeof(Uri))
                return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "uri" };

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date-time" };

            if (type == typeof(DateOnly))
                return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date" };

            if (type == typeof(TimeOnly))
                return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "time" };

            if (type == typeof(TimeSpan) || type == typeof(Version))
                return new Dictionary<string, object?> { ["type"] = "string" };

            // System.Text.Json reads and writes byte[] as a base64 string.
            if (type == typeof(byte[]))
                return new Dictionary<string, object?> { ["type"] = "string", ["contentEncoding"] = "base64" };

            if (IsIntegral(type))
                return new Dictionary<string, object?> { ["type"] = "integer" };

            if (IsFloating(type))
                return new Dictionary<string, object?> { ["type"] = "number" };

            if (path.Count >= MaxDepth)
                return new Dictionary<string, object?>();

            // Dictionaries before enumerables: a dictionary is also IEnumerable<KeyValuePair<,>>.
            if (TryGetDictionaryValueType(type, out var valueType))
            {
                var valueNullability = nullability is { GenericTypeArguments.Length: 2 } ? nullability.GenericTypeArguments[1] : null;
                path.Push(type);
                try
                {
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = Generate(valueType, AllowsNull(valueType, valueNullability), valueNullability, path)
                    };
                }
                finally { path.Pop(); }
            }

            if (TryGetEnumerableElementType(type, out var elementType))
            {
                if (elementType == null)
                    return new Dictionary<string, object?> { ["type"] = "array" };

                var elementNullability = type.IsArray
                    ? nullability?.ElementType
                    : nullability is { GenericTypeArguments.Length: 1 } ? nullability.GenericTypeArguments[0] : null;
                path.Push(type);
                try
                {
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = Generate(elementType, AllowsNull(elementType, elementNullability), elementNullability, path)
                    };
                }
                finally { path.Pop(); }
            }

            // A type that contains itself (directly or through other types): cut the cycle with
            // a permissive object schema instead of recursing until the stack overflows.
            if (path.Contains(type))
                return new Dictionary<string, object?> { ["type"] = "object" };

            path.Push(type);
            try
            {
                return GenerateObjectSchema(type, path);
            }
            finally { path.Pop(); }
        }

        private static Dictionary<string, object?> GenerateObjectSchema(Type type, Stack<Type> path)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            var required = new List<string>();

            foreach (var prop in GetBindableProperties(type))
            {
                var jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                               ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);

                var nullability = new NullabilityInfoContext().Create(prop);
                var allowsNull = AllowsNull(prop.PropertyType, nullability);

                var propSchema = Generate(prop.PropertyType, allowsNull, nullability, path);
                var description = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (!string.IsNullOrWhiteSpace(description))
                    propSchema["description"] = description;
                properties[jsonName] = propSchema;

                var requiredByAttr = prop.GetCustomAttribute<RequiredAttribute>() != null ||
                                     prop.GetCustomAttribute<JsonRequiredAttribute>() != null ||
                                     prop.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null;
                if (requiredByAttr || !allowsNull)
                    required.Add(jsonName);
            }

            var schema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (required.Count > 0)
                schema["required"] = required;

            var typeDescription = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(typeDescription))
                schema["description"] = typeDescription;

            return schema;
        }

        // Properties System.Text.Json can populate: public, non-indexer, not [JsonIgnore], and
        // either settable (set/init) or bound through a constructor parameter of the same name.
        private static IEnumerable<PropertyInfo> GetBindableProperties(Type type)
        {
            var ctorParamNames = new HashSet<string>(
                type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .SelectMany(c => c.GetParameters())
                    .Select(p => p.Name!)
                    .Where(n => n != null),
                StringComparer.OrdinalIgnoreCase);

            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
                .Where(p => p.SetMethod is { IsPublic: true } || ctorParamNames.Contains(p.Name) ||
                            p.GetCustomAttribute<JsonIncludeAttribute>() != null);
        }

        /// <summary>
        /// Whether a value of <paramref name="type"/> may be null: <c>Nullable&lt;T&gt;</c>, or a
        /// reference type annotated nullable. NRT-oblivious reference types are treated as non-null.
        /// </summary>
        public static bool AllowsNull(Type type, NullabilityInfo? nullability)
        {
            if (Nullable.GetUnderlyingType(type) != null)
                return true;
            if (type.IsValueType)
                return false;
            return nullability?.ReadState == NullabilityState.Nullable ||
                   nullability?.WriteState == NullabilityState.Nullable;
        }

        private static Dictionary<string, object?> MakeNullable(Dictionary<string, object?> schema)
        {
            // A schema without "type" already accepts null.
            if (!schema.TryGetValue("type", out var t))
                return schema;

            schema["type"] = t switch
            {
                string s => new[] { s, "null" },
                _ => t
            };
            if (schema.TryGetValue("enum", out var e) && e is string[] names)
                schema["enum"] = names.Cast<object?>().Append(null).ToArray();
            return schema;
        }

        /// <summary>
        /// True for types that bind from the whole tool arguments object when they are a tool's
        /// only parameter: POCOs, records and string-keyed dictionaries. Collections, scalars and
        /// well-known value types (Guid, Uri, DateTime, ...) are named parameters instead.
        /// </summary>
        public static bool IsObjectLike(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (IsPermissive(type) || IsScalar(type))
                return false;
            if (TryGetDictionaryValueType(type, out _))
                return true;
            if (TryGetEnumerableElementType(type, out _))
                return false;
            return type.IsClass || (type.IsValueType && !type.IsPrimitive) || type.IsInterface;
        }

        private static bool IsPermissive(Type type) =>
            type == typeof(JsonElement) || type == typeof(object) || type == typeof(JsonDocument) ||
            type == typeof(System.Text.Json.Nodes.JsonNode) || type == typeof(System.Text.Json.Nodes.JsonValue);

        private static bool IsScalar(Type type) =>
            type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Guid) || type == typeof(Uri) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) ||
            type == typeof(TimeSpan) || type == typeof(Version) || type == typeof(byte[]) ||
            type == typeof(Half) || type == typeof(Int128) || type == typeof(UInt128);

        private static bool IsIntegral(Type t) =>
            t == typeof(Int128) || t == typeof(UInt128) || Type.GetTypeCode(t) switch
            {
                TypeCode.Byte => true,
                TypeCode.SByte => true,
                TypeCode.Int16 => true,
                TypeCode.UInt16 => true,
                TypeCode.Int32 => true,
                TypeCode.UInt32 => true,
                TypeCode.Int64 => true,
                TypeCode.UInt64 => true,
                _ => false
            };

        private static bool IsFloating(Type t) =>
            t == typeof(Half) || Type.GetTypeCode(t) switch
            {
                TypeCode.Single => true,
                TypeCode.Double => true,
                TypeCode.Decimal => true,
                _ => false
            };

        // elementType is null for a non-generic IEnumerable (an array of anything).
        private static bool TryGetEnumerableElementType(Type type, out Type? elementType)
        {
            elementType = null;
            if (type == typeof(string))
                return false;

            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
                return true;
            }

            var enumerable = FindGenericInterface(type, typeof(IEnumerable<>));
            if (enumerable != null)
            {
                elementType = enumerable.GetGenericArguments()[0];
                return true;
            }

            return typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
        }

        private static bool TryGetDictionaryValueType(Type type, out Type valueType)
        {
            var dict = FindGenericInterface(type, typeof(IDictionary<,>)) ??
                       FindGenericInterface(type, typeof(IReadOnlyDictionary<,>));
            if (dict != null)
            {
                valueType = dict.GetGenericArguments()[1];
                return true;
            }

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
            {
                valueType = typeof(object);
                return true;
            }

            valueType = typeof(void);
            return false;
        }

        private static Type? FindGenericInterface(Type type, Type genericDefinition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition)
                return type;
            return type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericDefinition);
        }
    }
}
