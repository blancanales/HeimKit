# HeimKit contributor instructions

These instructions apply to the entire repository. Follow the user's current task and keep changes within its scope.

## Required reading before editing

1. Read [docs/contributing.md](docs/contributing.md) for the procedure for creating and organizing features. Then read [docs/architecture.md](docs/architecture.md) for file placement, naming, configuration, logging and feature lifecycle rules.
2. Read the relevant feature specification in [docs/specs](docs/specs). For current torch work, read [docs/specs/infinite-torches.md](docs/specs/infinite-torches.md).
3. Read [README.md](README.md) for the overview and documentation links, then the relevant guide in [docs/features](docs/features) for user behavior and configuration. Read [docs/server-setup.md](docs/server-setup.md) and [docs/development.md](docs/development.md) when changing operational or build guidance.
4. Inspect the current implementation and working tree before making changes. Preserve unrelated local changes.

The architecture document is the canonical project-wide design reference. Feature specs define behavior and acceptance criteria. If a spec conflicts with the code, identify the difference and resolve it within the user's authorized task rather than silently assuming the code is correct.

Keep user-facing safety, sampling, inference, replication and delivery caveats in [docs/limitations.md](docs/limitations.md); link to that page instead of repeating disclaimers in every feature/subfeature. Keep feature scope and implementation rules in their owning guides/specifications.

Keep shared documentation independent of any particular assistant, vendor or tool. Tool-specific entry files contain only a pointer to this root AGENTS.md file; project rules belong here and in the referenced documentation.

## Current scope

Work locally on the checked-out working branch. HeimKit targets Windows dedicated servers only. The source contains InfiniteTorches, WeatherGuard, AFKNightWatch and AFKRaidWatch, plus SharedMap persistent community exploration, table initialization and map-field filtering. SharedMap optionally adds player-built portal markers, outdoor metal/Obsidian deposit markers, allowlisted exterior dungeon entrance markers, lore runestone landmarks and persistent recurring spawner markers; other location/resource families remain outside scope. Resource markers exclude scraps, dungeon resources and creature sources. Do not infer gameplay guarantees from compilation.

WeatherGuard priority: automatic weather protection for player-created structures first, preserving their existing creator. Claiming/protecting individual repaired world pieces is secondary. A nonzero creator makes a player-built piece eligible for protection; it must not be used as a protection exclusion. Require seamless activation during play, one normal Enabled switch, and unchanged combat/support behavior. Do not require logout-based activation. World-created claiming is not part of the current implementation.

Only add another feature when requested. Before implementing it, document its behavior and server-only feasibility in a new feature spec. Future ideas and incomplete functionality do not belong in the README's feature list. Maintain one current specification per feature; do not create development journals, abandoned-design histories, task-tracking Markdown files or completion checklists. Use README.md to introduce the application, feature benefits and user expectations, compatibility and links to detailed documentation as defined in [docs/contributing.md](docs/contributing.md#update-documentation). List all supported version values only in [docs/server-setup.md](docs/server-setup.md#supported-environment); every other document links there without repeating version values. Put build commands in docs/development.md. Never persist testing instructions, test results or validation-status notes in documents; provide them directly to the user in the active chat, following [docs/contributing.md](docs/contributing.md#testing-information-belongs-in-chat-only). Feature specs describe compatibility behavior without repeating supported version numbers.

## Implementation workflow

### AI action authorization

These instructions govern AI agents working in this repository. Human contributor and development guides describe procedures; they do not authorize an AI agent to perform those actions.

- Do not create a build unless Trevor explicitly requests a build. This applies to blank scaffolds, functional code changes, fixes and documentation work. An instruction to add or implement a feature does not itself request a build. Do not trigger compilation indirectly through automated checks, packaging or another tool.
- Do not increment application versions, change build metadata, prepare release notes or create release artifacts merely because source code changed. Perform the version-and-documentation procedure only when preparing an explicitly requested build or release.
- Leave edits local and reviewable unless Trevor explicitly requests a commit or push. Creating a requested GitHub branch from an existing commit does not authorize committing or pushing subsequent edits. Packaging, publishing, installation and server restarts require their own explicit scope.
- Approval of a tool-access prompt is not a request to expand the task into building or publishing. Establish authorization from Trevor's task instructions before requesting tool access.
- A blank feature addition includes only the requested source structure, independent settings, registration, compatibility and player-service lifecycle, with accurate scope documentation. Keep it disabled by default and do not imply gameplay functionality exists.

### Source changes and explicitly requested builds

1. Use `src/HeimKit/Features/<FeatureName>/` for all files specific to that feature. Do not use the word `Feature` in class or interface names.
   Name feature-owned behavior classes `<FeatureName><ResponsibilityOrAction>`. Model and enum prefixes are optional guidance; prefer descriptive names. Display the chosen feature name exactly, without appended development labels. Follow the architecture naming rule; document limitations separately from the name.
2. Keep plugin entry-point orchestration in `Plugin.cs`. Keep shared contracts/utilities in `Shared/` and configuration setup in `Configuration/`.
3. Follow the architecture’s required player-service hookup for every feature and document it in the feature spec. Only explicitly specified offline/server responsibilities in the architecture’s four exception categories may run independently of presence. Give each module its own enable setting, startup state log, compatibility checks and isolated failure handling. Modules must work with other modules disabled; shared classes must not depend on another module being enabled.
4. Use the loader-provided configuration directory. Create `heimkit.cfg` automatically with all defaults when missing; preserve existing values and add missing settings. Keep the optional reference config synchronized with code defaults. Put all shared/non-feature sections before feature sections in generated and reference configuration; no relative feature order is required. Follow the architecture configuration-layout contract when adding shared sections. The documented hidden DisableDebugLogging override is manual-only: preserve supplied values without generating absent keys; all features use the shared suppression resolver.
5. For build and release work, follow the [AI versioning policy](docs/ai/versioning.md) and its linked preparation procedure. Builds still require Trevor's explicit request.
6. Update the relevant spec and user guide when behavior changes. For logging changes, follow the architecture standard-event/grouping contract: document which observed results belong in normal output, which evidence stays in Debug, and the logical boundary used to combine repeated results. Preserve immediate failures/lifecycle transitions and distinct player identities. README feature entries explain user benefits, high-level behavior and essential scope, with guide/specification links. Include a concise compatibility overview; detailed configuration, implementation and operational instructions belong under docs. Follow the README content rules in docs/contributing.md. Keep testing instructions, results and evidence requests in chat only.

Do not install into or restart a live server, change worlds, remove the game-version guard, or change client installations as an incidental part of a code edit. Follow explicit user authorization for operational work.

Do not commit credentials, world saves, proprietary game assemblies, copied BepInEx runtime files, machine-specific reference paths, or generated build output. Inspect staged files before committing. Commit and push when requested; otherwise leave local edits reviewable.

For every Thunderstore package, update the root CHANGELOG.md with concise user-facing release notes, newest version first. The current heading must match the package version and include meaningful change bullets. Publish-Thunderstore.ps1 must require and include this file at the ZIP root. This release changelog is separate from development journals; keep testing instructions, results and evidence out of it.
