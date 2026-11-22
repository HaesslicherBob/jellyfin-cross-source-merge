# Releasing Cross-Source Merge

The release is a single DLL inside a ZIP, plus one entry in `manifest.json`.
The ZIP that gets uploaded must be the same file the checksum was computed from.

## 1. Version

The version lives in three places and has to match everywhere:

| File | Field |
|---|---|
| `src/Jellyfin.Plugin.CrossSourceMerge/Jellyfin.Plugin.CrossSourceMerge.csproj` | `<Version>`, `<AssemblyVersion>`, `<FileVersion>` |
| `build.yaml` | `version` |
| `manifest.json` | `versions[0].version` (a new entry, the old ones stay) |

`targetAbi` stays at the Jellyfin version the DLL was compiled against.

## 2. Build and test

```
dotnet build src/Jellyfin.Plugin.CrossSourceMerge/Jellyfin.Plugin.CrossSourceMerge.csproj -c Release
dotnet run --project tests/laufprobe -c Release          # naming rules
cd tests/seitenprobe && node seitenprobe.js              # settings page in jsdom
```

Both have to end clean: `All rules pass.` and `PAGE OK`.

## 3. Package

The ZIP holds the DLL at its root - no folder, no `.pdb`:

```
mkdir -p package
cp src/Jellyfin.Plugin.CrossSourceMerge/bin/Release/net10.0/Jellyfin.Plugin.CrossSourceMerge.dll package/
cd package && zip -r ../cross-source-merge-<version>.zip . && cd ..
md5sum cross-source-merge-<version>.zip
```

## 4. Checksum into the manifest

Put the MD5 into `manifest.json` -> `versions[0].checksum`, and make sure
`sourceUrl` points at the asset of the tag:

```
https://github.com/HaesslicherBob/jellyfin-cross-source-merge/releases/download/v<version>/cross-source-merge-<version>.zip
```

Commit that before tagging, so the repository URL is valid the moment the release
appears.

## 5. Tag and upload

```
git tag -a v<version> -m "Cross-Source Merge <version>"
git push origin main --tags
```

The workflow `.github/workflows/release.yml` builds its own ZIP on the tag and
publishes it with the release, so the asset users download is the one CI built -
not the one from step 3. Its MD5 differs from a local build (the build path ends
up in the assembly), therefore:

1. let the release job finish,
2. take the MD5 from the run summary (`Checksum` step) or compute it from the
   downloaded asset,
3. put it into `manifest.json` and commit that on `main`.

That is the follow-up commit after the release commit - the manifest on `main`
must always describe the asset that is actually published. Do not upload the
local ZIP from step 3 to a release that CI already filled; two different files
under one version number break the install for everybody.

## 6. Check the result

- the release page carries the ZIP and the changelog text
- `manifest.json` on `main` shows the new version first
- in Jellyfin: *Dashboard > Plugins > Catalog > Cross-Source Merge* appears with
  the new version, install works, and the settings page opens
