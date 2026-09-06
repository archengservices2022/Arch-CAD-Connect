using System.Text.Json;

using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Core.Tests;

public class DesktopSessionTests
{
    private static ArchIdentity Identity() => new(
        "u1", "Test User", "test@orga.com", "ENGINEER", "org1", "ORGA", "Org A");

    private static DesktopSession Make(DateTimeOffset issued, DateTimeOffset expires) => new(
        ArchServerUri.Parse("https://plm.example.com"),
        Identity(),
        "arch_dt_abcdefghijklmnop",
        expires,
        issued);

    [Fact]
    public void AuthorizationHeaderValue_is_a_bearer_and_token_is_not_a_public_property()
    {
        var s = Make(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(12));
        Assert.Equal("Bearer arch_dt_abcdefghijklmnop", s.AuthorizationHeaderValue());

        // The raw token must not be reachable as a public property (only via
        // AuthorizationHeaderValue / ToPersisted).
        var props = typeof(DesktopSession).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("Token", props);
        Assert.Contains("TokenHint", props);
        Assert.Equal("…klmnop", s.TokenHint);
    }

    [Fact]
    public void IsExpired_uses_a_one_minute_safety_skew()
    {
        var expires = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var s = Make(expires.AddHours(-12), expires);

        Assert.False(s.IsExpired(expires.AddMinutes(-2)));
        Assert.True(s.IsExpired(expires.AddSeconds(-30)));  // within skew -> treat as expired
        Assert.True(s.IsExpired(expires.AddMinutes(5)));
    }

    [Fact]
    public void Persist_round_trip_preserves_everything()
    {
        var issued = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = issued.AddHours(12);
        var original = Make(issued, expires);

        var json = JsonSerializer.Serialize(original.ToPersisted());
        var back = JsonSerializer.Deserialize<PersistedDesktopSession>(json)!;
        var restored = DesktopSession.FromPersisted(back);

        Assert.Equal("https://plm.example.com", restored.Server.OriginString);
        Assert.Equal(original.AuthorizationHeaderValue(), restored.AuthorizationHeaderValue());
        Assert.Equal(expires, restored.ExpiresAtUtc);
        Assert.Equal("ORGA", restored.Identity.OrganizationCode);
    }

    [Fact]
    public void FromPersisted_reapplies_the_transport_policy_and_has_no_override()
    {
        var remoteHttp = new PersistedDesktopSession(
            "http://plm.example.com", "arch_dt_x", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow, Identity());
        var lanHttp = remoteHttp with { ServerOrigin = "http://192.168.1.50:3000" };

        // A persisted remote/LAN http origin is rejected BEFORE the token is
        // usable - there is no argument, env var or setting that lets it back in.
        Assert.Throws<ArchServerUriException>(() => DesktopSession.FromPersisted(remoteHttp));
        Assert.Throws<ArchServerUriException>(() => DesktopSession.FromPersisted(lanHttp));

        // A loopback http origin restores fine.
        var loopback = remoteHttp with { ServerOrigin = "http://localhost:3000" };
        Assert.Equal("http://localhost:3000", DesktopSession.FromPersisted(loopback).Server.OriginString);
    }

    [Fact]
    public void Empty_token_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DesktopSession(
            ArchServerUri.Parse("https://plm.example.com"), Identity(), "  ",
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow));
    }
}
