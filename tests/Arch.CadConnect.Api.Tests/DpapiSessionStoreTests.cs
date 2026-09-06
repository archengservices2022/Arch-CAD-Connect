using Arch.CadConnect.Api;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Tests;

public class DpapiSessionStoreTests
{
    private static PersistedDesktopSession Sample() => new(
        "https://plm.example.com",
        "arch_dt_supersecrettoken",
        DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow,
        new ArchIdentity("u1", "T", "t@o.com", "ENGINEER", "org1", "ORGA", "Org A"));

    [Fact]
    public void Save_then_load_round_trips_and_the_file_is_not_plaintext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows-only; the client only ships on Windows.
        }

        var dir = Path.Combine(Path.GetTempPath(), "arch-cc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiSessionStore(dir);
            var original = Sample();
            store.Save(original);

            var bytes = File.ReadAllBytes(store.FilePath);
            var asText = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("arch_dt_supersecrettoken", asText);
            Assert.DoesNotContain("plm.example.com", asText);

            var loaded = store.TryLoad();
            Assert.NotNull(loaded);
            Assert.Equal(original.Token, loaded!.Token);
            Assert.Equal(original.ServerOrigin, loaded.ServerOrigin);
            Assert.Equal("ORGA", loaded.Identity.OrganizationCode);

            store.Clear();
            Assert.Null(store.TryLoad());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Missing_or_corrupt_file_loads_as_no_session_never_throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "arch-cc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiSessionStore(dir);
            Assert.Null(store.TryLoad()); // missing

            Directory.CreateDirectory(dir);
            File.WriteAllBytes(store.FilePath, new byte[] { 1, 2, 3, 4, 5 }); // not a DPAPI blob
            Assert.Null(store.TryLoad()); // corrupt -> null, no throw
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
