# HeimKit

![HeimKit: shield and longhouse emblem beside the HeimKit name](docs/images/heimkit-banner.jpg)

Quality-of-life features for Windows Valheim dedicated servers: infinite torches, shared map exploration and markers, weather protection, majority sleep and AFK raid protection.

Install on the server. Players use unmodified Valheim clients. Choose each feature independently in `heimkit.cfg`.

Before enabling HeimKit, follow [world backups](docs/world-backups.md) and read [safety and limitations](docs/limitations.md).

## Features

![HeimKit features: torch upkeep, weather protection, majority sleep, AFK raid protection and SharedMap with portal, resource, dungeon, runestone and recurring-spawner markers](docs/images/feature-overview.jpg)

### InfiniteTorches

Automatically refills eligible torches and hanging/standing braziers without spending inventory fuel. Optional campfire, hearth and bonfire refilling defaults off, with separate controls for each fire type.

[Usage and settings](docs/features/infinite-torches.md) · [Specification](docs/specs/infinite-torches.md)

### WeatherGuard

Protects existing and newly player-built structures from rain and underwater wear, preserving their creators. Activation is automatic during play. Combat, repair and structural support stay native; world-created claiming is excluded.

[Usage and settings](docs/features/weather-guard.md) · [Specification](docs/specs/weather-guard.md)

### AFKNightWatch

Combines configurable majority sleep with idle-player exclusion. A grace period gives others time to reach bed; everyone in bed can start immediately.

[Usage and settings](docs/features/afk-night-watch.md) · [Specification](docs/specs/afk-night-watch.md)

### AFKRaidWatch

Excludes idle-only locations from new automatic random raids. Nearby active or uncertain players retain normal eligibility. Sleep and raid controls are independent.

[Usage and settings](docs/features/afk-raid-watch.md) · [Specification](docs/specs/afk-raid-watch.md)

### SharedMap

Saves community exploration and discoveries across restarts, independently of cartography tables. Players choose **Read Map** at a vanilla table to receive updates. Shared death pins are filtered.

Five independent marker options default enabled:

- **PortalMarkerSync:** named player-built portals, with optional nearby grouping.
- **ResourceMarkerSync:** outdoor metal/Obsidian deposits, Tin node counts and Copper/Silver progress. Silver requires pickaxe discovery.
- **DungeonMarkerSync:** labeled exterior entrance markers with the vanilla hammer icon and cleanup of nearby duplicate dungeon pins. Supported pickup percentages appear after observed entry or evidence of already-picked items.
- **RunestoneMarkerSync:** placed lore-runestone landmarks as labeled dots.
- **SpawnerMarkerSync:** visible, player-destructible recurring spawner objects as labeled dots, removed after confirmed source destruction.

Optional settings discover resources/dungeons in previously explored terrain or reject player map uploads while automatic collection continues. Enabled runestone/spawner markers automatically include eligible points in community-explored terrain. Optional shared X checkmarks default off. Server owners can remove a selected shared pin through server-side administration without clearing other pins or exploration.

[Usage, settings and administration](docs/features/shared-map.md) · [Specification](docs/specs/shared-map.md)

## Setup and compatibility

Windows dedicated servers with BepInExPack Valheim are supported. See [supported environment](docs/server-setup.md#supported-environment) for exact requirements and [safety and limitations](docs/limitations.md) for gameplay/client constraints.

![Server installation: HeimKit and BepInEx belong on the dedicated server; clients remain unmodified](docs/images/server-installation.jpg)

Follow [server setup](docs/server-setup.md#installation) for Thunderstore Mod Manager or manual installation. Gameplay processing pauses when the server is empty and resumes after a player joins; see [feature lifecycle](docs/server-setup.md#feature-lifecycle).

## Documentation

- [Server setup](docs/server-setup.md): installation, configuration and diagnostics.
- [Safety and limitations](docs/limitations.md): world risks, server observations and client limitations.
- [World backups](docs/world-backups.md): matching world and SharedMap backups/recovery.
- [SharedMap](docs/features/shared-map.md): exploration, marker options, uploads and pin administration.
- [Development](docs/development.md): build and packaging commands.
- [Architecture](docs/architecture.md): shared design and lifecycle contracts.
- [Contributing](docs/contributing.md): source/documentation procedures.
- [Contributor instructions](AGENTS.md): task scope and authorization.

## License

[MIT](LICENSE). Game and dependency assets retain their own licenses.
