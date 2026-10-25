using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteLLM.Core;

public sealed class SkillStorageResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    /// <summary>A non-fatal note, such as a backup folder that could not be removed after a replace.</summary>
    public string Warning { get; set; } = "";
    public string Path { get; set; } = "";
    public int FilesCopied { get; set; }
    public long BytesCopied { get; set; }
}

/// <summary>The only code allowed to write inside a skills directory.
///
/// Every operation proves three things before it touches the disk: the skill's name is valid, the target is a
/// direct child of a configured root, and — for a delete or an import replace — the user confirmed. A name cannot
/// contain a separator, so it cannot be a traversal; the direct-child check is the second lock on the same door.
/// Nothing here follows a link, and nothing outside a skill's own directory is ever written.</summary>
public static class SkillStorage
{
    public const int MaxImportFiles = 300;
    public const long MaxImportBytes = 10 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>The directory a new skill in this scope belongs under. For a project skill this is the configured
    /// project directory, not the compatible <c>.github</c>/<c>.claude</c> locations: a new skill should go where
    /// the extension looks first. Null with an error means there is no solution open.</summary>
    public static string DestinationDirectory(SkillDiscoveryOptions options, SkillScope scope, out string error)
    {
        error = "";
        if (scope == SkillScope.Personal)
        {
            var personal = string.IsNullOrWhiteSpace(options.PersonalDirectory)
                ? SkillLocations.DefaultPersonalDirectory
                : options.PersonalDirectory.Trim();
            try { return Path.GetFullPath(personal); }
            catch (Exception ex)
            {
                error = "The personal skills directory is not a valid path: " + ex.Message;
                return "";
            }
        }

        var project = SkillLocations.ResolveProjectDirectory(options.SolutionRoot, options.ProjectDirectory);
        if (project.Length == 0)
        {
            error = "Project skills need an open solution or folder. Open one, or save the skill as a personal skill.";
            return "";
        }
        return project;
    }

    public static SkillStorageResult Create(SkillDiscoveryOptions options, SkillScope scope, string name,
        string description, string body, string license = "", string compatibility = "",
        IReadOnlyDictionary<string, string>? metadata = null, string allowedTools = "")
    {
        var result = new SkillStorageResult();
        var nameError = SkillFrontmatter.ValidateName(name);
        if (nameError != null) { result.Error = nameError; return result; }
        var descriptionError = SkillFrontmatter.ValidateDescription(description);
        if (descriptionError != null) { result.Error = descriptionError; return result; }

        name = name.Trim();
        var content = SkillFrontmatter.Build(name, description, body ?? "", license, compatibility, metadata, allowedTools);
        var contentError = ValidateContent(content, name, out _);
        if (contentError != null) { result.Error = contentError; return result; }

        var root = DestinationDirectory(options, scope, out var rootError);
        if (root.Length == 0) { result.Error = rootError; return result; }
        var destination = Path.Combine(root, name);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            result.Error = "A skill named '" + name + "' already exists at " + SkillLocations.DisplayPath(destination) +
                           ". Nothing was changed; choose another name or edit the existing skill.";
            return result;
        }

        var created = false;
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(destination);
            created = true;
            var skillPath = Path.Combine(destination, "SKILL.md");
            // CreateNew rather than Create: the existence check above is not atomic, and replacing a file another
            // process wrote in between is exactly the silent overwrite this feature is not allowed to make.
            using (var stream = new FileStream(skillPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8))
                writer.Write(content);
            result.Ok = true;
            result.Path = destination;
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            if (created)
            {
                try { if (Directory.Exists(destination) && !Directory.EnumerateFileSystemEntries(destination).Any()) Directory.Delete(destination); }
                catch (Exception) { }
            }
            result.Error = "The skill could not be created: " + ex.Message;
            return result;
        }
    }

    /// <summary>Writes a complete <c>SKILL.md</c>. Only that file is touched: an edit must not remove the
    /// references, scripts or assets the user added beside it. <paramref name="confirmed"/> is the caller's
    /// evidence that the user pressed Save on the reviewed content.</summary>
    public static SkillStorageResult Save(SkillDiscoveryOptions options, SkillInfo skill, string content, bool confirmed)
    {
        var result = new SkillStorageResult();
        if (!confirmed) { result.Error = "Saving a skill needs confirmation."; return result; }
        if (skill == null) { result.Error = "There is no skill to save."; return result; }
        if (!SkillRegistry.IsDirectChildOfAnyRoot(options, skill.Directory))
        {
            result.Error = "The skill is not inside a configured skills directory, so it was not written.";
            return result;
        }

        var folder = Path.GetFileName(skill.Directory);
        var contentError = ValidateContent(content, folder, out var parsed);
        if (contentError != null) { result.Error = contentError; return result; }
        if (parsed == null) { result.Error = "The skill could not be validated."; return result; }

        var path = Path.Combine(skill.Directory, "SKILL.md");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, Utf8);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            result.Ok = true;
            result.Path = path;
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            TryDeleteFile(temporary);
            result.Error = "The skill could not be saved: " + ex.Message;
            return result;
        }
    }

    public static SkillStorageResult Read(SkillDiscoveryOptions options, SkillInfo skill, out string content)
    {
        content = "";
        var result = new SkillStorageResult();
        if (skill == null) { result.Error = "There is no skill to read."; return result; }
        if (!SkillRegistry.IsDirectChildOfAnyRoot(options, skill.Directory))
        {
            result.Error = "The skill is not inside a configured skills directory, so it was not read.";
            return result;
        }
        var path = Path.Combine(skill.Directory, "SKILL.md");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { result.Error = "SKILL.md no longer exists at " + SkillLocations.DisplayPath(skill.Directory) + "."; return result; }
            if (info.Length > SkillFrontmatter.MaxFileBytes)
            {
                result.Error = "SKILL.md is larger than " + SkillFrontmatter.MaxFileBytes + " bytes, so it was not opened.";
                return result;
            }
            content = File.ReadAllText(path);
            result.Ok = true;
            result.Path = path;
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            result.Error = "SKILL.md could not be read: " + ex.Message;
            return result;
        }
    }

    /// <summary>Deletes the whole skill directory, after confirmation, and only when it is a direct child of a
    /// configured root. Read-only files inside are cleared first: the user confirmed the delete, and stopping
    /// halfway would leave a folder that is neither the old skill nor gone.</summary>
    public static SkillStorageResult Delete(SkillDiscoveryOptions options, SkillInfo skill, bool confirmed)
    {
        var result = new SkillStorageResult();
        if (!confirmed) { result.Error = "Deleting a skill needs confirmation."; return result; }
        if (skill == null) { result.Error = "There is no skill to delete."; return result; }
        if (!SkillRegistry.IsDirectChildOfAnyRoot(options, skill.Directory))
        {
            result.Error = "The skill is not inside a configured skills directory, so it was not deleted.";
            return result;
        }
        if (!Directory.Exists(skill.Directory))
        {
            result.Ok = true;
            result.Path = skill.Directory;
            return result;
        }
        if (!File.Exists(Path.Combine(skill.Directory, "SKILL.md")))
        {
            result.Error = "The folder does not contain SKILL.md, so it is not treated as a skill and was not deleted.";
            return result;
        }
        if (IsReparsePoint(skill.Directory))
        {
            result.Error = "The skill folder is a link. Links are not followed or deleted by the extension.";
            return result;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(skill.Directory, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(skill.Directory, true);
            result.Ok = true;
            result.Path = skill.Directory;
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            result.Error = "The skill could not be deleted: " + ex.Message;
            return result;
        }
    }

    /// <summary>Copies a skill directory into a scope. The copy is built in a temporary sibling and moved into
    /// place only when every file has been copied and the copied header validates, so a failure leaves whatever
    /// was at the destination untouched. An existing destination is replaced only when
    /// <paramref name="overwrite"/> is true, which the UI sets from an explicit confirmation.</summary>
    public static SkillStorageResult Import(SkillDiscoveryOptions options, SkillScope scope, string sourceDirectory,
        bool overwrite = false)
    {
        var result = new SkillStorageResult();
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            result.Error = "The folder to import does not exist.";
            return result;
        }
        if (IsReparsePoint(sourceDirectory))
        {
            result.Error = "The folder to import is a link. Links are not followed.";
            return result;
        }

        var sourcePath = Path.GetFullPath(sourceDirectory);
        var sourceSkill = Path.Combine(sourcePath, "SKILL.md");
        if (!File.Exists(sourceSkill))
        {
            result.Error = "The folder does not contain SKILL.md, so it is not a skill.";
            return result;
        }

        SkillFrontmatter parsed;
        try
        {
            var info = new FileInfo(sourceSkill);
            if (info.Length > SkillFrontmatter.MaxFileBytes)
            {
                result.Error = "SKILL.md is larger than " + SkillFrontmatter.MaxFileBytes + " bytes.";
                return result;
            }
            parsed = SkillFrontmatter.Parse(File.ReadAllText(sourceSkill));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            result.Error = "SKILL.md could not be read: " + ex.Message;
            return result;
        }

        if (!parsed.IsValid) { result.Error = "The skill is not valid: " + parsed.Error; return result; }
        var name = parsed.Name.Trim();
        var nameError = SkillFrontmatter.ValidateName(name);
        if (nameError != null) { result.Error = nameError; return result; }

        var root = DestinationDirectory(options, scope, out var rootError);
        if (root.Length == 0) { result.Error = rootError; return result; }
        var destination = Path.Combine(root, name);
        if (string.Equals(SkillRegistry.PathKey(sourcePath), SkillRegistry.PathKey(destination), StringComparison.OrdinalIgnoreCase))
        {
            result.Error = "The folder is already the destination for this skill. Nothing was copied.";
            return result;
        }
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            if (!overwrite)
            {
                result.Error = "A skill named '" + name + "' already exists at " + SkillLocations.DisplayPath(destination) +
                               ". Nothing was changed. Confirm Replace to overwrite it.";
                return result;
            }
        }

        var token = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(root, "." + name + ".import-" + token);
        var backup = Path.Combine(root, "." + name + ".backup-" + token);
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(temporary);
            var copied = CopyTree(sourcePath, temporary, result);
            if (!copied) { TryDeleteDirectory(temporary); return result; }

            // The copy is what will be used; validate it rather than trusting the source we read a moment ago.
            var copiedHeader = SkillFrontmatter.Parse(File.ReadAllText(Path.Combine(temporary, "SKILL.md")));
            if (!copiedHeader.IsValid)
            {
                result.Error = "The copied skill is not valid: " + copiedHeader.Error;
                TryDeleteDirectory(temporary);
                return result;
            }

            var existed = Directory.Exists(destination);
            if (existed) Directory.Move(destination, backup);
            try { Directory.Move(temporary, destination); }
            catch (Exception)
            {
                // Put the original back before reporting: a replace that failed must not also lose what it replaced.
                if (existed && Directory.Exists(backup))
                {
                    try { if (!Directory.Exists(destination)) Directory.Move(backup, destination); }
                    catch (Exception) { }
                }
                throw;
            }
            if (existed) TryDeleteDirectory(backup);

            result.Ok = true;
            result.Path = destination;
            if (Directory.Exists(backup)) result.Warning = "The previous copy is still at " + SkillLocations.DisplayPath(backup) + ".";
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            TryDeleteDirectory(temporary);
            result.Error = "The skill could not be imported: " + ex.Message;
            return result;
        }
    }

    private static bool CopyTree(string source, string destination, SkillStorageResult result)
    {
        var pending = new Stack<KeyValuePair<string, string>>();
        pending.Push(new KeyValuePair<string, string>(source, destination));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            List<string> entries;
            try { entries = Directory.GetFileSystemEntries(current.Key).ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                result.Error = "A folder could not be read while importing: " + ex.Message;
                return false;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, ".svn", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsReparsePoint(entry)) continue;

                var target = Path.Combine(current.Value, name);
                if (!PathGuard.IsUnder(destination, target))
                {
                    result.Error = "An entry would have escaped the skill directory, so the import was stopped.";
                    return false;
                }

                if (Directory.Exists(entry))
                {
                    try { Directory.CreateDirectory(target); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        result.Error = "A folder could not be created while importing: " + ex.Message;
                        return false;
                    }
                    pending.Push(new KeyValuePair<string, string>(entry, target));
                    continue;
                }

                if (result.FilesCopied >= MaxImportFiles)
                {
                    result.Error = "The folder has more than " + MaxImportFiles + " files, so the import was stopped.";
                    return false;
                }
                try
                {
                    var length = new FileInfo(entry).Length;
                    if (result.BytesCopied + length > MaxImportBytes)
                    {
                        result.Error = "The folder is larger than " + (MaxImportBytes / (1024 * 1024)) + " MB, so the import was stopped.";
                        return false;
                    }
                    File.Copy(entry, target, false);
                    result.FilesCopied++;
                    result.BytesCopied += length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Error = "A file could not be copied while importing: " + ex.Message;
                    return false;
                }
            }
        }
        return true;
    }

    private static string? ValidateContent(string content, string expectedFolder, out SkillFrontmatter? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(content)) return "SKILL.md is empty.";
        if (Encoding.UTF8.GetByteCount(content) > SkillFrontmatter.MaxFileBytes)
            return "SKILL.md is larger than " + SkillFrontmatter.MaxFileBytes + " bytes.";
        parsed = SkillFrontmatter.Parse(content);
        if (!parsed.IsValid) return parsed.Error;
        if (!string.Equals(parsed.Name.Trim(), expectedFolder, StringComparison.Ordinal))
            return "The name '" + parsed.Name.Trim() + "' must match the folder '" + expectedFolder +
                   "'. Create a new skill to use a different name.";
        return null;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return true; }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception) { }
    }
}
