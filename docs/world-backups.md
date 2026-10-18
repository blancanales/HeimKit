# World backups

Make an independent copy of the world before installing or enabling HeimKit, and keep that copy outside the active save folder. Back up the world on the computer that hosts it. Joining a dedicated server does not put its world save on your client computer, and a Thunderstore profile backup is not a world backup.

## Windows File Explorer

1. Stop the dedicated server gracefully and wait for it to finish saving and exit. For a server running in a command window, use **Ctrl+C**; for a managed server, use its normal stop control. For a locally hosted world, log out and close Valheim before copying its files.
2. Locate the active save directory. On the host computer, open File Explorer and paste this into its address bar:

   ```text
   %USERPROFILE%\AppData\LocalLow\IronGate\Valheim
   ```

   This is the default location for the Windows account running the game/server. If the server uses a different account or a custom `-savedir` argument, use that account's save directory or the configured path instead. A remote host must make the backup on the server machine or through its hosting control panel. The [official dedicated-server guide](https://valheim.com/support/a-guide-to-dedicated-servers/) documents the default path, custom save directory and normal shutdown.
3. Open `worlds_local`. For a custom save directory, look for `worlds_local` inside it. Older local saves can also be under `worlds`. If both exist, keep copies of both rather than guessing which contains the active world. The server's `-world` setting identifies its world name.
4. **Copy**, rather than cut or move, the entire world-save folder into a separate backup location. For example, create a dated folder under `Documents\ValheimBackups` and paste the copied `worlds_local` folder there. An additional copy on another drive protects against losing the original disk.
5. If copying only one world, preserve its complete save format: copy the entire world-named folder and all its contents for folder-based saves, or copy both matching `<world>.db` and `<world>.fwl` files for paired-file saves. Keep related backup files/folders too. Do not copy just one file from a folder-based save, mix files from different save times, or rename their contents.
6. Open the destination folder and confirm the copied files and subfolders are present before installing HeimKit. Keep the pre-installation copy separate from later backups so it cannot be replaced by a save made after enabling the mod.

## SharedMap data

SharedMap's community terrain is stored separately from Valheim's world save. If using SharedMap, stop the server and also copy the entire `HeimKit/SharedMap` directory from the active BepInEx configuration directory into the same dated backup. Manager profiles and manual installations can have different active configuration locations; see [server setup](server-setup.md#shared-configuration-and-maintenance).

Keep each `world-<world UID in hexadecimal>.map`, its matching `.portal-pins`, `.resources`, `.resource-pins` and `.pin-removals` files and their `.bak` files with the corresponding world backup. In-game Manage Saves and native world backup retention do not manage these plugin files. Restore the world and community files together from the same backup point; restoring an older world while keeping a later community map can reveal terrain from later progress. SharedMap rejects corrupt or incompatible community files rather than silently erasing them. See the [SharedMap guide](features/shared-map.md#storage-and-recovery).

## Valheim's in-game save management

The in-game menu manages worlds available to that game installation. It does not back up a remote dedicated server's world; use the host-side File Explorer instructions above for that world.

1. Leave the world and return to Valheim's main menu. Open **Manage Saves** and select the **Worlds** tab. Select the correct world and note whether it is stored locally or in the cloud.
2. For a cloud world that you want to copy through File Explorer, select the main world entry and choose **Move to Local**, then confirm the storage change. This moves the active save out of cloud storage; it is not an independent backup by itself. The developer's [save-location FAQ](https://steamcommunity.com/app/892970/discussions/0/4615641483004444994/) explains local/cloud storage and manual copies.
3. Exit Valheim, then follow the File Explorer steps to copy the local world into your separate backup location. A client and dedicated server must not write to the same world simultaneously.
4. To view existing game-created backups, expand the world's entry in **Manage Saves**. **Restore Backup** replaces the active world with the selected backup; it is a recovery action, not a way to create a fresh pre-installation copy. Do not restore or delete an entry merely to make a backup.

Valheim also maintains automatic backups. They are useful recovery options, but keep your independent pre-installation copy because automatic backups can rotate or be replaced. Steam Cloud synchronization likewise does not preserve an immutable pre-mod copy.

## Restoring your independent copy

Stop the server/game first and preserve the current save separately. Copy the complete backed-up world folder, or matching paired files, into the original active save location. Keep the same world name and restore one coherent copy rather than mixing its files with newer data. If the backup is listed in **Manage Saves**, its **Restore Backup** action is another recovery option for that locally accessible world.

Return to [server setup](server-setup.md#installation) after keeping your backup safe.
