using System.Reflection;
using System.Text.RegularExpressions;

namespace DeskBox.Services;

/// <summary>
/// GOXF/DeskBox fork: identifies a *local development build* — a DeskBox.exe
/// compiled inside a git clone of the fork by scripts/fork-dev-update.ps1 —
/// and the git commit it was built from. Installed builds (Program Files,
/// %LOCALAPPDATA%\Programs, …) never sit inside such a clone, so every
/// local-test-channel feature stays off for them.
///
/// The commit sha / branch are embedded at build time by
/// Directory.Build.targets (AssemblyMetadata "ForkGitSha" / "ForkGitBranch",
/// Debug builds only). When they are missing the sha is read from the
/// clone's .git directory at runtime.
/// </summary>
public sealed record ForkDevBuild(string RepositoryRoot, string LocalSha, string Branch)
{
    public string ScriptPath => Path.Combine(RepositoryRoot, ForkDevBuildInfo.ScriptRelativePath);
    public string ShortSha => ForkDevBuildInfo.ShortSha(LocalSha);
}

public static partial class ForkDevBuildInfo
{
    public static readonly string ScriptRelativePath = Path.Combine("scripts", "fork-dev-update.ps1");
    public const string DevBranch = "dev";
    public const string ShaMetadataKey = "ForkGitSha";
    public const string BranchMetadataKey = "ForkGitBranch";

    private static readonly Lazy<ForkDevBuild?> s_current = new(DetectCurrent);

    /// <summary>The running process' local dev build, or null for every installed build.</summary>
    public static ForkDevBuild? Current => s_current.Value;

    public static bool IsLocalDevBuild => Current is not null;

    private static ForkDevBuild? DetectCurrent()
    {
        try
        {
            return Detect(AppContext.BaseDirectory, typeof(ForkDevBuildInfo).Assembly);
        }
        catch (Exception ex)
        {
            App.Log($"[DevUpdate] Local build detection failed: {ex.GetType().Name}");
            return null;
        }
    }

    internal static ForkDevBuild? Detect(string? executableDirectory, Assembly? assembly)
    {
        string? root = FindRepositoryRoot(executableDirectory);
        if (root is null)
        {
            return null;
        }

        string? sha = NormalizeSha(GetAssemblyMetadata(assembly, ShaMetadataKey));
        string? branch = GetAssemblyMetadata(assembly, BranchMetadataKey);
        if (sha is null)
        {
            (sha, string? headBranch) = ReadHeadFromGitDirectory(root);
            branch ??= headBranch;
        }

        return sha is null
            ? null
            : new ForkDevBuild(root, sha, string.IsNullOrWhiteSpace(branch) ? string.Empty : branch.Trim());
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> (the exe folder) to the
    /// first directory that holds BOTH a ".git" entry (directory, or file for
    /// worktrees) and scripts/fork-dev-update.ps1. Null when none exists.
    /// </summary>
    public static string? FindRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        }
        catch (Exception)
        {
            return null;
        }

        for (int depth = 0; current is not null && depth < 32; depth++, current = current.Parent)
        {
            string gitPath = Path.Combine(current.FullName, ".git");
            if ((Directory.Exists(gitPath) || File.Exists(gitPath)) &&
                File.Exists(Path.Combine(current.FullName, ScriptRelativePath)))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static string? GetAssemblyMetadata(Assembly? assembly, string key)
    {
        if (assembly is null)
        {
            return null;
        }

        foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, key, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Reads HEAD from the clone: "ref: refs/heads/x" → loose ref file or
    /// packed-refs; a detached HEAD is the sha itself. Handles a ".git" file
    /// ("gitdir: …") for worktrees, where refs live in the common dir.
    /// </summary>
    internal static (string? Sha, string? Branch) ReadHeadFromGitDirectory(string repositoryRoot)
    {
        try
        {
            string? gitDir = ResolveGitDirectory(repositoryRoot);
            if (gitDir is null)
            {
                return (null, null);
            }

            string headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath))
            {
                return (null, null);
            }

            string head = File.ReadAllText(headPath).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return (NormalizeSha(head), null);
            }

            string refName = head[4..].Trim();
            string? branch = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? refName["refs/heads/".Length..]
                : null;

            string commonDir = gitDir;
            string commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile))
            {
                string relative = File.ReadAllText(commonDirFile).Trim();
                commonDir = Path.GetFullPath(Path.Combine(gitDir, relative));
            }

            foreach (string dir in new[] { gitDir, commonDir })
            {
                string loose = Path.Combine(dir, refName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose))
                {
                    string? looseSha = NormalizeSha(File.ReadAllText(loose));
                    if (looseSha is not null)
                    {
                        return (looseSha, branch);
                    }
                }
            }

            string packed = Path.Combine(commonDir, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (string line in File.ReadLines(packed))
                {
                    int space = line.IndexOf(' ');
                    if (space > 0 && string.Equals(line[(space + 1)..].Trim(), refName, StringComparison.Ordinal))
                    {
                        return (NormalizeSha(line[..space]), branch);
                    }
                }
            }

            return (null, branch);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string? ResolveGitDirectory(string repositoryRoot)
    {
        string gitPath = Path.Combine(repositoryRoot, ".git");
        if (Directory.Exists(gitPath))
        {
            return gitPath;
        }

        if (File.Exists(gitPath))
        {
            string content = File.ReadAllText(gitPath).Trim();
            if (content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                string target = content["gitdir:".Length..].Trim();
                string full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(repositoryRoot, target));
                return Directory.Exists(full) ? full : null;
            }
        }

        return null;
    }

    [GeneratedRegex("^[0-9a-f]{7,64}$")]
    private static partial Regex ShaPattern();

    /// <summary>Lower-cased hex sha (7–64 chars), or null when the text is not one.</summary>
    public static string? NormalizeSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim().ToLowerInvariant();
        return ShaPattern().IsMatch(trimmed) ? trimmed : null;
    }

    public static string ShortSha(string? sha)
    {
        string? normalized = NormalizeSha(sha);
        if (normalized is null)
        {
            return string.Empty;
        }

        return normalized.Length > 7 ? normalized[..7] : normalized;
    }

    /// <summary>
    /// True when the remote tip differs from the local build's commit. A
    /// short local sha (prefix) that matches the remote counts as the same
    /// commit. Unknown / malformed shas never report an update.
    /// </summary>
    public static bool IsRemoteNewer(string? localSha, string? remoteSha)
    {
        string? local = NormalizeSha(localSha);
        string? remote = NormalizeSha(remoteSha);
        if (local is null || remote is null)
        {
            return false;
        }

        return !(remote.StartsWith(local, StringComparison.Ordinal) ||
                 local.StartsWith(remote, StringComparison.Ordinal));
    }
}
