namespace Arch.CadConnect.Core.Session;

/// <summary>
/// Local, at-rest persistence for a single desktop session. Implementations
/// MUST encrypt the token (it is a live credential) and MUST scope it to the
/// current OS user. A load failure (corrupt / missing / wrong user / decrypt
/// error) is reported as "no session", never as an exception the caller has
/// to special-case.
/// </summary>
public interface ISessionStore
{
    void Save(PersistedDesktopSession session);

    /// <summary>Returns null when there is no readable session for this user.</summary>
    PersistedDesktopSession? TryLoad();

    void Clear();
}

/// <summary>Non-persistent store, for tests and headless use.</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private PersistedDesktopSession? _session;

    public void Save(PersistedDesktopSession session) => _session = session;

    public PersistedDesktopSession? TryLoad() => _session;

    public void Clear() => _session = null;
}
