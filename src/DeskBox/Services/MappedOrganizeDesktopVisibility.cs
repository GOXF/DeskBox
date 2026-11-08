namespace DeskBox.Services;

/// <summary>
/// 「映射整理」 per-item desktop visibility (GOXF fork). Mapped desktop entries
/// are hidden on the desktop by setting <see cref="FileAttributes.Hidden"/>;
/// every path DeskBox hid is recorded in a ledger so exactly those (and never
/// a file that was hidden before) are restored when the entry leaves its
/// mapped widget, mapping ends, the mode switches back to moving, DeskBox exits,
/// on the next start after a crash, and by the uninstaller.
///
/// This class holds the pure planning rules (unit-tested in
/// ForkOrganizeModeTests); <see cref="MappedOrganizeService"/> applies them.
/// </summary>
public static class MappedOrganizeHiddenLedger
{
    public const string FileName = "mapped-organize.hidden";

    public sealed record Plan(
        IReadOnlyList<string> ToHide,
        IReadOnlyList<string> ToUnhide,
        IReadOnlyList<string> Forget);

    /// <summary>
    /// Plans the attribute changes that make the desktop match
    /// <paramref name="desiredHidden"/> (the members of the live mapped widgets).
    /// <paramref name="probe"/> returns the entry's attributes, or null when it
    /// does not exist / cannot be read.
    /// <list type="bullet">
    /// <item>A desired entry that is visible is hidden (and recorded), also when the ledger already lists it but something cleared the attribute.</item>
    /// <item>A desired entry that is already hidden and not in the ledger was hidden by the user: left alone and never recorded.</item>
    /// <item>A ledger entry no longer desired is made visible when still hidden, otherwise just forgotten.</item>
    /// </list>
    /// </summary>
    public static Plan Reconcile(
        IEnumerable<string> desiredHidden,
        IEnumerable<string> ledger,
        Func<string, FileAttributes?> probe)
    {
        var ledgerSet = Normalize(ledger).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredSet = Normalize(desiredHidden).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toHide = new List<string>();
        var toUnhide = new List<string>();
        var forget = new List<string>();

        foreach (string path in desiredSet)
        {
            FileAttributes? attributes = probe(path);
            if (attributes is not { } value)
            {
                if (ledgerSet.Contains(path))
                {
                    forget.Add(path);
                }

                continue;
            }

            if ((value & FileAttributes.Hidden) == 0)
            {
                toHide.Add(path);
            }
        }

        foreach (string path in ledgerSet.Where(path => !desiredSet.Contains(path)))
        {
            FileAttributes? attributes = probe(path);
            if (attributes is { } value && (value & FileAttributes.Hidden) != 0)
            {
                toUnhide.Add(path);
            }
            else
            {
                forget.Add(path);
            }
        }

        return new Plan(toHide, toUnhide, forget);
    }

    public static IReadOnlyList<string> Parse(string? content) =>
        string.IsNullOrEmpty(content)
            ? []
            : Normalize(content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();

    public static string Serialize(IEnumerable<string> paths) =>
        string.Join('\n', Normalize(paths));

    /// <summary>A recorded entry that is renamed keeps its record under the new name.</summary>
    public static bool Rename(ISet<string> ledger, string oldPath, string newPath)
    {
        string oldNormalized = MappedOrganizeMembership.NormalizePath(oldPath);
        if (!ledger.Remove(oldNormalized))
        {
            return false;
        }

        ledger.Add(MappedOrganizeMembership.NormalizePath(newPath));
        return true;
    }

    private static IEnumerable<string> Normalize(IEnumerable<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return MappedOrganizeMembership.NormalizePath(path);
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            })
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Process-wide view of the paths DeskBox hid, consulted by the widget folder
/// loader (FileService) so a mapped widget still lists its members although
/// they carry the hidden attribute. Lazily seeded from the ledger file so
/// widgets restored before the mapped-organize service starts (for example
/// after a crash left the attributes set) already see the members.
/// </summary>
public static class MappedOrganizeHiddenRegistry
{
    private static readonly object Gate = new();
    private static HashSet<string>? s_paths;

    public static bool IsHiddenByDeskBox(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = MappedOrganizeMembership.NormalizePath(path);
        }
        catch (Exception)
        {
            return false;
        }

        lock (Gate)
        {
            EnsureLoaded();
            return s_paths!.Count > 0 && s_paths.Contains(normalized);
        }
    }

    internal static void Publish(IEnumerable<string> paths)
    {
        var copy = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            s_paths = copy;
        }
    }

    internal static string LedgerPath(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? DeskBoxDataPathService.Current.DataDirectory, MappedOrganizeHiddenLedger.FileName);

    private static void EnsureLoaded()
    {
        if (s_paths is not null)
        {
            return;
        }

        s_paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = LedgerPath();
            if (File.Exists(path))
            {
                s_paths.UnionWith(MappedOrganizeHiddenLedger.Parse(File.ReadAllText(path)));
            }
        }
        catch (Exception)
        {
            // No data root (tests) or unreadable ledger: nothing is DeskBox-hidden.
        }
    }
}

/// <summary>
/// Desktop entries the user put back on the desktop from a mapped widget
/// (「放回桌面」): they stay visible and automatic sorting / 「一键整理」 never
/// claims them again. An explicit drag into a mapped widget or a confirmed
/// preview clears the mark. Pure helpers; persisted by the service.
/// </summary>
public static class MappedOrganizeKeepList
{
    public const string FileName = "mapped-organize.kept";

    public static IReadOnlyList<string> FilterUnassigned(IEnumerable<string> unassigned, IReadOnlySet<string> kept) =>
        unassigned.Where(path => !kept.Contains(MappedOrganizeMembership.NormalizePath(path))).ToList();

    /// <summary>Marks the paths as kept on the desktop. Returns true when the set changed.</summary>
    public static bool Keep(ISet<string> kept, IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            changed |= kept.Add(MappedOrganizeMembership.NormalizePath(path));
        }

        return changed;
    }

    /// <summary>Clears the mark (explicit assignment, deletion). Returns true when the set changed.</summary>
    public static bool Release(ISet<string> kept, IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            changed |= kept.Remove(MappedOrganizeMembership.NormalizePath(path));
        }

        return changed;
    }

    public static bool Rename(ISet<string> kept, string oldPath, string newPath) =>
        MappedOrganizeHiddenLedger.Rename(kept, oldPath, newPath);
}
