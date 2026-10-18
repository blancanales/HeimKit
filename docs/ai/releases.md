# AI release workflow

Read [AGENTS.md](../../AGENTS.md) and the [AI versioning policy](versioning.md) first. This document defines what Trevor means by an actual request to create a HeimKit release. Discussing the workflow or quoting example requests does not authorize executing a release.

## Request scope

An actual request such as "create a release for me" authorizes completing the current feature/fix for release, committing its reviewed source, integrating it into main, assigning the next release version, building and packaging from main, pushing main and the release tag, creating the GitHub release with the ZIP attached, and removing the merged feature branch after successful publication. Do not repeatedly ask for authorization already supplied by that request. Required tool-access approvals still apply.

Trevor uploads the ZIP to Thunderstore himself. Never upload to Thunderstore, install into a server, restart a server, change worlds, alter clients, bypass compatibility checks or include unrelated work as part of this workflow.

"Create a test build" only requests one local development DLL through build.ps1, using the standard source bin output directory. "Build Release configuration" or "create an optimized DLL" is a local build request, not authorization for Git integration, branch deletion or GitHub publication. If the request actually leaves publication scope ambiguous, establish that scope before performing external writes.

## Integrate the intended source

1. Inspect the current branch, working tree, origin, worktrees and applicable project instructions. Identify the current task's feature/fix branch and preserve unrelated changes and other active tasks. Inspect staged content before every source commit. Exclude generated output, credentials, saves, copied runtime files and proprietary assemblies.
2. Fetch current main and inspect existing GitHub releases and tags to establish the latest released version. Do not use stale memory or only the feature branch's version. Use existing connector/CLI access; do not embed credentials. An existing tag or release must not be overwritten or moved to different source.
3. Commit the intended feature/fix work, bring current main into the feature branch as needed, and resolve conflicts while retaining the intended behavior. Do not discard another task's work or force-push to simplify conflicts. Keep release failures recoverable.
4. Use a suitable main checkout/worktree, without switching a checkout that contains unrelated changes or taking over an active task's checkout. Fast-forward local main to the fetched main where appropriate, then merge the intended feature/fix branch into main. Preserve main's intervening updates. If already working on main, no feature merge or feature deletion is needed. Follow branch-protection requirements if direct updates are unavailable; attach any pull request created for this task to the task.

## Prepare and build on main

1. Choose the next plain Major.Minor.Patch release version. Normally retain the latest released major/minor and increment the patch. Use the current development target only if it is the intended next unused release; do not reuse an already released version or infer a minor-version increase without Trevor's instruction. Remove the development label/counter for the actual release.
2. Synchronize the project version, BepInPlugin metadata, both startup configuration messages and the release baseline in server setup. Remove the development-version row for the release source. Keep numeric File version and clean Product version aligned. Leave game/loader compatibility unchanged unless explicitly included in the task.
3. Update the relevant specifications, guides, configuration reference and README scope as required by contributing.md. Add concise user-facing notes at the top of CHANGELOG.md under the matching release version. Follow the [release-note scope](../contributing.md#thunderstore-release-notes): exclude AI instructions, build scripts, contributor procedures and internal tooling maintenance from both the mod changelog and GitHub release notes. Describe only resulting user-visible mod behavior when relevant. Internal maintenance alone does not warrant a mod release or changelog entry. Do not add test results, task journals or development histories to documentation. Preserve historical release notes.
4. Review and commit the release-ready source and metadata on main. Record that commit as the intended release source. The checkout must be clean before the local release command; generated ignored artifacts do not belong in the commit.
5. Invoke Build-Release.ps1 once using the [release build command](../development.md#release-build-command). That command runs the automated checks, builds the optimized plugin, validates the Thunderstore package and reports the ZIP location. Prefer one approval for this local build operation instead of separate approvals for each internal stage. Failure stops publication; repair within the authorized task, commit corrected source and rerun as necessary. Do not describe a failed or incomplete command as a completed release.
6. The artifact must correspond to the exact release commit. If source or main changes after building, reconcile the changes and rebuild before tagging/publishing. Never attach an older DLL or ZIP merely because its filename/version looks correct. Keep feature branches until the release succeeds.

## Publish GitHub and finish

1. Push main without forcing. If remote main advanced, integrate it safely and repeat release preparation/build as needed; do not publish an artifact from a different commit.
2. Create the version tag, named v followed by the plain release version, at the exact release commit. Push that tag and create a published GitHub release targeting it, with a clear HeimKit version title and the matching user-facing release notes. A draft is not the completed release Trevor requested.
3. Attach the same validated versioned Thunderstore ZIP to the GitHub release. Verify the published tag target, release URL and attached asset name/content correspond to the local package. GitHub creates its own source archives; those are not substitutes for the plugin ZIP. Packaging README links should use the immutable version tag, which Build-Release.ps1 selects by default.
4. Only after the release and ZIP are verified, remove the merged feature/fix branch locally and from origin. Confirm it is merged into main; do not force-delete unmerged work. Retain any needed ignored build artifacts before retiring a managed worktree. Do not delete main, another task's branch, tags or unrelated worktrees. If safe deletion is blocked by an active task/worktree or repository permissions, preserve the work and report the exact remaining cleanup.
5. Report the release version, GitHub release link, local ZIP path, release commit and branch-cleanup outcome. State that Trevor's Thunderstore upload remains to be done. Keep observed build/check results in chat and separate them from unverified gameplay behavior. Do not claim Thunderstore publication or live installation.

Build-Release.ps1 performs only local build/packaging work. The AI owns the Git/GitHub steps above; the script does not silently merge, publish or remove branches.
