# Server setup

Install HeimKit on a Windows Valheim dedicated server using Thunderstore Mod Manager or a manual installation. Both routes use the same plugin and feature configuration. See the [Infinite Torches guide](features/infinite-torches.md) and [WeatherGuard guide](features/weather-guard.md) for feature-specific usage.

## Supported environment

| Item | Current baseline |
| --- | --- |
| HeimKit build | 0.1.12 |
| Valheim Dedicated Server | 1.0.17; enforced by module compatibility guards. |
| BepInExPack Valheim | 5.4.2351 |
| BepInEx runtime | 5.4.23.5 |
| Plugin target framework | netstandard2.1 |
| Build output | `src/HeimKit/bin/Release/netstandard2.1/HeimKit.dll` |

This section is the sole documentation source for HeimKit's supported game, loader, runtime and framework versions. Other documents link here rather than repeating version values. Update this baseline when code guards and dependencies change.

## Installation

### Before either installation route

1. Stop the dedicated server and [back up its world/save data](world-backups.md) before installing or enabling HeimKit. Also preserve any existing loader/configuration files.
2. Locate the dedicated-server folder containing `valheim_server.exe`. This is separate from the client game's `Valheim` folder. For a Steam installation, open **Valheim Dedicated Server → Properties → Installed Files → Browse**.
3. Obtain `HeimKit.dll` from the project, or follow [building from source](development.md#building-from-source). These instructions accept a local DLL and do not require a published HeimKit Thunderstore package.
4. Use the game and loader versions in [Supported environment](#supported-environment). Inspect an existing BepInEx installation before replacing its files.

Choose one route below for the server. Having Thunderstore Mod Manager installed does not by itself install BepInEx or HeimKit into the dedicated-server process. HeimKit belongs on the server; client installation is not required by the design.

### Option A: Thunderstore Mod Manager

This route keeps HeimKit in a manager profile and launches the dedicated server through that profile. Thunderstore supports Valheim profiles and mod installation; the [Valheim modding maintainers' server guidance](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#thunderstore-mod-manager-or-r2modman) covers dedicated-server launching and argument forwarding.

1. Install [Thunderstore Mod Manager](https://www.overwolf.com/app/thunderstore-thunderstore_mod_manager), or open it if already installed. Select Valheim and its server mode. Use a separate server profile, such as **HeimKit Server**, rather than sharing a running client profile.
2. Set the dedicated-server location to the folder containing `valheim_server.exe`. Use the manager's server launch mode, not the normal game launch. If your manager installation does not expose server mode or a server location, use [Option B](#option-b-manual-installation) instead.
3. Install **denikson — BepInExPack_Valheim** into the selected server profile, using the version in the supported environment above. If the loader is already present, retain its configuration rather than replacing it blindly.
4. Open the selected profile's **Settings → Browse profile folder**. Inside that profile, create `BepInEx/plugins/HeimKit/` and copy `HeimKit.dll` there. Keep one HeimKit DLL in the profile; remove duplicate older copies from other plugin folders. This manual DLL addition works alongside manager-installed dependencies. Profile-folder navigation is described in the [Thunderstore profile documentation](https://wiki.thunderstore.io/sharing-your-mods/modpacks-and-profiles).
5. In the dedicated-server folder, back up `start_headless_server.bat`. At the end of its existing `valheim_server` launch line, append a space followed by `%*` if it is not already present. Keep the existing server name, world, password, port and other arguments. This forwards the manager's loader arguments, as documented by the Valheim modding maintainers.
6. Start the dedicated server using the manager's modded server launch action. Use this profile for subsequent starts. If you also play on the same machine, use a separate client profile or launch the unmodified client normally.

The active loader, plugin and configuration files for this route are in the selected **profile's** `BepInEx` folder. A DLL copied only into the Steam server folder is not the same as one installed into the active manager profile. Menu labels can vary between manager versions; use the manual route if the dedicated-server launch options are unavailable.

### Option B: Manual installation

This route puts the loader and HeimKit directly beside the dedicated-server executable. It also applies when Thunderstore Mod Manager is already installed: leave the manager profile separate and launch this server with its own startup script.

1. Download **BepInExPack_Valheim** from its [package page](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) using the version listed above. Extract the archive into a temporary folder.
2. Copy the **contents** of `BepInExPack_Valheim` into the dedicated-server folder containing `valheim_server.exe`. Copy the loader's root files as well as its `BepInEx` folder; the archive's outer folder must not become an extra nested directory. For an existing installation, preserve configuration and update only the loader files needed. The pack's [manual installation instructions](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/#installation-manual) describe the archive layout; for HeimKit the destination is the dedicated-server folder.
3. Create `BepInEx/plugins/HeimKit/` in that server folder and copy `HeimKit.dll` into it. Keep one HeimKit DLL; remove duplicate older copies from other plugin folders in this server installation.
4. Start the Windows dedicated server with its own startup script, such as `start_headless_server.bat`, keeping its existing world and connection settings. Use this launch route for later starts rather than switching to a manager profile's loader arguments.

The active loader, plugin and configuration files for this route are in the **dedicated-server folder's** `BepInEx` directory. Installing Thunderstore Mod Manager afterward does not move or manage this manual installation automatically.

### Shared configuration and maintenance

On startup, HeimKit creates `BepInEx/config/heimkit.cfg` under the active loader location and logs the full configuration path. There is no need to create or copy a configuration file first. Use the profile location for Option A and the dedicated-server location for Option B.

**Updating or replacing the mod while the dedicated server is running can corrupt the world.** Stop the server gracefully and wait for it to finish saving and fully exit before changing the DLL or applying manager updates. Make a [world backup](world-backups.md) before updating, as well as before the initial installation.

With the server stopped, replace the DLL in the same active location used during installation, preserve `heimkit.cfg`, and start with the same launch route. Stop the server before editing configuration too. A manually added DLL must be replaced manually; installing the manager does not enable automatic updates for that file.

## Configuration

HeimKit uses the configuration directory supplied by BepInEx. Existing values are preserved, and missing settings are added with defaults. The supplied [config/heimkit.cfg](../config/heimkit.cfg) is an optional reference; do not overwrite an existing server config with it.

```ini
[Torches]
Enabled = true
IntervalSeconds = 5
MaxMessagesPerPass = 32
Prefabs = piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch

[WeatherGuard]
Enabled = false

[Diagnostics]
DebugMode = false
```

Each feature has an independent Enabled switch. Stop the server, edit the appropriate value, save and restart. Startup reports ENABLED or DISABLED and any compatibility reason. Unsupported game versions disable the affected module.

DebugMode is the single troubleshooting switch. When false, logs contain general configuration, startup, readiness, warnings and errors. When true, they also contain bounded diagnostic details and status summaries. Diagnostic messages use Info level so normal BepInEx log filters display them.

## Troubleshooting and removal

If the plugin does not load, inspect `BepInEx/LogOutput.log` in the active loader location. For Option A, use the server profile's files and the manager's server launch; for Option B, use the dedicated-server folder's files and its startup script. Version warnings require API review before changing the guard. Logs report transmission attempts, not confirmed delivery. Remove credentials and join codes before sharing logs.

To disable a module, stop the server, set its Enabled value to false in the active `heimkit.cfg` and restart using the same launch route. To remove HeimKit, stop the server and remove its DLL from the active profile or manual server installation. Torch fuel remains ordinary saved data. WeatherGuard releases network ownership and does not persist a new protection flag or creator; existing custom world fields from other implementations are not automatically removed.
