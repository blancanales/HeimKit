# HeimKit

HeimKit brings quality-of-life features to Windows Valheim dedicated servers, helping server owners reduce upkeep, coordinate night skipping and limit automatic raids around idle players.

Install HeimKit on the server and choose the features you want independently. It is designed for players using unmodified clients, with configuration managed by the server. The project is under local development; no production release is advertised.

## World safety

**HeimKit can corrupt a world or cause loss of progress. Back up your world before installing or enabling the mod.** See the [world backup guide](docs/world-backups.md) for Windows File Explorer and Valheim's in-game save-management options.

## Features

### Infinite Torches

Keeps eligible torches supplied with fuel automatically, reducing manual refilling without spending player inventory materials. Normal water, smoke and obstruction behavior still applies, so fuel alone does not guarantee a lit torch.

[Usage and supported torch types](docs/features/infinite-torches.md) · [Technical specification](docs/specs/infinite-torches.md)

### WeatherGuard

Designed to prevent rain and wetness wear on eligible existing and newly player-built structures while preserving their original creators. Protection activates automatically during play without requiring a repair, logout or rejoin. Combat damage, repair, removal and structural support retain their normal gameplay roles. World-generated structures are outside its current scope.

[Usage and protection scope](docs/features/weather-guard.md) · [Technical specification](docs/specs/weather-guard.md)

### MajoritySleep

Lets the night advance when a configurable share of players is in bed, rather than requiring everyone. The default is more than half. Normal bed requirements and native night-skip behavior remain.

[Usage](docs/features/majority-sleep.md) · [Technical specification](docs/specs/majority-sleep.md)

### AfkSleepExclusion

Keeps players inferred idle from blocking sleep. It works independently of majority sleep and uses configurable server-observed activity, with conservative treatment of loading and uncertain players.

[Usage and activity limits](docs/features/afk-sleep-exclusion.md) · [Technical specification](docs/specs/afk-sleep-exclusion.md)

### AfkRaidProtection

Helps protect areas occupied only by players inferred idle from new automatic raids. Active nearby players keep normal raid eligibility, and existing raids continue. Idle inference cannot observe every local menu action.

[Usage and scope](docs/features/afk-raid-protection.md) · [Technical specification](docs/specs/afk-raid-protection.md)

## Compatibility

- Windows Valheim dedicated servers; Linux is not supported.
- Requires BepInExPack Valheim and a compatible game version. See the [supported environment](docs/server-setup.md#supported-environment) for version requirements. Unsupported game versions disable the affected features.
- Designed for unmodified clients; HeimKit is installed on the dedicated server.

See [server setup](docs/server-setup.md) for installation and configuration.

## Setup

Follow the [setup guide](docs/server-setup.md#installation) to install with Thunderstore Mod Manager or manually, including manual installation when the manager is already installed.

## Documentation

- [Server setup](docs/server-setup.md) — supported environment, installation, configuration and general troubleshooting.
- [World backups](docs/world-backups.md) — copying world saves and using Valheim's save-management menu.
- [Development](docs/development.md) — build commands and plugin/Thunderstore references.
- [Contributing](docs/contributing.md) — change procedure and documentation placement rules.
- [Architecture](docs/architecture.md) — code organization and shared design rules.
- [Contributor instructions](AGENTS.md) — required reading and project scope.

## License

HeimKit uses the [MIT License](LICENSE). Game files and dependencies retain their own licenses and are not distributed or relicensed by this project.

## Disclaimer

Use HeimKit at your own risk. This mod can corrupt your world or cause loss of progress and data. We are not responsible for world corruption, data loss or other damage resulting from its use. Back up your world before installing or using the mod; see the [world backup guide](docs/world-backups.md).
