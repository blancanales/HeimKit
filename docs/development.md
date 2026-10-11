# Development

Build HeimKit from the repository root using the supported environment in [server setup](server-setup.md#supported-environment). Follow [contributing](contributing.md) for changes.

## Building from source

Run from the repository root with local Windows server assemblies and a .NET SDK:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

If the BepInEx development references are elsewhere:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

The DLL is written to the standard Release bin directory; the exact output location and framework are listed in [Supported environment](server-setup.md#supported-environment). Install only `HeimKit.dll`; the adjacent example config is optional. Game and BepInEx assemblies are not distributed. WeatherGuard uses the Harmony library supplied with BepInEx; Jotunn is not required.

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
