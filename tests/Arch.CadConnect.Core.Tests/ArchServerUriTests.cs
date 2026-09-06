using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Core.Tests;

public class ArchServerUriTests
{
    [Theory]
    [InlineData("https://plm.example.com", "https://plm.example.com")]
    [InlineData("https://plm.example.com/", "https://plm.example.com")]
    [InlineData("https://plm.example.com:8443/ignored/path", "https://plm.example.com:8443")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("http://127.0.0.1:3000/", "http://127.0.0.1:3000")]
    [InlineData("http://[::1]:3000", "http://[::1]:3000")]
    public void Parse_reduces_to_origin_for_allowed_inputs(string input, string expectedOrigin)
    {
        Assert.Equal(expectedOrigin, ArchServerUri.Parse(input).OriginString);
    }

    // ---- transport policy: HTTPS anywhere -------------------------------

    [Fact]
    public void Https_to_a_remote_host_is_allowed()
    {
        Assert.Equal("https://plm.example.com", ArchServerUri.Parse("https://plm.example.com").OriginString);
    }

    // ---- transport policy: plain HTTP only for exact loopback ----------

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]:3000")]
    public void Http_to_a_loopback_address_is_allowed(string input)
    {
        Assert.True(ArchServerUri.Parse(input).IsLoopback);
    }

    [Theory]
    [InlineData("http://plm.example.com")]        // remote hostname
    [InlineData("http://192.168.1.50:3000")]      // private LAN IP
    [InlineData("http://10.0.0.5")]               // private LAN IP
    [InlineData("http://workstation-01:3000")]    // machine name
    [InlineData("http://127.0.0.2:3000")]         // loopback range but not the exact address
    public void Plain_http_to_anything_but_exact_loopback_is_rejected(string input)
    {
        var ex = Assert.Throws<ArchServerUriException>(() => ArchServerUri.Parse(input));
        Assert.Contains("HTTPS is required", ex.Message);
        Assert.DoesNotContain("192.168", ex.Message);
        Assert.DoesNotContain("plm.example.com", ex.Message);
    }

    [Fact]
    public void No_environment_variable_can_make_remote_http_valid()
    {
        var saved = Environment.GetEnvironmentVariable("ARCH_ALLOW_INSECURE_DESKTOP_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("ARCH_ALLOW_INSECURE_DESKTOP_AUTH", "true");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            // Parse takes no bypass argument and reads no environment.
            Assert.Throws<ArchServerUriException>(() => ArchServerUri.Parse("http://192.168.1.50:3000"));
            Assert.Throws<ArchServerUriException>(() => ArchServerUri.Parse("http://plm.example.com"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARCH_ALLOW_INSECURE_DESKTOP_AUTH", saved);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://plm.example.com")]
    [InlineData("file:///c:/x")]
    public void Parse_rejects_missing_or_non_http_addresses(string? input)
    {
        Assert.Throws<ArchServerUriException>(() => ArchServerUri.Parse(input));
    }

    [Fact]
    public void Parse_rejects_embedded_credentials()
    {
        Assert.Throws<ArchServerUriException>(() => ArchServerUri.Parse("https://user:pw@plm.example.com"));
    }

    [Fact]
    public void MatchesOrigin_is_scheme_host_port_exact()
    {
        var uri = ArchServerUri.Parse("https://plm.example.com");
        Assert.True(uri.MatchesOrigin(new Uri("https://plm.example.com/api/desktop/session")));
        Assert.False(uri.MatchesOrigin(new Uri("https://plm.example.com:8443/x")));
        Assert.False(uri.MatchesOrigin(new Uri("http://plm.example.com/x")));
        Assert.False(uri.MatchesOrigin(new Uri("https://evil.example.com/x")));
    }

    [Fact]
    public void ResolvePath_joins_against_the_origin()
    {
        var uri = ArchServerUri.Parse("https://plm.example.com:8443");
        Assert.Equal("https://plm.example.com:8443/api/desktop/auth/login",
            uri.ResolvePath("/api/desktop/auth/login").ToString());
        Assert.Equal("https://plm.example.com:8443/api/desktop/session",
            uri.ResolvePath("api/desktop/session").ToString());
    }

    [Fact]
    public void TryParse_reports_failure_without_throwing()
    {
        Assert.False(ArchServerUri.TryParse("http://plm.example.com", out var r));
        Assert.Null(r);
        Assert.True(ArchServerUri.TryParse("https://plm.example.com", out var ok));
        Assert.NotNull(ok);
    }
}
