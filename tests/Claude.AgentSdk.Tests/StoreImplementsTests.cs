// SessionStoreValidation.StoreImplements: optional-method detection without
// Type.GetInterfaceMap (unsupported under NativeAOT).

using Claude.AgentSdk.Sessions;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class StoreImplementsTests
{
    private class Minimal : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(null);

        // Same name, different signature: not an implementation.
        public Task DeleteAsync(string sessionId) => Task.CompletedTask;
    }

    private class Explicit : Minimal, ISessionStore
    {
        Task<IReadOnlyList<SessionStoreListEntry>> ISessionStore.ListSessionsAsync(string projectKey, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SessionStoreListEntry>>([]);
    }

    private sealed class DerivedFromExplicit : Explicit;

    private class PublicBase
    {
        public Task<IReadOnlyList<string>> ListSubkeysAsync(SessionListSubkeysKey key, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class InheritsPublicImplementation : PublicBase, ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(null);
    }

    [Fact]
    public void DefaultInterfaceMethodsAreNotImplementations()
    {
        var store = new Minimal();
        Assert.False(SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSessionsAsync)));
        Assert.False(SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.DeleteAsync)));
        Assert.False(SessionStoreValidation.StoreImplements(store, "NoSuchMethod"));
    }

    [Fact]
    public void ImplicitAndExplicitImplementationsAreDetected()
    {
        Assert.True(SessionStoreValidation.StoreImplements(new InMemorySessionStore(), nameof(ISessionStore.ListSessionsAsync)));
        Assert.True(SessionStoreValidation.StoreImplements(new Explicit(), nameof(ISessionStore.ListSessionsAsync)));
        Assert.True(SessionStoreValidation.StoreImplements(new DerivedFromExplicit(), nameof(ISessionStore.ListSessionsAsync)));
        Assert.False(SessionStoreValidation.StoreImplements(new DerivedFromExplicit(), nameof(ISessionStore.ListSubkeysAsync)));
        Assert.True(SessionStoreValidation.StoreImplements(new InheritsPublicImplementation(), nameof(ISessionStore.ListSubkeysAsync)));
    }
}
