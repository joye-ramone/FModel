using System.Diagnostics;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Nascar.Assets.Exports;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Houdini;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using Newtonsoft.Json;
using Serilog;

namespace FModel.CLI;

[Flags]
public enum EExportKind
{
    None = 0,
    Mesh = 1 << 0,
    Skeleton = 1 << 1,
    Anim = 1 << 2,
    Texture = 1 << 3,
    Material = 1 << 4,
    World = 1 << 5,
    Sound = 1 << 6,
    Embedded = 1 << 7,
    Raw = 1 << 8,
    Json = 1 << 9,
    Assets = Mesh | Anim | Texture | Sound,
    All = Mesh | Skeleton | Anim | Texture | Material | World | Sound | Embedded
}

public static class Commands
{
    private static readonly Dictionary<string, EMeshFormat> _meshFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["actorx"] = EMeshFormat.ActorX, ["psk"] = EMeshFormat.ActorX, ["gltf"] = EMeshFormat.Gltf2, ["glb"] = EMeshFormat.Gltf2,
        ["ueformat"] = EMeshFormat.UEFormat, ["uemodel"] = EMeshFormat.UEFormat, ["usd"] = EMeshFormat.USD
    };
    private static readonly Dictionary<string, ENaniteMeshFormat> _naniteFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nanite-only"] = ENaniteMeshFormat.NaniteOnly, ["no-nanite"] = ENaniteMeshFormat.NoNanite, ["nanite-first"] = ENaniteMeshFormat.NaniteFirst, ["nanite-last"] = ENaniteMeshFormat.NaniteLast
    };
    private static readonly Dictionary<string, EMaterialDepth> _materialDepths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = EMaterialDepth.TopLayerOnly, ["all-noref"] = EMaterialDepth.AllLayersNoRef, ["all"] = EMaterialDepth.AllLayers
    };
    private static readonly Dictionary<string, ETextureFormat> _textureFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = ETextureFormat.Jpeg
    };

    public static ExportOptions BuildExportOptions(CliOptions o) => new(
        meshFormat: o.GetEnum("mesh-format", EMeshFormat.ActorX, _meshFormats),
        naniteMeshFormat: o.GetEnum("nanite", ENaniteMeshFormat.NoNanite, _naniteFormats),
        meshQuality: o.GetEnum("lod", EMeshQuality.Highest),
        texturePlatform: o.GetEnum("platform", ETexturePlatform.DesktopMobile),
        textureFormat: o.GetEnum("texture-format", ETextureFormat.Png, _textureFormats),
        textureQuality: o.GetInt("texture-quality", 100),
        exportHdrTexturesAsHdr: !o.Has("hdr-as-png"),
        exportAllTextureMips: o.Has("all-mips"),
        materialDepth: o.GetEnum("materials", EMaterialDepth.TopLayerOnly, _materialDepths),
        exportMaterials: !o.Has("no-materials"),
        exportMorphTargets: !o.Has("no-morphs"),
        socketFormat: o.GetEnum("sockets", ESocketFormat.Bone),
        compressionFormat: o.GetEnum("compression", EFileCompressionFormat.ZSTD));

    public static EExportKind ParseKinds(string? text, EExportKind fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        var kinds = EExportKind.None;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            kinds |= part.ToLowerInvariant() switch
            {
                "mesh" or "meshes" or "model" or "models" => EExportKind.Mesh,
                "skeleton" or "skeletons" => EExportKind.Skeleton,
                "anim" or "anims" or "animation" or "animations" => EExportKind.Anim,
                "texture" or "textures" => EExportKind.Texture,
                "material" or "materials" => EExportKind.Material,
                "world" or "worlds" or "map" or "maps" => EExportKind.World,
                "sound" or "sounds" or "audio" => EExportKind.Sound,
                "embedded" => EExportKind.Embedded,
                "raw" => EExportKind.Raw,
                "json" or "properties" => EExportKind.Json,
                "assets" => EExportKind.Assets,
                "all" => EExportKind.All,
                _ => throw new CliException($"--kind: unknown kind '{part}' (mesh, skeleton, anim, texture, material, world, sound, embedded, raw, json, assets, all)")
            };
        }
        return kinds;
    }

    private static List<GameFile> Select(GameSession s, CliOptions o, bool packagesOnly)
    {
        var filter = new PathFilter(o.Patterns, o.Has("regex"));
        if (filter.IsEmpty && !o.Has("all")) throw new CliException("no path pattern given (use --all for every file)");

        var files = s.Provider.Files.Values
            .Where(f => !f.IsUePackagePayload && (!packagesOnly || f.IsUePackage) && (filter.IsEmpty || filter.Match(f)))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) throw new CliException("no file matches the given pattern(s)", ExitCodes.NoMatch);
        return files;
    }

    private static TextWriter OpenTable(CliOptions o)
    {
        var path = o.Get("tsv");
        if (path == null) return Console.Out;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
    }

    public static int Info(GameSession s, CliOptions o)
    {
        var p = s.Provider;
        using var w = OpenTable(o);
        w.WriteLine($"game\t{p.Versions.Game}");
        w.WriteLine($"platform\t{p.Versions.Platform}");
        w.WriteLine($"directory\t{s.GameDirectory}");
        w.WriteLine($"project\t{p.ProjectName}");
        w.WriteLine($"mappings\t{s.MappingsPath ?? "-"}");
        w.WriteLine($"archives\t{p.MountedVfs.Count + p.UnloadedVfs.Count}");
        w.WriteLine($"mounted\t{p.MountedVfs.Count}");
        w.WriteLine($"missing_keys\t{string.Join(",", p.RequiredKeys)}");
        w.WriteLine($"files\t{p.Files.Count}");
        w.WriteLine();
        w.WriteLine("archive\tmounted\tencrypted\tkey_guid\tfiles\tmount_point");
        foreach (var vfs in p.MountedVfs.Select(x => (x, true)).Concat(p.UnloadedVfs.Select(x => (x, false))).OrderBy(x => x.x.Name, StringComparer.OrdinalIgnoreCase))
            w.WriteLine($"{vfs.x.Name}\t{(vfs.Item2 ? 1 : 0)}\t{(vfs.x.IsEncrypted ? 1 : 0)}\t{vfs.x.EncryptionKeyGuid}\t{vfs.x.FileCount}\t{vfs.x.MountPoint}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// path, size, archive; with --exports one more row per export of each package: path, export name, class
    /// </summary>
    public static int List(GameSession s, CliOptions o)
    {
        var filter = new PathFilter(o.Patterns, o.Has("regex"));
        var files = s.Provider.Files.Values
            .Where(f => !f.IsUePackagePayload && (filter.IsEmpty || filter.Match(f)))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) throw new CliException("no file matches the given pattern(s)", ExitCodes.NoMatch);

        using var w = OpenTable(o);
        if (!o.Has("exports"))
        {
            w.WriteLine("path\tsize\tarchive");
            foreach (var f in files)
                w.WriteLine($"{f.Path}\t{f.Size}\t{(f is VfsEntry v ? v.Vfs.Name : "loose")}");
            return ExitCodes.Ok;
        }

        w.WriteLine("path\texport\tclass\tsuper");
        var failed = 0;
        foreach (var f in files.Where(x => x.IsUePackage))
        {
            IPackage pkg;
            try
            {
                pkg = s.Provider.LoadPackage(f);
            }
            catch (Exception e)
            {
                failed++;
                Log.Warning("Could not load {Path}: {Error}", f.Path, e.Message);
                continue;
            }

            for (var i = 0; i < pkg.ExportMapLength; i++)
            {
                var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
                if (pointer == null) continue;
                w.WriteLine($"{f.Path}\t{pointer.Name.Text}\t{pointer.Class?.Name.Text}\t{pointer.Super?.Name.Text}");
            }
        }
        return failed > 0 ? ExitCodes.ExportFailed : ExitCodes.Ok;
    }

    public static int Refs(GameSession s, CliOptions o)
    {
        using var w = OpenTable(o);
        w.WriteLine("package\treferenced_by");
        foreach (var f in Select(s, o, true))
            foreach (var r in s.Provider.ScanForPackageRefs(f))
                w.WriteLine($"{f.Path}\t{r.Path}");
        return ExitCodes.Ok;
    }

    public static Task<int> Json(GameSession s, CliOptions o) => Export(s, o, EExportKind.Json);

    public static async Task<int> Export(GameSession s, CliOptions o, EExportKind? forced = null)
    {
        var kinds = forced ?? ParseKinds(o.Get("kind"), EExportKind.Assets);
        var outDir = Path.GetFullPath(o.Get("out") ?? "Exports");
        var options = BuildExportOptions(o);
        var files = Select(s, o, !kinds.HasFlag(EExportKind.Raw));
        var toStdout = o.Has("stdout") && kinds == EExportKind.Json && files.Count == 1;

        Log.Information("Exporting {Kinds} from {Count} file(s) to {Out}", kinds, files.Count, outDir);
        var session = new ExportSession { MaxDegreeOfParallelism = o.GetInt("threads", Environment.ProcessorCount) };
        var written = 0;
        var failed = 0;

        foreach (var file in files)
        {
            try
            {
                if (kinds.HasFlag(EExportKind.Raw))
                    session.Add(new RawDataExporter(file, s.Provider));

                if (!file.IsUePackage || (kinds & ~EExportKind.Raw) == EExportKind.None)
                    continue;

                var pkg = s.Provider.LoadPackage(file);
                if (kinds.HasFlag(EExportKind.Json))
                {
                    var json = JsonConvert.SerializeObject(pkg.GetExports(), Formatting.Indented);
                    if (toStdout)
                    {
                        Console.Out.WriteLine(json);
                    }
                    else
                    {
                        var path = Path.Combine(outDir, file.PathWithoutExtension.Replace('/', '\\') + ".json");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
                    }
                    written++;
                }

                for (var i = 0; i < pkg.ExportMapLength; i++)
                    written += QueueExport(session, pkg, file, i, kinds, outDir, o.Has("raw-sound"));
            }
            catch (Exception e)
            {
                failed++;
                Log.Error(e, "Failed on {Path}", file.Path);
            }
        }

        if (session.HasQueuedItems)
        {
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var progress = new Progress<ExportProgress>(p =>
            {
                if (p.LastResult is { Success: false } r) Log.Error(r.Error, "Export failed: {Path}", r.ObjectPath);
                else if (p.LastResult is { } ok) Log.Debug("Exported {Path}: {Files}", ok.ObjectPath, string.Join(", ", ok.DiskFilePaths ?? []));
                if (sw.Elapsed - lastReport < TimeSpan.FromSeconds(2) && p.Completed < p.Total) return;
                lastReport = sw.Elapsed;
                Log.Information("Export {Progress}", p.DisplayText);
            });

            var results = await session.RunAsync(outDir, options, progress).ConfigureAwait(false);
            written += results.Count(x => x.Success);
            failed += results.Count(x => !x.Success);
        }

        Log.Information("Done: {Written} written, {Failed} failed", written, failed);
        return failed > 0 ? ExitCodes.ExportFailed : ExitCodes.Ok;
    }

    /// <summary>
    /// picks the exporter per export class like CUE4ParseViewModel.CheckExport; sounds are written here, the rest is queued in the session
    /// </summary>
    private static int QueueExport(ExportSession session, IPackage pkg, GameFile file, int index, EExportKind kinds, string outDir, bool rawSound)
    {
        var pointer = new FPackageIndex(pkg, index + 1).ResolvedObject;
        if (pointer?.Object is null) return 0;

        var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class, pkg);
        switch (dummy)
        {
            case UTexture when kinds.HasFlag(EExportKind.Texture):
            case UStaticMesh when kinds.HasFlag(EExportKind.Mesh):
            case UIRMesh when kinds.HasFlag(EExportKind.Mesh):
            case UHoudiniStaticMesh when kinds.HasFlag(EExportKind.Mesh):
            case UGeometryCollection when kinds.HasFlag(EExportKind.Mesh):
            case USkinnedAsset when kinds.HasFlag(EExportKind.Mesh):
            case USkeleton when kinds.HasFlag(EExportKind.Skeleton):
            case UAnimationAsset when kinds.HasFlag(EExportKind.Anim):
            case UMaterialInterface when kinds.HasFlag(EExportKind.Material):
            case UWorld when kinds.HasFlag(EExportKind.World):
                session.Add(pointer.Object.Value);
                return 0;
            case UObject when kinds.HasFlag(EExportKind.Embedded) && EmbeddedFilesDto.Supports(dummy):
                if (new EmbeddedFilesDto(pointer.Object.Value).Files.Count > 0) session.Add(new EmbeddedFilesExporter(pointer.Object.Value));
                return 0;
            case UAkMediaAssetData when kinds.HasFlag(EExportKind.Sound):
            case USoundWave when kinds.HasFlag(EExportKind.Sound):
            {
                // the UAkMediaAsset of the same package has the real name, it writes the sound instead
                if (pointer.Object.Value is UAkMediaAssetData dataObj && dataObj.Outer?.Object?.Value is UAkMediaAsset) return 0;
                return WriteSound(pointer.Object.Value, file.PathWithoutExtension, outDir, rawSound);
            }
            case UAkMediaAsset when kinds.HasFlag(EExportKind.Sound) && pointer.Object.Value is UAkMediaAsset media:
            {
                if (media.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is not UAkMediaAssetData data) return 0;
                var name = string.IsNullOrEmpty(media.MediaName) ? media.Name : media.MediaName;
                return WriteSound(data, Path.Combine(file.Directory, name), outDir, rawSound);
            }
            default:
                return 0;
        }
    }

    private static int WriteSound(UObject export, string virtualPath, string outDir, bool rawSound)
    {
        export.Decode(!rawSound, out var audioFormat, out var data);
        if (data == null || string.IsNullOrEmpty(audioFormat))
        {
            if (!string.IsNullOrEmpty(audioFormat)) Log.Warning("Unsupported audio format '{Format}': {Path}", audioFormat, virtualPath);
            return 0;
        }

        var path = Path.Combine(outDir, virtualPath.Replace('/', '\\') + "." + audioFormat.ToLowerInvariant());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        Log.Debug("Sound {Path}", path);
        return 1;
    }
}
