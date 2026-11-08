namespace DeskBox.Services;

/// <summary>NTFS identity of a desktop entry DeskBox hid (volume serial + file index).</summary>
public readonly record struct MappedOrganizeFileIdentity(uint VolumeSerial, ulong FileIndex);

/// <summary>
/// GOXF fork, 「映射整理」: whether DeskBox currently keeps mapped entries
/// hidden. Every exit path (tray exit, restart / update, Windows
/// shutdown / restart / sign-out, crash) restores the entries first; a
/// session end that another application cancels resumes hiding, a real exit
/// never does. Pure and thread-safe; unit-tested in ForkHiddenRestoreTests.
/// </summary>
public sealed class MappedOrganizeExitGate
{
    private readonly object _gate = new();
    private State _state;

    private enum State
    {
        Running,
        RestoredForSessionEnd,
        Exiting,
    }

    /// <summary>True while mapped entries must stay visible (restored for exit / session end).</summary>
    public bool IsRestored
    {
        get
        {
            lock (_gate)
            {
                return _state != State.Running;
            }
        }
    }

    /// <summary>
    /// Records a restore. <paramref name="sessionEnd"/> is a Windows
    /// QUERYENDSESSION / ENDSESSION, which can still be cancelled; anything
    /// else is final. Returns true when this call is the first restore.
    /// </summary>
    public bool Restore(bool sessionEnd)
    {
        lock (_gate)
        {
            bool first = _state == State.Running;
            if (!sessionEnd)
            {
                _state = State.Exiting;
            }
            else if (first)
            {
                _state = State.RestoredForSessionEnd;
            }

            return first;
        }
    }

    /// <summary>The session end was cancelled: hide again unless DeskBox is exiting anyway.</summary>
    public bool TryResume()
    {
        lock (_gate)
        {
            if (_state != State.RestoredForSessionEnd)
            {
                return false;
            }

            _state = State.Running;
            return true;
        }
    }
}

/// <summary>Raised by the lifecycle watcher when a Windows session end is cancelled (WM_ENDSESSION with FALSE).</summary>
public static class ForkSessionEndSignal
{
    public static event Action? Cancelled;

    public static void RaiseCancelled()
    {
        try
        {
            Cancelled?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Session-end cancel handler failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Where a DeskBox-hidden desktop entry went after the desktop reported it
/// deleted. The hidden attribute must never travel with it: a moved entry is
/// made visible at its new location and leaves the ledger.
/// </summary>
public static class MappedOrganizeLeakPolicy
{
    public enum Outcome
    {
        /// <summary>Really deleted (or moved to another volume): just forget it.</summary>
        Gone,

        /// <summary>Still a desktop entry (a rename seen as delete): keep the record.</summary>
        StillOnDesktop,

        /// <summary>Moved elsewhere (another folder, the Recycle Bin): clear the attribute there.</summary>
        LeftDesktop,
    }

    public static Outcome Classify(string? resolvedPath, Func<string, bool> isDesktopEntry)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return Outcome.Gone;
        }

        return isDesktopEntry(resolvedPath) ? Outcome.StillOnDesktop : Outcome.LeftDesktop;
    }

    /// <summary>Turns a GetFinalPathNameByHandle result (\\?\C:\x, \\?\UNC\srv\share\x) into a normal path.</summary>
    public static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}

/// <summary>
/// Startup recovery: before widgets are restored, every entry the ledger says
/// DeskBox hid is made visible again (a previous run was killed, crashed, or
/// was an older build), the ledger is emptied and the outcome is logged. The
/// mapped-organize service then hides the current members again.
/// </summary>
public static class MappedOrganizeStartupRecovery
{
    /// <summary>The ledger entries that still exist and are hidden.</summary>
    public static IReadOnlyList<string> PlanRestore(IEnumerable<string> ledger, Func<string, FileAttributes?> probe) =>
        ledger
            .Where(path => probe(path) is { } attributes && (attributes & FileAttributes.Hidden) != 0)
            .ToList();

    public static void Run()
    {
        string ledgerPath = MappedOrganizeHiddenRegistry.LedgerPath();
        if (!File.Exists(ledgerPath))
        {
            return;
        }

        IReadOnlyList<string> ledger = MappedOrganizeHiddenLedger.Parse(File.ReadAllText(ledgerPath));
        if (ledger.Count == 0)
        {
            return;
        }

        int restored = 0;
        var failed = new List<string>();
        foreach (string path in PlanRestore(ledger, ProbeAttributes))
        {
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
                restored++;
            }
            catch (Exception ex)
            {
                failed.Add(path);
                App.Log($"[MappedOrganize] Startup recovery could not show '{path}': {ex.Message}");
            }
        }

        // Entries that could not be restored stay recorded so the service
        // (or the uninstaller) retries them; everything else is visible now.
        File.WriteAllText(ledgerPath, MappedOrganizeHiddenLedger.Serialize(failed));
        MappedOrganizeHiddenRegistry.Publish(failed);
        App.Log($"[MappedOrganize] Startup recovery: ledger had {ledger.Count} entries, restored {restored}, failed {failed.Count}.");
    }

    private static FileAttributes? ProbeAttributes(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
