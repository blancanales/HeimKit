# HeimKit

![HeimKit: shield and longhouse emblem beside the HeimKit name](docs/images/heimkit-banner.jpg)

Quality-of-life features for Windows Valheim dedicated servers: infinite torches, shared map exploration and markers, weather protection, majority sleep, AFK raid protection, tame portal travel, door closing, tree renewal, sunken-loot recovery and gradual building repairs.

Install on the server. Players use unmodified Valheim clients. Choose each feature independently in `heimkit.cfg`.

The HeimKit performance rebuild shares world discovery, prefab metadata and scheduling through a common Core. It retains the same features, settings and data paths. See the [rebuild specification](docs/ai/heimkit-rebuild-spec.md) and [development guide](docs/development.md) for its temporary source layout. Run only one implementation on a server.

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

### DoorKeeper

Closes eligible player-built doors and gates after a delay once players clear the doorway. Native manual access remains; keyed and special doors are excluded. Defaults enabled.

[Usage and settings](docs/features/door-keeper.md) · [Specification](docs/specs/door-keeper.md)

### TameTravel

Brings explicitly following or portal-waiting tamed wolves, boars, lox, asksvin, hens and moose along a validated trip between linked outdoor portals. Preserves the same animal and its saved state. One follow command covers successive portal trips and following continues at the destination until stopped. Follow, stay and optional portal-wait commands have vanilla emote fallbacks. Explicit followers can use configurable stopping distance through bounded server control. Defaults enabled.

[Usage and settings](docs/features/tame-travel.md) · [Specification](docs/specs/tame-travel.md)

### ForestRenewal

Renews supported trees from stumps left behind or their confirmed-cleared locations, using vanilla saplings, renewal chance, delay, terrain/building exclusions and persisted cooldowns. Native growth requirements remain. Defaults enabled.

[Usage and settings](docs/features/forest-renewal.md) · [Specification](docs/specs/forest-renewal.md)

### SeaSalvage

Recovers eligible stable sunken item drops into ordinary floating cargo crates while preserving native item data. Active client-owned and unsupported drops wait or remain untouched. Defaults enabled.

[Usage and settings](docs/features/sea-salvage.md) · [Specification](docs/specs/sea-salvage.md)

### HeimMender

Gradually repairs damaged player-built structures with configurable cycles, chance and limits. Administrators can request nearby repairs, and its settings reload during play. Defaults enabled.

[Usage and administration](docs/features/heim-mender.md) · [Specification](docs/specs/heim-mender.md)

## Setup and compatibility

Windows dedicated servers with BepInExPack Valheim are supported. See [supported environment](docs/server-setup.md#supported-environment) for exact requirements and [safety and limitations](docs/limitations.md) for gameplay/client constraints.

![Server installation: HeimKit and BepInEx belong on the dedicated server; clients remain unmodified](docs/images/server-installation.jpg)

Follow [server setup](docs/server-setup.md#installation) for Thunderstore Mod Manager or manual installation. Gameplay processing pauses when the server is empty and resumes after a player joins; see [feature lifecycle](docs/server-setup.md#feature-lifecycle).

## Documentation

- [Server setup](docs/server-setup.md): installation, configuration and diagnostics.
- [Safety and limitations](docs/limitations.md): world risks, server observations and client limitations.
- [World backups](docs/world-backups.md): matching world and SharedMap backups/recovery.
- [SharedMap](docs/features/shared-map.md): exploration, marker options, uploads and pin administration.
- [DoorKeeper](docs/features/door-keeper.md): delayed door closing.
- [ForestRenewal](docs/features/forest-renewal.md): optional sapling renewal.
- [SeaSalvage](docs/features/sea-salvage.md): floating recovery cargo.
- [TameTravel](docs/features/tame-travel.md): explicit followers through linked outdoor portals.
- [HeimMender](docs/features/heim-mender.md): gradual and administrative building repairs.
- [Development](docs/development.md): build and packaging commands.
- [Architecture](docs/architecture.md): shared design and lifecycle contracts.
- [Contributing](docs/contributing.md): source/documentation procedures.
- [Contributor instructions](AGENTS.md): task scope and authorization.

## License

[MIT](LICENSE). Game and dependency assets retain their own licenses.
