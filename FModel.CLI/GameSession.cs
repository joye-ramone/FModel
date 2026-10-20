using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures.BC;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FModel.CLI;

/// <summary>
/// the provider setup FModel does in CUE4ParseViewModel (constructor, Initialize, LoadVfs) without the UI
/// </summary>
public sealed class GameSession : IDisposable
{
    private const string ZERO_64_CHAR = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly FGuid ZERO_GUID = new(0U);

    public DefaultFileProvider Provider { get; }
    public string GameDirectory { get; }
    public string? MappingsPath { get; private set; }

    private GameSession(DefaultFileProvider provider, string gameDirectory)
    {
        Provider = provider;
        GameDirectory = gameDirectory;
    }

    public static async Task<GameSession> OpenAsync(CliOptions o)
    {
        var gameDirectory = Path.GetFullPath(o.Require("game"));
        if (!Directory.Exists(gameDirectory)) throw new CliException($"--game: folder not found: {gameDirectory}");

        var game = o.GetEnum("ue", EGame.GAME_UE5_LATEST);
        if (o.Get("ue") == null) Log.Warning("No --ue given, using {Game}; games with their own id (GAME_Stalker2, ...) need it", game);
        var platform = o.GetEnum("platform", ETexturePlatform.DesktopMobile);

        await InitNativesAsync(o).ConfigureAwait(false);

        var versions = new VersionContainer(game, platform);
        var provider = new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
        provider.ReadScriptData = false;
        provider.ReadShaderMaps = false;
        provider.ReadNaniteData = true;
        PropertyUtil.SearchPropertyInTemplate = true;

        var session = new GameSession(provider, gameDirectory);
        session.LoadMappings(o.Get("usmap"));

        provider.Initialize();
        Log.Information("{Game} ({Platform}) | Archives: x{Archives} | AES: x{Keys} | Loose Files: x{Files}", provider.Versions.Game, provider.Versions.Platform, provider.UnloadedVfs.Count, provider.RequiredKeys.Count, provider.Files.Count);

        provider.SubmitKeys(ReadKeys(o));
        provider.PostMount();
        provider.LoadVirtualPaths();

        var aesMax = provider.RequiredKeys.Count + provider.Keys.Count;
        var archiveMax = provider.UnloadedVfs.Count + provider.MountedVfs.Count;
        Log.Information("Project: {Project} | Mounted: {Mounted}/{ArchiveMax} | AES: {Keys}/{AesMax} | Files: x{Files}", provider.ProjectName, provider.MountedVfs.Count, archiveMax, provider.Keys.Count, aesMax, provider.Files.Count);

        foreach (var vfs in provider.MountedVfs.Where(x => x.Path.Contains("~mods", StringComparison.OrdinalIgnoreCase)))
            Log.Warning("Mod archive mounted over the game data: {Path}", vfs.Path);
        foreach (var guid in provider.RequiredKeys)
            Log.Warning("Missing AES key for archives with key GUID {Guid}", guid);
        if (provider.GlobalData == null && provider.MountedVfs.Any(x => x.Name.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)))
            Log.Warning("global.utoc / global.ucas not found: IoStore packages will not load (json, export, list --exports)");

        if (provider.MountedVfs.Count == 0)
            throw new CliException("no archive mounted (wrong --game folder, missing or wrong --aes key?)", ExitCodes.Mount);
        if (o.Has("strict") && provider.RequiredKeys.Count > 0)
            throw new CliException($"{provider.RequiredKeys.Count} AES key(s) missing (--strict)", ExitCodes.Mount);

        return session;
    }

    private void LoadMappings(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new CliException($"--usmap: file not found: {path}");

        ITypeMappingsProvider mappings = path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) ? new JmapTypeMappingsProvider(path) : new FileUsmapTypeMappingsProvider(path);
        Provider.MappingsContainer = mappings;
        MappingsPath = path;
        Log.Information("Mappings: {Path}", path);
    }

    /// <summary>
    /// --aes 0x... (main key), --aes GUID:0x... (dynamic key), --aes-file (one key per line, same forms, or FModel's / uedb's json with mainKey + dynamicKeys)
    /// </summary>
    private static List<KeyValuePair<FGuid, FAesKey>> ReadKeys(CliOptions o)
    {
        var keys = new Dictionary<FGuid, FAesKey>();
        var entries = new List<string>(o.GetAll("aes"));

        var file = o.Get("aes-file");
        if (file != null)
        {
            if (!File.Exists(file)) throw new CliException($"--aes-file: file not found: {file}");
            var text = File.ReadAllText(file).Trim();
            if (text.StartsWith('{')) entries.AddRange(ReadKeysJson(text));
            else entries.AddRange(text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')));
        }

        foreach (var entry in entries)
        {
            var sep = entry.IndexOfAny([':', '=', ' ', '\t']);
            var guid = sep > 0 ? new FGuid(entry[..sep].Trim().Replace("-", "")) : ZERO_GUID;
            var key = NormalizeKey(sep > 0 ? entry[(sep + 1)..] : entry);
            keys[guid] = new FAesKey(key);
        }

        // unencrypted archives mount with the zero key, like FModel does when no main key is set
        keys.TryAdd(ZERO_GUID, new FAesKey(ZERO_64_CHAR));
        return keys.ToList();
    }

    private static IEnumerable<string> ReadKeysJson(string text)
    {
        var json = JObject.Parse(text);
        var main = json.Value<string>("mainKey") ?? json.Value<string>("MainKey");
        if (!string.IsNullOrEmpty(main)) yield return main;

        if ((json["dynamicKeys"] ?? json["DynamicKeys"]) is JArray dynamicKeys)
        {
            foreach (var d in dynamicKeys)
            {
                var guid = d.Value<string>("guid") ?? d.Value<string>("Guid");
                var key = d.Value<string>("key") ?? d.Value<string>("Key");
                if (!string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(key)) yield return $"{guid}:{key}";
            }
        }
    }

    private static string NormalizeKey(string key)
    {
        key = key.Trim();
        if (!key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) key = "0x" + key;
        if (key.Length != 66) throw new CliException($"AES key must be 0x + 64 hex characters: '{key}'");
        return key;
    }

    /// <summary>
    /// Oodle, zlib-ng and Detex like FModel's ApplicationViewModel: kept in the data folder, downloaded once when missing
    /// </summary>
    private static async Task InitNativesAsync(CliOptions o)
    {
        var dataDir = Path.GetFullPath(o.Get("data") ?? Path.Combine(AppContext.BaseDirectory, ".data"));
        Directory.CreateDirectory(dataDir);

        var oodlePath = Path.Combine(dataDir, OodleHelper.OODLE_NAME_OLD);
        if (!File.Exists(oodlePath)) oodlePath = Path.Combine(dataDir, OodleHelper.OODLE_NAME_CURRENT);
        await OodleHelper.InitializeAsync(oodlePath).ConfigureAwait(false);

        var zlibPath = Path.Combine(dataDir, ZlibHelper.DLL_NAME);
        if (File.Exists(zlibPath) || await ZlibHelper.DownloadDllAsync(zlibPath).ConfigureAwait(false))
            await ZlibHelper.InitializeAsync(zlibPath).ConfigureAwait(false);
        else
            Log.Warning("zlib-ng not available: zlib compressed archives will not read");

        var detexPath = Path.Combine(dataDir, DetexHelper.DLL_NAME);
        if (File.Exists(detexPath) || await DetexHelper.LoadDllAsync(detexPath).ConfigureAwait(false))
            DetexHelper.Initialize(detexPath);
        else
            Log.Warning("Detex not available: some texture formats will not decode");
    }

    public GameFile? Find(string path) => Provider.Files.TryGetValue(path, out var file) ? file : null;

    public void Dispose() => Provider.Dispose();
}
