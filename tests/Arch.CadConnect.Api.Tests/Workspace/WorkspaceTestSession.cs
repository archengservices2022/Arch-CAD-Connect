using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Tests.Workspace;

internal static class WorkspaceTestSession
{
    public static readonly ArchServerUri Server = ArchServerUri.Parse("https://plm.example.com");

    public static IArchSession Make() => new DesktopSession(
        Server,
        new ArchIdentity("u1", "Test User", "t@orga.com", "ENGINEER", "org1", "ORGA", "Org A"),
        "arch_dt_TESTTOKEN123456",
        DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow);
}
