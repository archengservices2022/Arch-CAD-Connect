using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Documents;

namespace Arch.CadConnect.Core.Tests;

public class ActiveDocumentTrackerTests
{
    [Fact]
    public void Starts_with_no_document()
    {
        Assert.Equal(CadDocumentContext.None, new ActiveDocumentTracker().Current);
    }

    [Fact]
    public void Set_raises_change_only_when_value_actually_differs()
    {
        var tracker = new ActiveDocumentTracker();
        var seen = new List<CadDocumentContext>();
        tracker.Changed += seen.Add;

        var a = CadDocumentContext.Create(@"C:\w\a.ipt", "a", false);
        tracker.Set(a);
        tracker.Set(CadDocumentContext.Create(@"C:\w\a.ipt", "a", false)); // equal -> no event
        var b = CadDocumentContext.Create(@"C:\w\b.iam", "b", false);
        tracker.Set(b);
        tracker.Set(null); // -> None

        Assert.Equal(new[] { a, b, CadDocumentContext.None }, seen);
        Assert.Equal(CadDocumentContext.None, tracker.Current);
    }

    [Fact]
    public void Clear_resets_to_none()
    {
        var tracker = new ActiveDocumentTracker();
        tracker.Set(CadDocumentContext.Create(@"C:\w\a.ipt", "a", false));
        tracker.Clear();
        Assert.Equal(CadDocumentContext.None, tracker.Current);
    }
}
