using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class ApiKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiteLLM.VisualStudio.v1");
    public string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    public string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
    }
}
public sealed class SettingsStore
{
    private readonly string _path;
    public SettingsStore(string? path = null) => _path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteLLM.VisualStudio", "settings.json");
    public string Path => _path;
    public UserSettings Load()
    {
        if (!File.Exists(_path)) return new UserSettings();
        try
        {
            var settings = JsonConvert.DeserializeObject<UserSettings>(File.ReadAllText(_path)) ?? new UserSettings();
            if (settings.McpServers == null) settings.McpServers = new List<McpServerConfiguration>();
            foreach (var server in settings.McpServers.Where(server => server != null)) McpSecrets.FromStorage(server);
            return settings;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { return new UserSettings(); }
    }
    public void Save(UserSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        // The server list is replaced with protected copies before serialization. Protecting in place would put the
        // DPAPI blobs into the live settings, and the next request would send them as the environment value. The
        // sensitive values never reach the file; the in-memory object stays plaintext.
        var document = JObject.FromObject(settings);
        document["McpServers"] = JToken.FromObject(
            (settings.McpServers ?? new List<McpServerConfiguration>()).Select(McpSecrets.ForStorage));
        File.WriteAllText(temporary, document.ToString(Formatting.Indented), Encoding.UTF8);
        if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
    }
}
