using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

/// <summary>One directory the registry scans. Only its immediate children are considered: a folder is a skill
/// when it contains <c>SKILL.md</c>, and the scanner never walks a drive.</summary>
public sealed class SkillRoot
{
    public string Path { get; set; } = "";
    public SkillScope Scope { get; set; }
    public string Label { get; set; } = "";
    public int Precedence { get; set; }
}

/// <summary>Where to look, for whom. Built from <see cref="UserSettings"/> once per turn or per refresh, so the
/// registry itself carries no settings dependency and the tests can point it at temporary directories.</summary>
public sealed class SkillDiscoveryOptions
{
    public string? SolutionRoot { get; set; }
    public string PersonalDirectory { get; set; } = SkillLocations.DefaultPersonalDirectory;
    /// <summary>Absolute, or relative to the open solution.</summary>
    public string ProjectDirectory { get; set; } = SkillLocations.DefaultProjectDirectory;
    /// <summary>Also look in <c>.github/skills</c> and <c>.claude/skills</c> under the solution.</summary>
    public bool IncludeCompatibleLocations { get; set; } = true;
    /// <summary>Full paths of definitions the user turned off. Keyed by path, not name, so disabling one copy of
    /// a duplicate does not disable the other.</summary>
    public IReadOnlyCollection<string> DisabledPaths { get; set; } = Array.Empty<string>();

    public static SkillDiscoveryOptions FromSettings(UserSettings settings, string? solutionRoot)
    {
        var value = settings ?? new UserSettings();
        return new SkillDiscoveryOptions
        {
            SolutionRoot = solutionRoot,
            PersonalDirectory = string.IsNullOrWhiteSpace(value.SkillsPersonalDirectory)
                ? SkillLocations.DefaultPersonalDirectory
                : value.SkillsPersonalDirectory,
            ProjectDirectory = string.IsNullOrWhiteSpace(value.SkillsProjectDirectory)
                ? SkillLocations.DefaultProjectDirectory
                : value.SkillsProjectDirectory,
            IncludeCompatibleLocations = value.SkillsIncludeCompatibleLocations,
            DisabledPaths = value.DisabledSkillPaths ?? new List<string>()
        };
    }
}

public static class SkillLocations
{
    public const string DefaultProjectDirectory = ".agents/skills";
    public const string GitHubDirectory = ".github/skills";
    public const string ClaudeDirectory = ".claude/skills";

    /// <summary>Under the profile, not under LocalApplicationData: a skill is the user's document, and one that
    /// follows them between machines through a synced profile or a dotfiles repository is the point.</summary>
    public static string DefaultPersonalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llm123", "skills");

    public static string ResolveProjectDirectory(string? solutionRoot, string configured)
    {
        var directory = string.IsNullOrWhiteSpace(configured) ? DefaultProjectDirectory : configured.Trim();
        if (string.IsNullOrWhiteSpace(solutionRoot)) return "";
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(solutionRoot!, directory));
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>The roots in precedence order, highest first. The configured project directory comes first, then
    /// the compatible locations, then the personal directory. Paths are de-duplicated so a user who set the
    /// project directory to <c>.claude/skills</c> does not see every skill twice.</summary>
    public static List<SkillRoot> Roots(SkillDiscoveryOptions options)
    {
        var roots = new List<SkillRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, SkillScope scope, string label)
        {
            var key = SkillRegistry.PathKey(path);
            if (key.Length == 0 || !seen.Add(key)) return;
            roots.Add(new SkillRoot { Path = path, Scope = scope, Label = label, Precedence = roots.Count });
        }

        var project = ResolveProjectDirectory(options.SolutionRoot, options.ProjectDirectory);
        if (project.Length > 0)
            Add(project, SkillScope.Project, "Project · " + ProjectLabel(options.SolutionRoot, project, options.ProjectDirectory));

        if (options.IncludeCompatibleLocations && !string.IsNullOrWhiteSpace(options.SolutionRoot))
        {
            foreach (var (relative, label) in new[]
                     {
                         (GitHubDirectory, "Project · " + GitHubDirectory),
                         (ClaudeDirectory, "Project · " + ClaudeDirectory)
                     })
            {
                var path = ResolveProjectDirectory(options.SolutionRoot, relative);
                if (path.Length > 0) Add(path, SkillScope.Project, label);
            }
        }

        var personal = string.IsNullOrWhiteSpace(options.PersonalDirectory)
            ? DefaultPersonalDirectory
            : options.PersonalDirectory.Trim();
        Add(personal, SkillScope.Personal, "Personal · " + DisplayPath(personal));
        return roots;
    }

    private static string ProjectLabel(string? solutionRoot, string full, string configured)
    {
        var display = string.IsNullOrWhiteSpace(configured) ? DefaultProjectDirectory : configured.Trim();
        return DisplayPath(display);
    }

    public static string DisplayPath(string path) => (path ?? "").Replace('\\', '/');
}

public sealed class SkillConflict
{
    public string Name { get; set; } = "";
    public SkillInfo Winner { get; set; } = new SkillInfo();
    public List<SkillInfo> Others { get; set; } = new List<SkillInfo>();
    public string Summary =>
        "Skill conflict · '" + Name + "' is defined " + (Others.Count + 1) + " times; " +
        SkillLocations.DisplayPath(Winner.Directory) + " is used";
}

/// <summary>One scan's result. <see cref="Effective"/> is the deterministic answer to "which skills does this
/// turn have"; <see cref="All"/> keeps every definition so the Dashboard can show what lost and why.</summary>
public sealed class SkillRegistrySnapshot
{
    public static SkillRegistrySnapshot Empty { get; } = new SkillRegistrySnapshot
    {
        All = Array.Empty<SkillInfo>(),
        Effective = Array.Empty<SkillInfo>(),
        Conflicts = Array.Empty<SkillConflict>(),
        Problems = Array.Empty<string>(),
        ScannedUtc = DateTime.MinValue
    };

    public IReadOnlyList<SkillInfo> All { get; set; } = Array.Empty<SkillInfo>();
    public IReadOnlyList<SkillInfo> Effective { get; set; } = Array.Empty<SkillInfo>();
    public IReadOnlyList<SkillConflict> Conflicts { get; set; } = Array.Empty<SkillConflict>();
    /// <summary>Roots that could not be read. A skill's own problem is on <see cref="SkillInfo.Error"/> instead.</summary>
    public IReadOnlyList<string> Problems { get; set; } = Array.Empty<string>();
    public DateTime ScannedUtc { get; set; }

    public SkillInfo? Find(string? name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return null;
        return Effective.FirstOrDefault(skill => string.Equals(skill.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Exact matches first, then names that start with the token. Both are ordered deterministically.</summary>
    public IReadOnlyList<SkillInfo> Match(string? token)
    {
        var wanted = (token ?? "").Trim();
        if (wanted.Length == 0) return Array.Empty<SkillInfo>();
        var exact = Effective.Where(skill => string.Equals(skill.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return Effective
            .Where(skill => skill.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Discovers and validates skills. The registry knows nothing about a harness, a model or a tool: it
/// answers which definitions exist and which one a name means, and the backend decides how to use them.
///
/// The cache is keyed by a signature built from the candidate paths and each <c>SKILL.md</c>'s length and
/// last-write time. That is enough to notice every add, remove, rename, edit and enable/disable, without reading
/// a file whose header has not changed.</summary>
public sealed class SkillRegistry
{
    public const int MaxResourcesPerSkill = 200;
    public const int MaxResourceDepth = 6;

    private readonly object _gate = new object();
    private SkillRegistrySnapshot _snapshot = SkillRegistrySnapshot.Empty;
    private string? _signature;

    public SkillRegistrySnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public SkillRegistrySnapshot Refresh(SkillDiscoveryOptions options, CancellationToken token = default)
    {
        options ??= new SkillDiscoveryOptions();
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var roots = SkillLocations.Roots(options);
            var disabled = DisabledSet(options.DisabledPaths);
            var signature = Signature(roots, disabled);
            // A signature that could not be computed means the directory tree is not readable in a way that can
            // be compared; scanning again is the honest answer, and an unchanged cache would hide a change.
            if (signature != null && _signature != null && string.Equals(signature, _signature, StringComparison.Ordinal))
                return _snapshot;

            var all = new List<SkillInfo>();
            var problems = new List<string>();
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root.Path)) continue;
                if (!SafeDirectoryExists(root.Path))
                {
                    if (Directory.Exists(root.Path))
                        problems.Add(root.Label + ": the directory could not be read.");
                    else if (File.Exists(root.Path))
                        problems.Add(root.Label + ": the configured path is a file, not a directory.");
                    continue;
                }

                List<string> children;
                try { children = Directory.GetDirectories(root.Path).ToList(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    problems.Add(root.Label + ": " + SafeMessage(ex));
                    continue;
                }

                foreach (var directory in children.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsReparsePoint(directory))
                    {
                        problems.Add(root.Label + ": skipped '" + Path.GetFileName(directory) + "' because it is a link.");
                        continue;
                    }
                    var info = Load(directory, root, disabled, token);
                    if (info != null) all.Add(info);
                }
            }

            var effective = Resolve(all, out var conflicts);
            var snapshot = new SkillRegistrySnapshot
            {
                All = all,
                Effective = effective,
                Conflicts = conflicts,
                Problems = problems,
                ScannedUtc = DateTime.UtcNow
            };
            _snapshot = snapshot;
            _signature = signature;
            return snapshot;
        }
    }

    public Task<SkillRegistrySnapshot> RefreshAsync(SkillDiscoveryOptions options, CancellationToken token = default) =>
        Task.Run(() => Refresh(options, token), token);

    /// <summary>Full path with a trailing separator removed, used as the identity for enabled state and for
    /// comparing the same directory written two ways. Never throws: a malformed configured path is compared as
    /// the text it is rather than bringing a scan down.</summary>
    public static string PathKey(string? path)
    {
        var text = (path ?? "").Trim();
        if (text.Length == 0) return "";
        try { text = Path.GetFullPath(text); }
        catch (Exception) { return text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        return text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>The full paths of the configured roots, for the storage writer to prove it is not being asked to
    /// write somewhere else.</summary>
    public static IReadOnlyList<string> RootPaths(SkillDiscoveryOptions options) =>
        SkillLocations.Roots(options).Select(root => PathKey(root.Path)).Where(path => path.Length > 0).ToList();

    public static bool IsDirectChildOfAnyRoot(SkillDiscoveryOptions options, string directory)
    {
        var key = PathKey(directory);
        if (key.Length == 0) return false;
        foreach (var root in RootPaths(options))
        {
            if (string.Equals(PathKey(Path.GetDirectoryName(key) ?? ""), root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static HashSet<string> DisabledSet(IReadOnlyCollection<string>? paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths ?? Array.Empty<string>())
        {
            var key = PathKey(path);
            if (key.Length > 0) set.Add(key);
        }
        return set;
    }

    private static string? Signature(IReadOnlyList<SkillRoot> roots, HashSet<string> disabled)
    {
        try
        {
            var text = new StringBuilder();
            foreach (var root in roots)
            {
                text.Append("root|").Append(PathKey(root.Path)).Append('|').Append(root.Precedence).Append('\n');
                if (!Directory.Exists(root.Path)) continue;
                foreach (var directory in Directory.GetDirectories(root.Path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var skill = Path.Combine(directory, "SKILL.md");
                    var info = new FileInfo(skill);
                    text.Append(PathKey(directory)).Append('|');
                    if (info.Exists) text.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
                    else text.Append("no-skill-md");
                    text.Append('\n');
                }
            }
            text.Append("disabled|");
            foreach (var path in disabled.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) text.Append(path).Append(';');
            return text.ToString();
        }
        catch (Exception)
        {
            // Any failure here is a reason to rescan, not a reason to reuse what is cached.
            return null;
        }
    }

    private static SkillInfo? Load(string directory, SkillRoot root, HashSet<string> disabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var skillPath = Path.Combine(directory, "SKILL.md");
        var folder = Path.GetFileName(directory);
        if (!File.Exists(skillPath)) return null;

        var info = new SkillInfo
        {
            Name = folder,
            Scope = root.Scope,
            Source = root.Label,
            Directory = PathKey(directory),
            SkillFilePath = skillPath,
            Enabled = !disabled.Contains(PathKey(directory))
        };

        try
        {
            var file = new FileInfo(skillPath);
            info.ContentLength = file.Length;
            info.LastWriteUtc = file.LastWriteTimeUtc;
            if (file.Length > SkillFrontmatter.MaxFileBytes)
            {
                info.Error = "SKILL.md is " + file.Length + " bytes; the limit is " + SkillFrontmatter.MaxFileBytes + ".";
                return info;
            }

            var parsed = SkillFrontmatter.Parse(File.ReadAllText(skillPath));
            info.Name = string.IsNullOrWhiteSpace(parsed.Name) ? folder : parsed.Name.Trim();
            info.Description = parsed.Description;
            info.License = parsed.License;
            info.Compatibility = parsed.Compatibility;
            info.AllowedTools = parsed.AllowedTools;
            info.Metadata = parsed.Metadata;
            if (!parsed.IsValid)
            {
                info.Error = parsed.Error;
                return info;
            }

            // The folder name is the durable identity. A file whose name field disagrees would make the skill
            // impossible to address by the name the user sees on the folder, so it is refused rather than
            // guessed at.
            if (!string.Equals(parsed.Name.Trim(), folder, StringComparison.Ordinal))
            {
                info.Error = "The name '" + parsed.Name.Trim() + "' does not match the folder '" + folder +
                             "'. Rename the folder or the name field so they agree.";
                return info;
            }

            info.Resources = EnumerateResources(info, token);
            return info;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            info.Error = "SKILL.md could not be read: " + SafeMessage(ex);
            return info;
        }
    }

    private static List<SkillResourceInfo> EnumerateResources(SkillInfo skill, CancellationToken token)
    {
        var result = new List<SkillResourceInfo>();
        var pending = new Stack<KeyValuePair<string, int>>();
        pending.Push(new KeyValuePair<string, int>(skill.Directory, 0));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            List<string> entries;
            try { entries = Directory.GetFileSystemEntries(current.Key).ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                skill.Warnings.Add("Some resources could not be listed: " + SafeMessage(ex));
                continue;
            }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (IsReparsePoint(entry)) continue;
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(PathKey(Path.GetDirectoryName(entry) ?? ""), skill.Directory, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Directory.Exists(entry))
                {
                    if (current.Value + 1 < MaxResourceDepth)
                        pending.Push(new KeyValuePair<string, int>(entry, current.Value + 1));
                    else skill.Warnings.Add("Resources nested deeper than " + MaxResourceDepth + " levels are not listed.");
                    continue;
                }

                if (result.Count >= MaxResourcesPerSkill)
                {
                    skill.Warnings.Add("Only the first " + MaxResourcesPerSkill + " resource files are listed.");
                    return result;
                }

                // Shown to the model and drawn in the Dashboard, so it uses the separator both read as "/". The
                // read path accepts either; this only keeps the two from looking like different files.
                var relative = SkillLocations.DisplayPath(
                    entry.Substring(skill.Directory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var kind = relative.Split('/', '\\')[0].ToLowerInvariant();
                if (kind != "references" && kind != "scripts" && kind != "assets") kind = "file";
                long size = 0;
                try { size = new FileInfo(entry).Length; } catch (Exception) { }
                result.Add(new SkillResourceInfo { RelativePath = relative, Kind = kind, SizeBytes = size });
            }
        }
        result.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Picks one winner per name and records every loser. The input is already in precedence order, and
    /// only a valid, enabled definition can win. The loser is marked rather than dropped so the Dashboard can
    /// show the path the user needs to change.</summary>
    private static List<SkillInfo> Resolve(List<SkillInfo> all, out List<SkillConflict> conflicts)
    {
        var effective = new List<SkillInfo>();
        var winners = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in all)
        {
            skill.Shadowed = false;
            skill.ShadowedBy = "";
            if (skill.Name.Length == 0) continue;
            if (skill.IsValid && skill.Enabled)
            {
                if (!winners.TryGetValue(skill.Name, out var winner))
                {
                    winners[skill.Name] = skill;
                    effective.Add(skill);
                }
                else
                {
                    skill.Shadowed = true;
                    skill.ShadowedBy = winner.Directory;
                }
            }
        }

        conflicts = new List<SkillConflict>();
        foreach (var group in all
                     .Where(skill => skill.IsValid && skill.Name.Length > 0)
                     .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2 || !winners.TryGetValue(group.Key, out var winner)) continue;
            conflicts.Add(new SkillConflict
            {
                Name = group.Key,
                Winner = winner,
                Others = group.Where(skill => !ReferenceEquals(skill, winner)).ToList()
            });
        }

        effective.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return effective;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return true; }
    }

    private static string SafeMessage(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}
