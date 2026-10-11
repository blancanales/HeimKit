# Contributing to HeimKit

Use this guide when adding or changing code. Read the relevant feature specification and inspect the working tree first. Preserve unrelated local edits. Architecture explains the design; this guide explains how to work within it.

## Set up a feature

Add a feature only when requested. Establish its intended behavior and dedicated-server-only feasibility before writing gameplay code. Inspect the actual game APIs and ownership behavior; identify blockers for unmodified clients. Keep the feature specification current as implementation proceeds, without a task diary or completion checklist.

1. Create `src/HeimKit/Features/<FeatureName>/`. Use the chosen feature name, without development labels or the word Feature in class names.
2. Create `<FeatureName>Settings.cs` first. Bind the feature's section, Enabled switch, defaults, descriptions and validation ranges. Use the shared DiagnosticsSettings binding for the single DebugMode switch. Settings classes define values; they do not load or save separate files.
3. Create `<FeatureName>.cs`, implementing IServerModule. Construct its settings before initialization. Construction binds configuration only; defer world processing, subscriptions and patch installation until enabled, compatible and ready. Keep the main class responsible for settings construction, compatibility, lifecycle, startup state and failure isolation. Put scanning, selection, network operations and other gameplay processing in supporting classes. For ongoing processing, use `<FeatureName>Controller.cs`; the main class activates, ticks and disposes it, and the controller reports processing failures back to the main class.
4. Create `Models/` when structured data types or enums are needed. Use descriptive model and enum names; a feature prefix is optional when it improves clarity or avoids ambiguity. Place each named type in a matching file with namespace `HeimKit.Features.<FeatureName>.Models`. Do not create placeholder models. Keep gameplay behavior in supporting classes.
5. Add supporting classes only for actual responsibilities, such as diagnostics, processing, rules or network filtering. Name feature-owned behavior classes `<FeatureName><ResponsibilityOrAction>`, and match their filenames. Avoid vague suffixes such as Helper, Manager or Utilities. A fixed collection of helper files is not required. For recurring troubleshooting output, use `<FeatureName>Diagnostics.cs` to own debug gating, summary/sample formatting, throttling and reset. Controllers own operational counters and pass data to diagnostics; diagnostics must not scan or modify gameplay state. Keep startup, readiness, warnings and errors where those events occur. Features without recurring diagnostic behavior do not need an empty diagnostics class. Shared class names describe their reusable responsibility without a feature prefix. Keep feature-specific helpers in the feature folder. Move code into Shared only when it is genuinely reusable without enabling another feature.
6. Register the main class in Plugin.cs inside the ConfigurationLoader callback. Bind settings for disabled features too. The central loader loads heimkit.cfg, preserves existing values, adds missing defaults and saves the complete file before initialization. Keep startup failure isolation between modules.
7. In Initialize, check Enabled and compatibility before activating gameplay code, and log the effective ENABLED or DISABLED state with a reason. In Tick, respect readiness and scheduling. Shutdown must stop work and release resources safely, including repeated calls.
8. Update config/heimkit.cfg to match implemented defaults. Users must never need to create the file themselves.

The current main classes construct their settings internally during the central loader callback. This preserves the existing registration flow; settings binding still happens before configuration saving and module initialization. ConfigurationLoader remains responsible for the shared file, and DiagnosticsSettings defines the common troubleshooting switch.

## Place tests

Use `tests/HeimKit/`, with `HeimKit.Tests.csproj` referencing the source project. Feature folders use the chosen feature name directly, using the same responsibility grouping as the source:

| Source responsibility | Test location |
| --- | --- |
| Features/InfiniteTorches | tests/HeimKit/InfiniteTorches/ |
| Features/WeatherGuard | tests/HeimKit/WeatherGuard/ |
| Configuration/ConfigurationLoader | tests/HeimKit/Configuration/ConfigurationLoaderTests.cs |
| Shared/<ClassName> | tests/HeimKit/Shared/<ClassName>Tests.cs |
| Feature Models/<TypeName> | tests/HeimKit/<FeatureName>/Models/<TypeName>Tests.cs |

Every test class and its file ends in `Tests`, never Checks or Test. Program.cs is only the executable runner that invokes suites; it contains no tests. Keep existing check logic when reorganizing. Create folders only when they contain meaningful tests. Do not invent tests for empty models or mirror trivial implementation details.

Verify enable/disable behavior, configuration preservation, logging and relevant feature behavior. Test independent operation and relevant combinations. Local checks cannot prove Unity-world or client networking behavior. Use README.md for build commands and live test procedures; report local and live results separately. Build output remains in the standard source bin directory.

## Update documentation

Update the current feature specification with implemented behavior, configuration, algorithms, file responsibilities and limitations. Keep one specification per feature. Put installation, supported versions, user test instructions and troubleshooting in README.md. Add verified functionality to its feature descriptions; distinguish code that still awaits live verification. Update architecture when the design changes and this guide when contributor procedures change. Keep shared documentation independent of AI tools or vendors.

Do not create development journals, task-tracking Markdown, completion checklists or abandoned-design histories. Do not commit generated output, proprietary assemblies, worlds, credentials or machine-specific paths. Leave local changes reviewable; commit and push only when requested. Installing or restarting a live server requires explicit authorization.
