# Windows client setup

Install HeimKit in the Windows game to run its gameplay features in a world you host. When you join someone else’s world, you can use automatic personal map markers even on a vanilla server. On a compatible HeimKit dedicated server, F7 also shows server settings. For loader and game requirements, see the [supported environment](server-setup.md#supported-environment).

## Install in the game

1. Install the supported [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) if needed. With a mod manager, install it in the **Valheim game** profile you play with and open that profile’s folder. For a manual installation, copy the contents of the pack’s `BepInExPack_Valheim` folder beside `valheim.exe`.
2. Place one copy of `HeimKit.dll` in that installation’s `BepInEx/plugins` directory.
3. Launch Valheim through that BepInEx installation. Load a world or join a server, then follow the [HeimKit Settings guide](heimkit-settings.md).

To install HeimKit on a dedicated server, follow [server installation](server-setup.md#installation).

## Host a world from Valheim

Start a private world for solo play or select **Start Server** when opening the world to let guests join. The Windows game runs HeimKit’s gameplay modules in both modes. The player who starts the world can manage its settings in F7. Guests can join through vanilla Valheim and use the supported gameplay features. F7 administration for the hosted world belongs to its host.

HeimKit creates settings for each world on first use and preserves existing values. A single world uses the same HeimKit configuration in private and **Start Server** modes. Valheim continues to manage its world saves.

## Configuration locations

Paths are relative to the active game, server or mod manager profile.

| File | What it controls |
| --- | --- |
| `BepInEx/config/HeimKit/Worlds/<world-id>/heimkit.cfg` | Gameplay settings for one player-hosted world. |
| `BepInEx/config/heimkit.cfg` | Gameplay and remote admin settings on a dedicated server. |
| `BepInEx/config/heimkit.client.cfg` | This game installation’s menu shortcut, automatic map markers and local diagnostic preferences, across worlds and servers. |

For a manual gameplay edit, leave your hosted world or stop the dedicated server, edit its `heimkit.cfg`, then reopen the world or restart the server. HeimMender also supports live file edits as described in its [feature guide](features/heim-mender.md). Changes made in F7 follow the timing shown on the control.

## Server administration

Use the [HeimKit Settings guide](heimkit-settings.md#change-a-setting) for in-game changes. Dedicated server owners can manage remote access through [dedicated server access to HeimKit Settings](server-setup.md#dedicated-server-access-to-heimkit-settings).
