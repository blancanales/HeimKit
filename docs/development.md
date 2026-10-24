# Development

Build HeimKit from the repository root using the supported environment in [server setup](server-setup.md#supported-environment). Follow [contributing](contributing.md) for changes.

## Building from source

Before building, synchronize identity and documentation through [build preparation](contributing.md#before-every-build). Development DLLs use a prerelease identity; published releases use a plain version. AI authorization and identity rules are in [versioning](ai/versioning.md).

Run from the repository root with local Windows server assemblies and a .NET SDK:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

If the BepInEx development references are elsewhere:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

The script accepts `-Configuration Debug` or `-Configuration Release`, defaulting to Release. To create a Debug DLL:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -Configuration Debug
```

Release is optimized; Debug adds symbols and disables optimization. Both use the prepared identity and report configuration at startup. Output locations/framework are listed in [Supported environment](server-setup.md#supported-environment). DebugMode controls runtime logs in either configuration.

Install only one `HeimKit.dll` at a time; the adjacent example config is optional. Game and BepInEx assemblies are not distributed. WeatherGuard uses the Harmony library supplied with BepInEx; Jotunn is not required.

Use Release unless Debug is requested. Keep local DLLs in the standard configuration-specific source bin directory.

The performance rebuild remains in the temporary `src/HeimKit2/` directory until cutover. Select its project explicitly:

```powershell
.\build.ps1 -ProjectPath 'src\HeimKit2\HeimKit.csproj' -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

Its output is `src/HeimKit2/bin/<configuration>/<target-framework>/HeimKit.dll`. Both source trees identify as HeimKit and share the `heimkit` loader identity, configuration and persistent data paths; deploy only one implementation. After the legacy source is retired and the rebuild folder becomes `src/HeimKit/`, the default build command selects that project without changing the application identity. The [rebuild specification](ai/heimkit-rebuild-spec.md) defines Core ownership. Current build identities appear in [supported environment](server-setup.md#supported-environment).

## Release build command

After integrating the intended source into main, preparing a plain release version and committing the reviewed release metadata, use:

```powershell
.\Build-Release.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

The command requires a clean main checkout and a plain release version. It runs the automated checks, builds the optimized plugin through the existing packager, validates the ZIP and checks DLL version metadata. The resulting ZIP is suitable for the GitHub release asset and Trevor's manual Thunderstore upload. It defaults packaged README links to the matching immutable version tag. Optional IconPath, OutputDirectory, PackageName, ReadmeBaseUrl and Force settings are passed to the packager. Omit BepInExCore when the server has its core references in the default location.

Build-Release.ps1 handles local preparation/build/packaging. Git/GitHub operations follow the [release workflow](ai/releases.md).

## Packaging for Thunderstore

`Publish-Thunderstore.ps1` builds Release using the existing build script and produces a local, versioned ZIP under `artifacts/thunderstore/`. It requires PowerShell 7 or newer, a .NET SDK, and the same local server and loader references used for building. Upload and installation are separate operations.

For the complete local release build, use Build-Release.ps1 above. For explicitly requested standalone local packaging, complete the [version and documentation procedure](contributing.md#before-every-build), update the root CHANGELOG.md with the current release notes, then run from the repository root:

```powershell
.\Publish-Thunderstore.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

Use `-BepInExCore 'C:\path\to\BepInEx\core'` when the loader references are outside the server installation. Paths may contain spaces. The included `docs/images/icon.png` is the default package icon; `-IconPath 'C:\path\to\icon.png'` selects a different valid 256x256 PNG.

The packager reads the project version and checks matching plugin/startup/documentation/DLL metadata. Prepare the version before invoking it. The required loader dependency is pinned to the BepInExPack Valheim row in [Supported environment](server-setup.md#supported-environment), where supported versions are maintained. Harmony comes with that loader; Jotunn is not required. Other mod dependencies can be declared with `-AdditionalDependencies @('Team-Package-Major.Minor.Patch')`, using real dependency strings from their Thunderstore pages. Dependency packages are declared, not downloaded or bundled.

Optional parameters:

| Parameter | Purpose |
| --- | --- |
| `-OutputDirectory` | Destination directory; default `artifacts/thunderstore/`, which Git ignores. Relative paths resolve from the current working directory. |
| `-PackageName` | Thunderstore package name; default `HeimKit`. Letters, digits and underscores only. |
| `-Description` | Short package summary written to `manifest.json`; up to 250 characters. Defaults to the feature/search wording maintained in `Publish-Thunderstore.ps1`. |
| `-WebsiteUrl` | Project website; defaults to this GitHub repository. May be an empty string. |
| `-ReadmeBaseUrl` | Published GitHub branch or tag URL for README links and images; defaults to the repository's `blob/main` URL. Set it to the branch or tag containing the release documentation and assets. |
| `-Force` | Replace an existing ZIP of the same name only after the new archive passes local checks. |

The ZIP name is `<PackageName>-<project-version>.zip`. Its root contains `manifest.json`, `README.md`, `CHANGELOG.md` and `icon.png`; the DLL is under `BepInEx/plugins/HeimKit/`. It also includes the license, guides, README images, contributor guidance and an optional reference config under `config/`. It deliberately excludes game and loader assemblies, build intermediates, symbols and live settings. The reference config is not placed in `BepInEx/config`, allowing HeimKit to generate defaults and preserve existing server values.

The ZIP's `manifest.json` is generated by `Publish-Thunderstore.ps1`; there is no separately maintained root manifest. Its default `Description` includes **Infinite Torches**, **InfiniteTorches**, **Shared Map**, **SharedMap** and **AFK**, alongside the other supported benefits. Retain these terms when revising the summary for an update, including when overriding `-Description`. `Build-Release.ps1` uses this default description. Keep the package name `HeimKit` when updating the existing listing.

Thunderstore's [listing search implementation](https://github.com/thunderstore-io/Thunderstore/blob/master/django/thunderstore/api/cyberstorm/views/package_listing_list.py) splits queries on spaces and requires each word to appear, case-insensitively, within the package name, owner name or latest version's description. Substrings qualify: `torch` matches `Torches`, and `portal` matches `portals`. It does not perform synonym expansion or typo correction. Use relevant feature wording in the description, rather than adding an unsupported tags field to the manifest. The default summary also covers fuel, braziers, exploration, map sharing, pins/markers, portals, ore/resources, dungeons, runestones, spawners, rain/weather protection, sleep and raid protection. The README supplies the full package page and does not replace those searchable metadata fields. The [manifest requirements](https://wiki.thunderstore.io/mods/creating-a-package#manifest) define the description limit; upload categories provide additional discovery filters. Updated metadata reaches the public listing when the new package is uploaded.

The repository README keeps relative links. The packaged README rewrites documentation and image links to `ReadmeBaseUrl` so the Thunderstore page can display them. Those files must be pushed to the selected public branch or tag before uploading; creating a ZIP does not publish GitHub assets. The script requires CHANGELOG.md to start with a release heading matching the project version and contain at least one nonempty change bullet for that release. It preserves earlier release notes and includes the changelog at the ZIP root for Thunderstore display. Local checks cover metadata, icon dimensions, dependency syntax, text encoding, ZIP entries and readability. Review the package presentation using Thunderstore's [Markdown preview](https://thunderstore.io/tools/markdown-preview/) and [manifest validator](https://thunderstore.io/tools/manifest-v1-validator/), then upload the ZIP to the Valheim community under your chosen team when ready.

## Diagnostic configuration integration

Each module uses the shared DebugLoggingResolver and DebugLoggingPolicy described in [architecture](architecture.md#logging), following the [contributor requirements](contributing.md#shared-logging-format). Resolve the hidden suppression-only override during settings construction; use the effective policy for optional output and costly evidence collection. Keep hidden keys absent from generated/reference config, preserve manual values and retain restart-required behavior. The complete user configuration contract belongs in [server setup](server-setup.md#hidden-per-feature-debug-suppression).

Server-admin coverage uses the [documented local request file](server-setup.md#requested-weatherguard-coverage), not a new client command. Keep explicit request handling independent of automatic diagnostics. Gameplay channel and exact-exclusion rules belong in WeatherGuardController/WeatherGuardRules; configuration definitions remain in WeatherGuardSettings and requested report formatting remains in WeatherGuardDiagnostics.

## Plugin development and Thunderstore references

| Reference | Use for HeimKit |
| --- | --- |
| [BepInEx: writing a basic plugin](https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/index.html) | Plugin entry points, project references, logging and configuration. Use documentation matching the loader in [Supported environment](server-setup.md#supported-environment). |
| [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) | Valheim-specific loader setup and package information. |
| [Valheim Modding/Jotunn step-by-step guide](https://valheim-modding.github.io/Jotunn/guides/guide.html) | Valheim development environment, game assembly references and example mod projects. This is community-maintained guidance, not an Iron Gate API contract. |
| [Harmony patching documentation](https://harmony.pardeike.net/v2/articles/patching.html) | Prefixes, postfixes, transpilers, finalizers and patch behavior. |
| [Thunderstore: creating a package](https://wiki.thunderstore.io/mods/creating-a-package) | Required ZIP-root manifest, README and 256x256 PNG icon, versioning and dependency strings. |
| [Thunderstore: packaging BepInEx mods](https://wiki.thunderstore.io/mods/packaging-your-mods) | Plugin folder layout and mod-manager installation rules. |

Use these guides alongside the project's [Supported environment](server-setup.md#supported-environment), [architecture](architecture.md) and [contributing procedure](contributing.md). HeimKit uses the framework listed there, local dedicated-server assemblies and the loader-provided Harmony library; Jotunn is not a dependency. Example projects can use different framework targets, publicized assemblies or automatic deployment scripts, so retain HeimKit's existing build/reference setup when consulting them.

Before changing a game method or ownership behavior, inspect the corresponding methods in the installed, supported Valheim assemblies to determine their behavior. General modding guides do not establish that a calculation runs on the server or that a patch preserves native gameplay. Thunderstore packaging requirements are separate from the game methods and networking behavior.
