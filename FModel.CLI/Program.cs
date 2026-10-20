using Serilog;
using Serilog.Events;

namespace FModel.CLI;

public static class Program
{
    private const string Usage = """
FModel.CLI - command line export of Unreal Engine game assets (CUE4Parse, FModel's parser)

usage: FModel.CLI <command> [patterns...] --game <Paks folder> [options]

commands:
  info                    mount summary and every archive (mounted, encrypted, key GUID, files)
  list [patterns]         every file: path, size, archive; --exports adds export name / class per package
  json <patterns>         package properties as .json (FModel's "Save Properties"); --stdout for one package
  export <patterns>       exports by --kind (default assets = mesh, anim, texture, sound)
  refs <patterns>         packages that reference the given ones

patterns: virtual paths, case insensitive: exact ("Game/Content/A/B.uasset", extension optional),
  folder ("Game/Content/Weapons/"), glob ("**/SK_*.uasset", "*" stays in one folder) or --regex; --all for every file

game:
  --game <dir>            the Paks folder (scanned recursively, like FModel)
  --ue <EGame>            CUE4Parse game id, e.g. GAME_Stalker2, GAME_UE5_5 (default GAME_UE5_LATEST)
  --platform <name>       texture platform (DesktopMobile)
  --aes <key>             main key 0x + 64 hex; GUID:0x... for a dynamic key; repeatable
  --aes-file <file>       keys one per line (same forms) or json with mainKey / dynamicKeys
  --usmap <file>          mappings (.usmap or .jmap) for unversioned properties
  --strict                fail (exit 2) when an AES key is missing
  --data <dir>            native libraries (Oodle, zlib-ng, Detex; downloaded once), default <exe>\.data

export:
  --out <dir>             output folder, the archive tree below it (default .\Exports)
  --kind <list>           mesh, skeleton, anim, texture, material, world, sound, embedded, raw, json, assets, all
  --mesh-format <f>       actorx (psk/psa, default), gltf, ueformat, usd (worlds need usd; anims: actorx, ueformat, usd)
  --nanite <m>            no-nanite (default), nanite-only, nanite-first, nanite-last
  --lod <q>               Highest (default), Lowest, All
  --sockets <s>           Bone (default), Socket, None
  --materials <d>         top (default), all-noref, all
  --no-materials          meshes without their materials
  --no-morphs             meshes without morph targets
  --texture-format <f>    png (default), jpeg, tga, webp
  --texture-quality <n>   1..100 (100)
  --hdr-as-png            HDR textures as png instead of .hdr
  --all-mips              every mip level
  --compression <c>       ueformat only: ZSTD (default), GZIP, None
  --raw-sound             sounds as stored (no decompression of ADPCM etc.)
  --threads <n>           parallel exports (processor count)

output:
  --tsv <file>            info / list / refs table into a file instead of stdout
  --log <file>            log file (default <exe>\Logs\FModel.CLI-<date>.log)
  -v, --verbose           debug log
  -h, --help

exit codes: 0 ok, 1 usage / error, 2 mount failed or key missing (--strict), 3 some exports failed, 4 no file matched
""";

    public static async Task<int> Main(string[] args)
    {
        CliOptions o;
        try
        {
            o = CliOptions.Parse(args);
        }
        catch (CliException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return e.ExitCode;
        }

        if (o.Has("help") || string.IsNullOrEmpty(o.Command) || o.Command == "help")
        {
            Console.Out.Write(Usage);
            return string.IsNullOrEmpty(o.Command) && !o.Has("help") ? ExitCodes.Usage : ExitCodes.Ok;
        }

        var logPath = o.Get("log") ?? Path.Combine(AppContext.BaseDirectory, "Logs", $"FModel.CLI-{DateTime.Now:yyyy-MM-dd}.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(o.Has("verbose") ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose) // stdout stays free for the tables / json
            .WriteTo.File(logPath)
            .CreateLogger();

        try
        {
            if (o.Command is not ("info" or "list" or "json" or "export" or "refs"))
                throw new CliException($"unknown command '{o.Command}' (info, list, json, export, refs)");

            using var session = await GameSession.OpenAsync(o).ConfigureAwait(false);
            return o.Command switch
            {
                "info" => Commands.Info(session, o),
                "list" => Commands.List(session, o),
                "json" => await Commands.Json(session, o).ConfigureAwait(false),
                "export" => await Commands.Export(session, o).ConfigureAwait(false),
                "refs" => Commands.Refs(session, o),
                _ => ExitCodes.Usage
            };
        }
        catch (CliException e)
        {
            Log.Error("{Message}", e.Message);
            return e.ExitCode;
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Unhandled error");
            return ExitCodes.Usage;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }
}
