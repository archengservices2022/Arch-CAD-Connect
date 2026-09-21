namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>ONE destination artifact this operation itself created - the
///  ONLY kind of thing cleanup is ever allowed to delete.</summary>
public sealed record CopyDesignJournalEntry(string CadDocumentId, string DestinationAbsolutePath, DateTimeOffset CreatedAtUtc);

/// <summary>
/// P6C: an operation-LOCAL, append-only record of every destination file
/// this ONE apply attempt has physically created so far. Consulted ONLY by
/// <see cref="CopyDesignCleanupPlanner"/> on failure - it is never a general
/// file inventory and is discarded at the end of the operation (in-memory
/// only; this is not the durable P6B reservation evidence, which is never
/// touched by cleanup regardless of outcome).
/// </summary>
public sealed class CopyDesignExecutionJournal
{
    private readonly List<CopyDesignJournalEntry> _entries = new();

    public IReadOnlyList<CopyDesignJournalEntry> Entries => _entries;

    /// <summary>Record that <paramref name="destinationAbsolutePath"/> was
    ///  just successfully created for <paramref name="cadDocumentId"/>. Call
    ///  this ONLY after the physical copy actually succeeded.</summary>
    public void RecordCreated(string cadDocumentId, string destinationAbsolutePath, DateTimeOffset? nowUtc = null) =>
        _entries.Add(new CopyDesignJournalEntry(cadDocumentId, destinationAbsolutePath, nowUtc ?? DateTimeOffset.UtcNow));
}

public sealed record CopyDesignCleanupOutcome(string AbsolutePath, bool Deleted, string? FailureReason);

public sealed record CopyDesignCleanupReport(IReadOnlyList<CopyDesignCleanupOutcome> Outcomes)
{
    public bool AllSucceeded => Outcomes.All(o => o.Deleted);
    public IReadOnlyList<CopyDesignCleanupOutcome> Failures => Outcomes.Where(o => !o.Deleted).ToArray();
}

/// <summary>
/// P6C: on a failed apply, deletes ONLY the destination artifacts THIS
/// operation's own journal proves it created - in REVERSE creation order
/// (undo most-recent-first) - and nothing else. It is structurally
/// impossible for this to delete a pre-existing file or a source file: the
/// only candidate set it ever considers is <see cref="CopyDesignExecutionJournal.Entries"/>,
/// which by construction contains only paths this SAME operation itself just
/// created. A delete failure for any one entry is reported, never hidden,
/// and never aborts attempting the rest.
/// </summary>
public static class CopyDesignCleanupPlanner
{
    public static CopyDesignCleanupReport CleanUp(CopyDesignExecutionJournal journal, Action<string> deleteFile)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(deleteFile);

        var outcomes = new List<CopyDesignCleanupOutcome>();
        foreach (var entry in journal.Entries.Reverse())
        {
            try
            {
                deleteFile(entry.DestinationAbsolutePath);
                outcomes.Add(new CopyDesignCleanupOutcome(entry.DestinationAbsolutePath, true, null));
            }
            catch (Exception ex)
            {
                outcomes.Add(new CopyDesignCleanupOutcome(entry.DestinationAbsolutePath, false, ex.Message));
            }
        }
        return new CopyDesignCleanupReport(outcomes);
    }
}
