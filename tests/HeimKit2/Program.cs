using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
var failures = new List<Exception>();
void Run(string name, Action action)
{
    try { action(); }
    catch (Exception error) { failures.Add(new Exception(name, error)); System.Console.WriteLine("FAIL: " + name + ": " + error.Message); }
}
Run("ApplicationIdentityTests", ApplicationIdentityTests.Run);
Run("CoreRebuildTests", CoreRebuildTests.Run);
Run("GameCompatibilityTests", GameCompatibilityTests.Run);
Run("PerformanceRegressionTests", PerformanceRegressionTests.Run);
Run("TameTravelRecoveryTests", TameTravelRecoveryTests.Run);
Run("GameVersionCompatibilityTests", GameVersionCompatibilityTests.Run);
Run("SharedMapTests", SharedMapTests.Run);
Run("SharedMapExplorationTests", SharedMapExplorationTests.Run);
Run("SharedMapStoreTests", SharedMapStoreTests.Run);
Run("SharedMapPortalTests", SharedMapPortalTests.Run);
Run("SharedMapMarkerRefreshTests", SharedMapMarkerRefreshTests.Run);
Run("SharedMapResourceTests", SharedMapResourceTests.Run);
Run("SharedMapTerrainTests", SharedMapTerrainTests.Run);
Run("SharedMapResourceStoreTests", SharedMapResourceStoreTests.Run);
Run("SharedMapResourcePinSpacingTests", SharedMapResourcePinSpacingTests.Run);
Run("SharedMapDungeonTests", SharedMapDungeonTests.Run);
Run("SharedMapDungeonPinTests", SharedMapDungeonPinTests.Run);
Run("SharedMapWritePolicyTests", SharedMapWritePolicyTests.Run);
Run("SharedMapProgressTests", SharedMapProgressTests.Run);
Run("SharedMapDungeonLayoutTests", SharedMapDungeonLayoutTests.Run);
Run("SharedMapLandmarkTests", SharedMapLandmarkTests.Run);
Run("SharedMapPinRemovalTests", SharedMapPinRemovalTests.Run);
Run("SharedMapPinChangesTests", SharedMapPinChangesTests.Run);
Run("InfiniteTorchesTests", InfiniteTorchesTests.Run);
Run("InfiniteTorchesRefillRulesTests", InfiniteTorchesRefillRulesTests.Run);
Run("DiagnosticLogTests", DiagnosticLogTests.Run);
Run("StartupLoggingTests", StartupLoggingTests.Run);
Run("ServerDebugActivityTests", ServerDebugActivityTests.Run);
Run("ModuleCleanupTests", ModuleCleanupTests.Run);
Run("ConfigurationLoaderTests", ConfigurationLoaderTests.Run);
Run("DebugLoggingResolverTests", DebugLoggingResolverTests.Run);
Run("InfiniteTorchesDiagnosticsTests", InfiniteTorchesDiagnosticsTests.Run);
Run("InfiniteTorchesPendingRefillsTests", InfiniteTorchesPendingRefillsTests.Run);
Run("WeatherGuardTests", WeatherGuardTests.Run);
Run("WeatherGuardDiagnosticsTests", WeatherGuardDiagnosticsTests.Run);
Run("WeatherGuardControlsTests", WeatherGuardControlsTests.Run);
Run("WeatherGuardPatchesTests", WeatherGuardPatchesTests.Run);
Run("WeatherGuardNetworkFilterTests", WeatherGuardNetworkFilterTests.Run);

Run("SleepActivityTests", SleepActivityTests.Run);

Run("SleepActivityDiagnosticsTests", SleepActivityDiagnosticsTests.Run);

Run("DiagnosticsEvidenceTests", DiagnosticsEvidenceTests.Run);

Run("PlayerServicesTests", PlayerServicesTests.Run);

Run("PlayerActivityTests", PlayerActivityTests.Run);
Run("PlayerActivityHealthTests", PlayerActivityHealthTests.Run);
Run("PlayerActivityDebugTests", PlayerActivityDebugTests.Run);
Run("RaidRulesTests", RaidRulesTests.Run);

Run("PlayerPresenceDiagnosticsTests", PlayerPresenceDiagnosticsTests.Run);
Run("PlayerConnectionNoticesTests", PlayerConnectionNoticesTests.Run);

Run("PlayerPresenceEventsTests", PlayerPresenceEventsTests.Run);

Run("PlayerActivityNoticesTests", PlayerActivityNoticesTests.Run);

Run("PlayerProcessingNoticesTests", PlayerProcessingNoticesTests.Run);

Run("SleepDelayTests", SleepDelayTests.Run);
Run("SleepNoticesTests", SleepNoticesTests.Run);
Run("SelectionNoticesTests", SelectionNoticesTests.Run);
Run("WeatherGuardStandardNoticesTests", WeatherGuardStandardNoticesTests.Run);
Run("InfiniteTorchesStandardNoticesTests", InfiniteTorchesStandardNoticesTests.Run);

Run("DebugLoggingStartupTests", DebugLoggingStartupTests.Run);

Run("WeatherGuardLiveDebugPolicyTests", WeatherGuardLiveDebugPolicyTests.Run);

Run("FeatureLiveDebugPolicyTests", FeatureLiveDebugPolicyTests.Run);

Run("DebugLoggingDeliveryTests", DebugLoggingDeliveryTests.Run);

Run("WeatherGuardExposureTrackingTests", WeatherGuardExposureTrackingTests.Run);

Run("WeatherGuardWearPassTests", WeatherGuardWearPassTests.Run);

Run("WeatherGuardSettingsLoggingTests", WeatherGuardSettingsLoggingTests.Run);
Run("WorldAdditionsTests", WorldAdditionsTests.Run);
Run("WorldPerformanceTests", WorldPerformanceTests.Run);

if (failures.Count > 0) throw new AggregateException(failures);
