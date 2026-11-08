using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// 「映射整理」 hidden-attribute leak guard (GOXF fork). A DeskBox-hidden
/// desktop entry that leaves the desktop (moved / cut-pasted / dragged into
/// another folder, or recycled) keeps its file id on the same volume; the
/// desktop watcher only reports a deletion, so the entry is found again by
/// the id captured when it was hidden, made visible where it now lives, and
/// dropped from the ledger. Cost: one CreateFile when an entry is hidden and
/// one OpenFileById per deleted ledger entry.
/// </summary>
public sealed partial class MappedOrganizeService
{
    private int HiddenLedgerCount
    {
        get
        {
            lock (_visibilityGate)
            {
                return _hiddenLedger.Count;
            }
        }
    }

    /// <summary>Caller holds <see cref="_visibilityGate"/>.</summary>
    private void CaptureIdentity(string path)
    {
        if (ForkFileIdentityNative.TryGet(path) is { } identity)
        {
            _hiddenIdentities[MappedOrganizeMembership.NormalizePath(path)] =
                new MappedOrganizeFileIdentity(identity.VolumeSerial, identity.FileIndex);
        }
    }

    private void ReleaseIfMovedOffDesktop(string path)
    {
        try
        {
            string key = MappedOrganizeMembership.NormalizePath(path);
            lock (_visibilityGate)
            {
                if (!_hiddenLedger.Contains(key))
                {
                    return;
                }

                string? resolved = null;
                if (_hiddenIdentities.TryGetValue(key, out MappedOrganizeFileIdentity identity) &&
                    Path.GetDirectoryName(key) is { Length: > 0 } desktopDirectory)
                {
                    string? finalPath = ForkFileIdentityNative.TryResolvePath(identity.VolumeSerial, identity.FileIndex, desktopDirectory);
                    resolved = finalPath is null ? null : MappedOrganizeLeakPolicy.NormalizeFinalPath(finalPath);
                }

                switch (MappedOrganizeLeakPolicy.Classify(resolved, IsDesktopEntryPath))
                {
                    case MappedOrganizeLeakPolicy.Outcome.StillOnDesktop:
                        return;
                    case MappedOrganizeLeakPolicy.Outcome.LeftDesktop:
                        bool shown = TrySetHidden(resolved!, hidden: false);
                        App.Log($"[MappedOrganize] Hidden entry left the desktop: '{key}' -> '{resolved}', attribute cleared={shown}.");
                        break;
                    default:
                        break;
                }

                _hiddenLedger.Remove(key);
                _hiddenIdentities.Remove(key);
                PersistHiddenLedger();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Leak guard failed for '{path}': {ex.Message}");
        }
    }
}
