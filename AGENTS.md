# HeimKit agent instructions

These instructions apply to the entire repository. Follow the user's current task and keep changes within its scope.

## Required reading before editing

1. Read [docs/architecture.md](docs/architecture.md) for file placement, naming, configuration, logging and feature lifecycle rules.
2. Read the relevant feature specification in [docs/specs](docs/specs). For current torch work, read [docs/specs/infinite-torches.md](docs/specs/infinite-torches.md).
3. Read [README.md](README.md) for the documented user behavior and validation status.
4. Inspect the current implementation and working tree before making changes. Preserve unrelated local changes.

The architecture document is the canonical project-wide design reference. Feature specs define behavior and acceptance criteria. Do not duplicate those rules in CLAUDE.md; that file only points here. If a spec conflicts with the code, identify the difference and resolve it within the user's authorized task rather than silently assuming the code is correct.

## Current scope

Work locally on the checked-out working branch. HeimKit currently implements infinite torches for Windows dedicated servers only. Wooden-torch refilling has passed an initial live test recorded in the spec; broader multiplayer validation remains pending. Do not infer additional in-game guarantees from compilation or expand support beyond recorded evidence.

Only add another feature when requested. Before implementing it, document its behavior and server-only feasibility in a new feature spec. Future ideas and incomplete functionality do not belong in the README's feature list.

## Implementation workflow

1. Use `src/HeimKit/Features/<FeatureName>/` for all files specific to that feature. Do not use the word `Feature` in class or interface names.
2. Keep plugin entry-point orchestration in `Plugin.cs`. Keep shared contracts/utilities in `Shared/` and configuration setup in `Configuration/`.
3. Give each module its own enable setting, startup state log, compatibility checks and isolated failure handling. Modules must work with other modules disabled; shared classes must not depend on another module being enabled.
4. Use the loader-provided configuration directory. Create `heimkit.cfg` automatically with all defaults when missing; preserve existing values and add missing settings. Keep the optional reference config synchronized with code defaults.
5. Build and run the relevant automated checks using the commands in the architecture document. Report build/check results separately from live multiplayer results.
6. Update the relevant spec when behavior changes. Add new feature descriptions to the README only when functionality works and has been verified; preserve honest test status for the existing torch implementation.

Do not install into or restart a live server, change worlds, remove the game-version guard, or change client installations as an incidental part of a code edit. Follow explicit user authorization for operational work.

Do not commit credentials, world saves, proprietary game assemblies, copied BepInEx runtime files, machine-specific reference paths, or generated build output. Inspect staged files before committing. Commit and push when requested; otherwise leave local edits reviewable.
