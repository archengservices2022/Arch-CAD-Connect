using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignExecutionJournalTests
{
    // 26. cleanup deletes only operation-created destination files
    [Fact]
    public void CleanupOnlyDeletesJournalledPaths()
    {
        var journal = new CopyDesignExecutionJournal();
        journal.RecordCreated("cad-1", @"C:\dst\a.ipt");
        journal.RecordCreated("cad-2", @"C:\dst\b.iam");

        var deleted = new List<string>();
        var report = CopyDesignCleanupPlanner.CleanUp(journal, deleted.Add);

        Assert.True(report.AllSucceeded);
        Assert.Equal(new[] { @"C:\dst\b.iam", @"C:\dst\a.ipt" }, deleted); // reverse creation order
    }

    // 27. cleanup never deletes pre-existing destination/source
    [Fact]
    public void CleanupNeverConsidersAnythingOutsideTheJournal()
    {
        var journal = new CopyDesignExecutionJournal();
        journal.RecordCreated("cad-1", @"C:\dst\a.ipt");

        var deleted = new List<string>();
        CopyDesignCleanupPlanner.CleanUp(journal, deleted.Add);

        // The journal structurally cannot name a source or pre-existing file
        // - it only ever contains what THIS operation itself recorded.
        Assert.DoesNotContain(@"C:\src\a.ipt", deleted);
        Assert.Single(deleted);
    }

    [Fact]
    public void AFailedDeleteIsReportedButDoesNotAbortTheRest()
    {
        var journal = new CopyDesignExecutionJournal();
        journal.RecordCreated("cad-1", @"C:\dst\a.ipt");
        journal.RecordCreated("cad-2", @"C:\dst\b.ipt");

        var report = CopyDesignCleanupPlanner.CleanUp(journal, path =>
        {
            if (path == @"C:\dst\b.ipt")
            {
                throw new IOException("locked");
            }
        });

        Assert.False(report.AllSucceeded);
        var failure = Assert.Single(report.Failures);
        Assert.Equal(@"C:\dst\b.ipt", failure.AbsolutePath);
        Assert.Contains(report.Outcomes, o => o.AbsolutePath == @"C:\dst\a.ipt" && o.Deleted);
    }

    [Fact]
    public void EmptyJournalCleansUpNothing()
    {
        var journal = new CopyDesignExecutionJournal();
        var deleted = new List<string>();
        var report = CopyDesignCleanupPlanner.CleanUp(journal, deleted.Add);
        Assert.True(report.AllSucceeded);
        Assert.Empty(deleted);
    }
}
