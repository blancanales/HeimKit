# Development

Build HeimKit from the repository root using the supported environment in [server setup](server-setup.md#supported-environment). Follow [contributing](contributing.md) for changes.

## Building from source

Before invoking a build, complete the required [version and documentation procedure](contributing.md#before-every-build). Updated code needs a distinct build identity with matching plugin metadata, startup message and documented build version; applicable behavior and configuration documentation must also be current. Development builds use a prerelease suffix and counter rather than advancing the published release version. AI agents follow the [AI versioning policy](ai/versioning.md), including explicit build authorization.

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

Supply `-BepInExCore` as above when needed. Each configuration has its own output directory; the exact locations and framework are listed in [Supported environment](server-setup.md#supported-environment). Debug includes debugging symbols and disables normal release optimization; Release uses optimized code. Both configurations retain the development version until a release is deliberately prepared. Startup output identifies the version and build configuration. The configuration does not turn feature diagnostics on automatically; use the existing DebugMode settings in either build.

Install only one `HeimKit.dll` at a time; the adjacent example config is optional. Game and BepInEx assemblies are not distributed. WeatherGuard uses the Harmony library supplied with BepInEx; Jotunn is not required.

## Development build command

Use one invocation for a complete development build after preparing its version and documentation:

```powershell
.\Build-Test.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

The script builds the Debug plugin and automated checks together, runs the checks, builds the matching Release plugin, verifies version metadata and configuration, then copies the DLLs and symbols into `artifacts/builds/HeimKit-<development-version>/Debug/` and `Release/`. Omit BepInExCore when using the server's default core directory. It stops on failure and refuses to overwrite different versioned artifacts under the same development identity. It does not assign versions, package a release, install, restart, commit, push or publish.

## Packaging for Thunderstore

## Release build command

After integrating the intended source into main, preparing a plain release version and committing the reviewed release metadata, use:

```powershell
.\Build-Release.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

The command requires a clean main checkout and a plain release version. It runs the automated checks, builds the optimized plugin through the existing packager, validates the ZIP and checks DLL version metadata. The resulting ZIP is suitable for the GitHub release asset and Trevor's manual Thunderstore upload. It defaults packaged README links to the matching immutable version tag. Optional IconPath, OutputDirectory, PackageName, ReadmeBaseUrl and Force settings are passed to the packager. Omit BepInExCore when the server has its core references in the default location.

This command does not perform Git merges, commits, pushes, branch deletion or GitHub publication. AI agents performing a requested release follow the separate [AI release workflow](ai/releases.md) for those operations. A Release-configuration DLL created by build.ps1 or Build-Test.ps1 is not itself a published release.

## Packaging for Thunderstore

`Publish-Thunderstore.ps1` builds Release using the existing build script and produces a local, versioned ZIP under `artifacts/thunderstore/`. It requires PowerShell 7 or newer, a .NET SDK, and the same local server and loader references used for building. The script does not upload the package or install it into a server.

For the complete local release build, use Build-Release.ps1 above. For explicitly requested standalone local packaging, complete the [version and documentation procedure](contributing.md#before-every-build), update the root CHANGELOG.md with the current release notes, then run from the repository root:

```powershell
.\Publish-Thunderstore.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

Use `-BepInExCore 'C:\path\to\BepInEx\core'` when the loader references are outside the server installation. Paths may contain spaces. The included `docs/images/icon.png` is the default package icon; `-IconPath 'C:\path\to\icon.png'` selects a different valid 256x256 PNG.

The version is read from `src/HeimKit/HeimKit.csproj`; the script requires matching source plugin metadata, startup text, the documented build version, and compiled assembly/BepInPlugin metadata. It does not increment versions automatically. Rebuilding unchanged plugin code retains its version. The required loader dependency is pinned to the BepInExPack Valheim row in [Supported environment](server-setup.md#supported-environment), where supported versions are maintained. Harmony comes with that loader; Jotunn is not required. Other mod dependencies can be declared with `-AdditionalDependencies @('Team-Package-Major.Minor.Patch')`, using real dependency strings from their Thunderstore pages. Dependency packages are declared, not downloaded or bundled.

Optional parameters:

| Parameter | Purpose |
| --- | --- |
| `-OutputDirectory` | Destination directory; default `artifacts/thunderstore/`, which Git ignores. Relative paths resolve from the current working directory. |
| `-PackageName` | Thunderstore package name; default `HeimKit`. Letters, digits and underscores only. |
| `-Description` | Package summary of up to 250 characters. |
| `-WebsiteUrl` | Project website; defaults to this GitHub repository. May be an empty string. |
| `-ReadmeBaseUrl` | Published GitHub branch or tag URL for README links and images; defaults to the repository's `blob/main` URL. Set it to the branch or tag containing the release documentation and assets. |
| `-Force` | Replace an existing ZIP of the same name only after the new archive passes local checks. |

The ZIP name is `<PackageName>-<project-version>.zip`. Its root contains `manifest.json`, `README.md`, `CHANGELOG.md` and `icon.png`; the DLL is under `BepInEx/plugins/HeimKit/`. It also includes the license, guides, README images, contributor guidance and an optional reference config under `config/`. It deliberately excludes game and loader assemblies, build intermediates, symbols and live settings. The reference config is not placed in `BepInEx/config`, allowing HeimKit to generate defaults and preserve existing server values.

The repository README keeps relative links. The packaged README rewrites documentation and image links to `ReadmeBaseUrl` so the Thunderstore page can display them. Those files must be pushed to the selected public branch or tag before uploading; creating a ZIP does not publish GitHub assets. The script requires CHANGELOG.md to start with a release heading matching the project version and contain at least one nonempty change bullet for that release. It preserves earlier release notes and includes the changelog at the ZIP root for Thunderstore display. It checks metadata, icon format/dimensions, dependency string syntax, text encoding, ZIP entries and readability; it does not contact Thunderstore to check dependency availability or guarantee upload acceptance. Review the package presentation using Thunderstore's [Markdown preview](https://thunderstore.io/tools/markdown-preview/) and [manifest validator](https://thunderstore.io/tools/manifest-v1-validator/), then upload the ZIP to the Valheim community under your chosen team when ready.

## Diagnostic configuration integration

Each module uses the shared DebugLoggingResolver and DebugLoggingPolicy described in [architecture](architecture.md#logging), following the [contributor requirements](contributing.md#shared-logging-format). Resolve the hidden suppression-only override during settings construction; use the effective policy for optional output and costly evidence collection. Keep hidden keys absent from generated/reference config, preserve manual values and retain restart-required behavior. The complete user configuration contract belongs in [server setup](server-setup.md#hidden-per-feature-debug-suppression).

Server-admin coverage uses the [documented local request file](server-setup.md#requested-weatherguard-coverage), not a new client command. Keep explicit request handling independent of automatic diagnostics. Gameplay channel and exact-exclusion rules belong in WeatherGuardController/WeatherGuardRules; configuration definitions remain in WeatherGuardSettings and requested report formatting remains in WeatherGuardDiagnostics.

## Plugin development and Thunderstore references

HeimKit is a C# BepInEx plugin that runs inside the Valheim dedicated-server process. Thunderstore distributes mod packages; the plugin DLL is built using the commands above, then packaged separately for distribution.

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
