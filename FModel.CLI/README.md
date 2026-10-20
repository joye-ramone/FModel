# FModel.CLI

Command line export of Unreal Engine game assets, for batch work and scripts. It uses the same parser and exporters as FModel (CUE4Parse, CUE4Parse-Conversion) and sets up the file provider the way FModel does (`CUE4ParseViewModel`), without the WPF application.

## Build

```
dotnet build FModel.CLI\FModel.CLI.csproj -c Release
```

CUE4Parse builds its native library (`CUE4Parse-Natives.dll`: ACL animation decompression and more) with CMake; `cmake` must be on `PATH` (Visual Studio ships one in `Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin`). Without it the build continues without the natives and compressed animations will not export.

Output: `FModel.CLI\bin\Release\net10.0\win-x64\FModel.CLI.exe` (.NET 10 runtime). `dotnet publish -c Release` gives one file.

On the first run Oodle, zlib-ng and Detex are put into `<exe>\.data` (`--data` to move it): Oodle and zlib-ng are downloaded once, like FModel does.

## Commands

```
FModel.CLI info   --game <Paks> --ue <EGame> [--aes ...] [--usmap ...]
FModel.CLI list   [patterns] [--exports] [--tsv file]
FModel.CLI json   <patterns> [--out dir | --stdout]
FModel.CLI export <patterns> --kind mesh,anim,texture,sound [--mesh-format actorx] [--out dir]
FModel.CLI refs   <patterns>
FModel.CLI --help
```

- `--game` is the `Paks` folder (scanned recursively); `--ue` the CUE4Parse game id (`GAME_Stalker2`, `GAME_UE5_5`, ...).
- Keys: `--aes 0x<64 hex>` (main), `--aes <GUID>:0x...` (dynamic), `--aes-file` (one per line, or json with `mainKey` / `dynamicKeys`). Unencrypted archives need none.
- `--usmap` for games cooked with unversioned properties.
- Patterns are virtual paths, case insensitive: exact (`Game/Content/A/B.uasset`, extension optional), a folder (`Game/Content/Weapons/`), a glob (`**/SK_*.uasset`) or `--regex`; `--all` selects every file.
- Exports land below `--out` (default `.\Exports`) in the archive's folder tree, like FModel's `Exports`.
- Tables (`info`, `list`, `refs`) and `json --stdout` go to stdout as TSV / JSON; the log goes to stderr and to `<exe>\Logs`.
- `--kind`: `mesh`, `skeleton`, `anim`, `texture`, `material`, `world`, `sound`, `embedded`, `raw` (packages as stored), `json`, `assets` (= mesh, anim, texture, sound, the default), `all`.
- Mesh format default is ActorX (`.psk` / `.psa`); `gltf`, `ueformat`, `usd` as in FModel. Worlds need `usd`; animations export as ActorX, UEFormat or USD.

Exit codes: 0 ok, 1 usage / error, 2 nothing mounted or a key missing with `--strict`, 3 some exports failed, 4 no file matched.

## Example

```
FModel.CLI export "Stalker2/Content/_Stalker_2/weapons/" --game "D:\S2\Stalker2\Content\Paks" --ue GAME_Stalker2 --usmap S2.usmap --kind mesh,anim,texture --mesh-format actorx --nanite no-nanite --out D:\s2_export
```
