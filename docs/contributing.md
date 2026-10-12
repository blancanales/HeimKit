# Contributing to HeimKit

Use this guide when adding or changing code. Read the relevant feature specification and inspect the working tree first. Preserve unrelated local edits. Architecture explains the design; this guide explains how to work within it.

## Development references

Use the [plugin development and Thunderstore references](development.md#plugin-development-and-thunderstore-references) for BepInEx plugin setup, Valheim modding examples, Harmony patch semantics and package requirements. Consult the relevant guide before introducing a new patch mechanism or changing loader integration. Follow this repository's [supported environment](server-setup.md#supported-environment) and build commands when tutorial examples use different dependencies or framework targets.

For gameplay or networking changes, inspect the actual methods and ownership paths in the installed supported game assemblies. Record implemented behavior and material limitations in the current feature specification. External guides supplement this procedure; they do not prove dedicated-server feasibility or live correctness.

## Before every build

Version and documentation preparation is a required part of building, including local builds supplied for gameplay use. Complete it before invoking the build command.

1. Determine whether code has changed since the last identified build. Assign an updated build a new HeimKit version, normally the next patch number unless the requested release calls for a different increment. Rebuilding identical code retains its existing version; changed code must not be supplied under that previous number.
2. Synchronize `<Version>` in `src/HeimKit/HeimKit.csproj`, the BepInPlugin version and startup message in `src/HeimKit/Plugin.cs`, and the HeimKit build row in [Supported environment](server-setup.md#supported-environment). These all identify the same build. Leave the game compatibility guard unchanged unless a compatibility change is explicitly within scope.
3. Update the affected feature specifications and user guides for the behavior and configuration included in that version. Update the architecture, contributor procedures and reference config when their contracts or defaults change. Update README benefits/scope when needed, using links for detailed facts and version values. Documentation must describe the code about to be built, rather than an earlier build.
4. Build only after the version and applicable documentation are synchronized. When delivering the artifact, state its exact HeimKit version and location in the active chat so the user can distinguish it from previous builds. Build/test results and live-testing instructions stay in chat; do not create release journals, validation records or task checklists in the documentation.

## Set up a feature

Add a feature only when requested. Establish its intended behavior and dedicated-server-only feasibility before writing gameplay code. Inspect the actual game APIs and ownership behavior; identify blockers for unmodified clients. Keep the feature specification current as implementation proceeds, without a task diary or completion checklist.

1. Create `src/HeimKit/Features/<FeatureName>/`. Use the chosen feature name, without development labels or the word Feature in class names.
2. Create `<FeatureName>Settings.cs` first. Bind the feature's section, Enabled switch, defaults, descriptions and validation ranges. Use DiagnosticsSettings for global DebugMode. Bind a visible DebugMode setting defaulting false in the feature section, including disabled features, and pass both switches to DebugLoggingResolver for the hidden suppression override. Settings classes define values; they do not load or save separate files.
3. Create `<FeatureName>.cs`, implementing IServerModule. Construct its settings before initialization. Construction binds configuration only; defer world processing, subscriptions and patch installation until enabled, compatible and ready. Keep the main class responsible for settings construction, compatibility, lifecycle, startup state and failure isolation. Put scanning, selection, network operations and other gameplay processing in supporting classes. For ongoing processing, use `<FeatureName>Controller.cs`; the main class activates, ticks and disposes it, and the controller reports processing failures back to the main class.
4. Create `Models/` when structured data types or enums are needed. Use descriptive model and enum names; a feature prefix is optional when it improves clarity or avoids ambiguity. Place each named type in a matching file with namespace `HeimKit.Features.<FeatureName>.Models`. Do not create placeholder models. Keep gameplay behavior in supporting classes.
5. Add supporting classes only for actual responsibilities, such as diagnostics, processing, rules or network filtering. Name feature-owned behavior classes `<FeatureName><ResponsibilityOrAction>`, and match their filenames. Avoid vague suffixes such as Helper, Manager or Utilities. A fixed collection of helper files is not required. For recurring troubleshooting output, use `<FeatureName>Diagnostics.cs` to own debug gating, summary/sample formatting, throttling and reset. Controllers own operational counters and pass data to diagnostics; diagnostics must not scan or modify gameplay state. Keep startup, readiness, warnings and errors where those events occur. Features without recurring diagnostic behavior do not need an empty diagnostics class. Shared class names describe their reusable responsibility without a feature prefix. Keep feature-specific helpers in the feature folder. Move code into Shared when multiple implemented features actually use it, or when it defines an application-wide contract such as the module lifecycle or logging format. Potential future reuse alone is not a reason to place feature-specific code in Shared.
6. Register the main class in Plugin.cs inside the ConfigurationLoader callback. Bind settings for disabled features too. The central loader loads heimkit.cfg, preserves existing values, adds missing defaults and saves the complete file before initialization. Keep startup failure isolation between modules.
7. In Initialize, check Enabled and compatibility before activating gameplay code, and log the effective ENABLED or DISABLED state with a reason. In Tick, respect readiness and scheduling. Shutdown must stop work and release resources safely, including repeated calls.
8. Update config/heimkit.cfg to match implemented defaults and place all shared sections before feature sections. Register new shared section names in ConfigurationLayout; feature sections have no required relative order. Users must never need to create the file themselves.

The current main classes construct their settings internally during the central loader callback. This preserves the existing registration flow; settings binding still happens before configuration saving and module initialization. ConfigurationLoader remains responsible for the shared file, and DiagnosticsSettings defines the common troubleshooting switch.

## Integrating player presence and AFK inference

Use the shared PlayerProcessingNotices for each module: acknowledge an empty-server pause only after its controller cleanup completes, allowing the shared service to combine confirmed pauses into one notice. Announce Ready once when required dedicated-server services become available, independently of joined players. This server status check must not keep gameplay hooks, scanning or activity leases active while empty. Emit individual processing activation/pause/recovery only at Debug through the effective feature policy. PlayerPresence owns the normal active/inactive notices and waits for all startup Ready entries before its initial status. Gameplay follows joined-world sessions, never a mere connection or login handshake.

Follow the [shared-service integration contract](architecture.md#integrating-a-consumer). Obtain the Plugin-owned PlayerServices through constructor injection; do not construct a feature-owned hub or another routed observer. Use HasJoinedPlayers/JoinedCount from its IPlayerPresence snapshot for gameplay occupancy, and ConnectedCount/ReadyCount only for transport/handshake evidence and treat null population as unknown. Loading, missing/nonfinite data and death cannot establish idle protection. Read Service.Reliable after Snapshot and retain native behavior on unavailable observations.

Every feature must follow the [required player-service hookup](architecture.md#required-feature-hookup) for ordinary processing. Document a `Player-service integration` section in its spec before implementation, including resource ownership, presence transitions, native readiness, AFK lease requirements and any explicit empty-server exception. Only the four responsibilities defined in the [lifecycle contract](architecture.md#presence-driven-lifecycle) can remain independent; server execution or a timer alone is not an exemption. Keep exempt work separate from gameplay controllers. Use the same standard procedure for subscription, replay, pause/resume and cleanup across features.

Acquire one activity lease only when an enabled behavior needs AFK inference. Keep majority sleep without exclusion independent of tracking. Use the service's cached observations rather than collecting feature-local samples; required inference continues during daylight regardless of diagnostic switches. Release the lease on rollback, world-readiness loss, failure and shutdown, including repeated shutdown. Stop only the failing feature's own resources. The final lease releases shared hooks and stale inference; Plugin owns final cleanup.

Ordinary AFK entry/exit notices belong to PlayerActivityService and follow the [shared AFK notice contract](architecture.md#ordinary-afk-notices). Do not gate them on debugging or duplicate them in consuming features. Preserve player identity, current counts and explicit tracking-loss reasons.

Supply a live effective debug-policy callback with the lease. Shared PlayerActivity diagnostics owns actor transitions once; each feature owns only its decision/outcome evidence. A consumer can permit shared activity diagnostics without enabling another feature's output. Hidden suppression, empty-server pause and sampling budgets govern diagnostic collection, never gameplay. Shared detection binding belongs under Configuration, and feature settings contain only their own behavior. Add no legacy aliases or converters when extending this fresh layout.

## Update documentation

### Main README

The root `README.md` introduces and promotes HeimKit for prospective users and server owners. It should explain the application's value, what users can expect and where to find details. Include:

- The project name, purpose and practical benefits for users.
- A high-level introduction to each feature: what it does, the user-facing benefit, expected behavior and the essential scope or limitations. A short paragraph is appropriate; feature descriptions are not restricted to one sentence.
- A concise compatibility overview covering the supported platform, required loader and client installation expectations, linked to [Supported environment](server-setup.md#supported-environment). Do not repeat version values in the README.
- Links from each feature to its usage guide and technical specification under `docs/`.
- A concise world-corruption disclaimer requiring a backup before installing or enabling the mod, linked to `docs/world-backups.md` for the backup procedure.
- A general usage disclaimer at the bottom of the README stating that the mod can corrupt worlds and that we are not responsible for resulting damage or data loss. Put specific corruption risks and prevention instructions in the relevant operational document, not in that general disclaimer.
- A documentation index with short descriptions and links.
- License information.

Keep the README focused on the user's decision to use HeimKit. Put detailed configuration blocks, setting tables, eligibility rules, algorithms, implementation/class responsibilities, diagnostic fields or sample logs, installation/build commands, full dependency/runtime version tables, troubleshooting steps and external development reference lists in the documents below and link to them. High-level feature expectations and essential limitations belong in the README; their technical explanation belongs under `docs/`. Testing information follows the chat-only rule below.

### Detailed documents

| Content | Destination |
| --- | --- |
| Supported game/loader/runtime versions, installation, shared configuration, general troubleshooting and removal | `docs/server-setup.md` |
| World-save backup and recovery instructions for File Explorer and in-game save management | `docs/world-backups.md` |
| Build commands, development environment and external BepInEx/Valheim/Harmony/Thunderstore references | `docs/development.md` |
| Feature usage, user-facing limitations and diagnostic semantics | `docs/features/<feature-name>.md` |
| Implemented feature behavior, configuration contract, eligibility, algorithms, ownership/network rules, bounds and source responsibilities | `docs/specs/<feature-name>.md` |
| Shared design, code placement, naming, lifecycle and documentation ownership | `docs/architecture.md` |
| Contributor workflow and the README/content placement rules | `docs/contributing.md` |
| Required-reading pointers, authorized scope and operational boundaries | `AGENTS.md` |

Keep one current guide and one current specification per feature. Guides explain how to use a feature; specifications define its implemented behavior. Maintain each detailed fact in its owning document and link from other documents rather than copying it. Update architecture when the design changes and this guide when contributor procedures change. Keep shared documentation independent of AI tools or vendors.

Complete the [version and documentation procedure](#before-every-build) before invoking a build.

### Supported versions have one source

List supported application, game, loader, runtime and framework versions only in [Supported environment](server-setup.md#supported-environment). Every other document must link to that section instead of repeating exact versions, version ranges, loader major versions or framework target values. This applies to the README, feature guides, specifications, development references and contributor instructions. When compatibility changes, update this one documentation source along with the relevant code guards or build definitions. Keep required version constants in executable code; this rule governs documentation.

Do not create development journals, task-tracking Markdown, completion checklists or abandoned-design histories. Do not commit generated output, proprietary assemblies, worlds, credentials or machine-specific paths. Leave local changes reviewable; commit and push only when requested. Installing or restarting a live server requires explicit authorization.


### Testing information belongs in chat only

Never add testing information to persistent documents, including README files, guides, specifications, contributor documents or development references. Do not store test procedures, test commands, scenarios, expected test results, pass/fail reports, validation-status notes, test evidence requests or testing checklists in those documents. Do not move that information to another file or an appendix.

The assistant gives the user it is communicating with the relevant testing instructions directly in the active chat, including what to test, expected behavior and what evidence to return. Report automated results and observed gameplay outcomes in chat, clearly distinguishing observations from inference. This documentation rule does not remove executable test code or change the need to check implementation changes. Persistent documents describe behavior, configuration, design, operational usage and diagnostic semantics.

## Shared logging format

For standard output, follow the [feature-event and grouping contract](architecture.md#standard-feature-events-and-grouping). Define meaningful results in the feature spec, collect operational counts independently of debug settings, and emit one count at the logical operation boundary. Keep empty/unchanged batches quiet and clear pending batches on pause, failure and shutdown. Preserve immediate first-failure/lifecycle notices and distinct actor or failure identities.

Use the [architecture logging contract](architecture.md#logging) for every HeimKit-owned message. Emit through `Shared/DiagnosticLog` and preserve severity. Info and Warning use `Feature: Message`, with concise status, impact and useful action. Debug, Error and exception details use an explicit event category followed by the result and named evidence fields with units and observation scope. Keep startup settings, hooks, schema and metrics at Debug. Keep unavailable values distinct from zero; do not claim an unobserved cause or outcome. Apply the [repetition and volume limits](architecture.md#repetition-and-volume-limits): state changes rather than polling output, semantic deduplication, quiet empty scopes, compact heartbeats and independent repeated-incident limits. Preserve first failure visibility and gameplay counters when repeated output is suppressed.

Feature Diagnostics classes own optional output, formatting decisions and bounded streams using `Shared/DiagnosticBudget`. Controllers continue collecting operational measurements when samples are suppressed. Route all recurring messages, including pending-operation outcomes, through these limits. Every current and future feature must resolve `DisableDebugLogging` in its existing feature section through `Configuration/DebugLoggingResolver` and pass the effective policy to its controller and Diagnostics class. Missing/false permits global OR feature DebugMode; true suppresses only that feature’s optional output and expensive diagnostic collection. A visible feature DebugMode default false can enable diagnostics with global debug off. Hidden suppression always takes precedence. Do not Bind the hidden key unconditionally, generate it in default/reference configuration, erase manual values, or substitute feature enablement for debug suppression. Preserve standard lifecycle, readiness, warnings and errors. Apply effective gating before snapshots, probes, history updates and formatting, while retaining gameplay-required state. Invalid values suppress diagnostics and warn once per feature/configuration. Document feature use by linking to the [configuration contract](server-setup.md#hidden-per-feature-debug-suppression); keep restart-required semantics.

Current, pass, interval and lifetime counts must be labeled distinctly. Document field semantics in the owning specification and shared format changes in architecture; the README retains only high-level feature information.

Apply the shared [empty-server debug pause](architecture.md#empty-server-debug-pause) to automatic diagnostics. Use the live effective policy for collection and output; constructors that capture configured eligibility must also consult the shared activity gate on each diagnostic opportunity so reconnecting restores output. Never use the Debug pause to stop gameplay processing. Gameplay follows the separate player-service lifecycle; warnings, errors and requested reports remain available.

Explicit local admin reports must remain separate from automatic diagnostics: authorize access through the documented server mechanism, gather data only on deliberate request, report bounded scope and unknown/unloaded state honestly, and preserve debug suppression. Configuration exclusions must use exact identifiers and existing eligibility/creator rules; channel options retain native combat, wetness and support behavior. Operational command documentation belongs in the owning guide; live-validation instructions and results remain chat-only.
