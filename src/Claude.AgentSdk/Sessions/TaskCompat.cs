// Claude Agent SDK for .NET — async/cancellation primitives.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/_internal/_task_compat.py
// (Python uses asyncio/trio; .NET uses Task + CancellationToken natively.)

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Claude.AgentSdk.Sessions;

/// <summary>
/// Helpers that approximate the Python <c>_task_compat</c> utilities used by
/// the sessions subsystem. .NET's <see cref="Task"/> + <see cref="CancellationToken"/>
/// already provides the equivalent of <c>spawn_detached</c> via
/// <see cref="Task.Run(Func{Task})"/>; this class only adds the timeout helper
/// that <c>session_resume._with_timeout</c> relies on.
/// </summary>
internal static class TaskCompat
{
    /// <summary>
    /// Await <paramref name="work"/> with a millisecond timeout, wrapping
    /// timeouts and exceptions as <see cref="SessionStoreOperationException"/>
    /// with descriptive context. Mirrors session_resume.py
    /// <c>_with_timeout</c>.
    /// </summary>
    public static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan timeout,
        string what,
        CancellationToken cancellationToken)
    {
        // Python: anyio.fail_after(0 or less) — the deadline has already passed.
        // Fail deterministically instead of racing a store call that may
        // complete synchronously without observing the token.
        if (timeout <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SessionStoreOperationException(
                $"{what} timed out after {(int)timeout.TotalMilliseconds}ms during resume materialization");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionStoreOperationException(
                $"{what} timed out after {(int)timeout.TotalMilliseconds}ms during resume materialization");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not SessionStoreOperationException)
        {
            throw new SessionStoreOperationException(
                $"{what} failed during resume materialization: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Suppress only OperationCanceledException originating from
    /// <paramref name="token"/>; rethrow other cancellations.
    /// </summary>
    public static bool ShouldSwallowCancellation(OperationCanceledException ex, CancellationToken token)
        => token.IsCancellationRequested && ex.CancellationToken == token;

    /// <summary>
    /// Does <paramref name="instance"/>'s runtime type implement
    /// <paramref name="methodName"/> of <see cref="ISessionStore"/> itself, rather
    /// than inheriting the interface's default (which throws
    /// <see cref="NotImplementedException"/>)? Mirrors Python's
    /// <c>_store_implements</c> default-interface detection.
    /// </summary>
    /// <remarks>
    /// Looks for an implicit (same name) or explicit
    /// (<c>Claude.AgentSdk.ISessionStore.Name</c>) implementation with the
    /// interface method's parameter types on the class hierarchy.
    /// <see cref="Type.GetInterfaceMap"/> would be exact but is not supported
    /// under NativeAOT; the <see cref="DynamicallyAccessedMembersAttribute"/>
    /// on <see cref="ISessionStore"/> keeps every implementation's methods
    /// visible to this lookup in trimmed and AOT apps.
    /// </remarks>
    public static bool OverridesMethod(ISessionStore instance, string methodName)
    {
        var interfaceMethod = typeof(ISessionStore).GetMethod(methodName);
        if (interfaceMethod is null)
            return false;
        var parameterTypes = interfaceMethod.GetParameters().Select(p => p.ParameterType).ToArray();
        var explicitName = typeof(ISessionStore).FullName + "." + methodName;

        for (var type = instance.GetType(); type is not null && type != typeof(object); type = BaseTypeOf(type))
        {
            foreach (var method in DeclaredInstanceMethods(type))
            {
                if ((method.Name == methodName || method.Name == explicitName) &&
                    method.ReturnType == interfaceMethod.ReturnType &&
                    method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes))
                    return true;
            }
        }
        return false;
    }

    private static MethodInfo[] DeclaredInstanceMethods(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    // Justification: every class in an ISessionStore implementation's hierarchy
    // that declares a method this lookup can match either implements
    // ISessionStore itself (an explicit implementation requires it) or
    // contributes public methods to a derived implementer. The type-hierarchy
    // [DynamicallyAccessedMembers] on ISessionStore preserves both: its
    // PublicMethods include inherited public methods and every implementing
    // class keeps its own non-public methods.
    [UnconditionalSuppressMessage("Trimming", "IL2073:DynamicallyAccessedMembers",
        Justification = "ISessionStore's type-hierarchy annotation covers the whole implementation hierarchy; see comment.")]
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)]
    private static Type? BaseTypeOf(Type type) => type.BaseType;
}
