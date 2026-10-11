# NearbyChestCrafting

Crafting with resources in nearby chests is a researched idea, not an implemented HeimKit feature. There is no module, enable toggle, client installation or inventory behavior for it.

## Server-only requirement

The user requires operation on the dedicated server with unmodified clients. If that cannot be provided, the feature will not be implemented, including a lifecycle scaffold or placeholder toggle.

The inspected vanilla crafting UI checks the player's local inventory before permitting crafting, and normal item creation/resource consumption also execute locally. A server seeing chest contents does not satisfy those client checks. No supported server-only mechanism for the intended behavior has been established in the reviewed paths. This is a specific evidence-based blocker, not a claim that every conceivable mechanism is impossible.

Vanilla crafting remains unchanged: players use resources in their own inventory. Nearby chests are not scanned, accessed, claimed, modified or used for crafting by HeimKit. Existing torch and weather features are unaffected by this research.

The [research specification](../specs/nearby-chest-crafting.md) records inspected methods, primary references, ownership/access concerns and remaining transaction/design questions. Companion-client or automatic-restocking alternatives are outside the approved scope. The [supported environment](../server-setup.md#supported-environment) identifies the game baseline inspected, not a working chest-crafting capability.
