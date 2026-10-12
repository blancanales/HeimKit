using BepInEx.Logging;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;

internal static class WeatherGuardDiagnosticsTests
{
    public static void Run()
    {
        using var logger = new ManualLogSource("WeatherGuard diagnostics tests");
        var messages = new List<string>();
        logger.LogEvent += (_, args) =>
        {
            if (args.Level != LogLevel.Debug && args.Level != LogLevel.Error)
                throw new Exception("Optional weather diagnostics must use Debug severity or preserve Error severity.");
            messages.Add(args.Data.ToString()!);
        };
        var disabled = new WeatherGuardDiagnostics(logger, false);
        disabled.Report(0f, () => throw new Exception("Debug-off snapshot evaluated."));
        disabled.Sample(0f, () => throw new Exception("Debug-off sample formatted."));
        if (disabled.TryWearSample(0, "piece", true) || disabled.ReserveCommand(0)) throw new Exception("Debug-off evidence reserved.");
        disabled.Wear(new WearObservation(), 100, false);
        disabled.Command(new WearObservation(), 100, false);
        if (messages.Count != 0) throw new Exception("Debug-off diagnostics emitted logs.");

        var diagnostics = new WeatherGuardDiagnostics(logger, true);
        diagnostics.Report(0f, () => new WeatherGuardStatus { Protected = 3, Eligible = 5, Limited = true });
        if (messages.Count != 2 || !messages[0].Contains("eligible_now=5 | managed_now=3") || !messages[0].Contains("capacity_limited=true"))
            throw new Exception("Status snapshot fields were not logged correctly.");
        diagnostics.Report(59f, () => throw new Exception("Throttled snapshot evaluated."));
        diagnostics.Report(60f, () => new WeatherGuardStatus { Protected = 8 });
        if (messages.Count != 4 || !messages[2].Contains("managed_now=8")) throw new Exception("Summary interval failed.");
        int formats = 0;
        for (int i = 0; i < 32; i++) diagnostics.Sample(0f, () => { formats++; return "acquisition sample"; });
        if (formats != WeatherGuardDiagnostics.SampleLimit || messages.Count != 4 + WeatherGuardDiagnostics.SampleLimit)
            throw new Exception("Sample cap failed or suppressed samples were formatted.");
        diagnostics.Sample(59f, () => throw new Exception("Sample budget reopened early."));
        diagnostics.Sample(60f, () => "Next sample window");
        if (!messages[^1].Contains("Next sample window")) throw new Exception("Sample budget did not recover.");
        int before = messages.Count;
        diagnostics.Reset();
        diagnostics.Report(0f, () => new WeatherGuardStatus());
        diagnostics.Sample(0f, () => "Reset sample window");
        if (messages.Count != before + 3) throw new Exception("Reset retained summary or sample limits.");
        // Loading/exclusion storms cannot consume acquisition, wear or command budgets.
        for (int i = 0; i < 100; i++) diagnostics.Sample(0, "context", () => "context blocked");
        diagnostics.Sample(0, "ownership", () => "ownership loss observed");
        if (!messages[^1].Contains("ownership loss observed")) throw new Exception("Streams share a sample budget.");
        diagnostics.Reset();
        foreach (bool protectedPiece in new[] { true, false })
        {
            for (int i = 0; i < WeatherGuardDiagnostics.WearSampleLimit; i++)
                if (!diagnostics.TryWearSample(0, "piece-" + i, protectedPiece)) throw new Exception("Wear samples missing.");
            if (diagnostics.TryWearSample(1, "piece-0", protectedPiece) || diagnostics.TryWearSample(59, "overflow", protectedPiece))
                throw new Exception("Wear evidence duplicated or cap exceeded.");
            if (!diagnostics.TryWearSample(60, "piece-0", protectedPiece)) throw new Exception("Wear window did not recover.");
        }
        for (int i = 0; i < WeatherGuardDiagnostics.SampleLimit; i++)
            if (!diagnostics.ReserveCommand(0)) throw new Exception("Native command budget missing.");
        if (diagnostics.ReserveCommand(59) || !diagnostics.ReserveCommand(60)) throw new Exception("Native command cap/interval failed.");
        diagnostics.Wear(new WearObservation { Identity = new() { ["object"] = "target" }, Protected = true, GateReached = false, Warmup = true, Before = 100 }, 100, false);
        if (!messages[^1].Contains("weather_branch_bypassed=false") || !messages[^1].Contains("warmup=true"))
            throw new Exception("Warmup was reported as suppression.");
        diagnostics.Wear(new WearObservation { Protected = true, GateReached = true, Suppressed = true, Before = 100 }, null, false);
        if (!messages[^1].Contains("health_after=unknown") || !messages[^1].Contains("weather_branch_bypassed=true"))
            throw new Exception("Native destruction or weather gate evidence missing.");
        diagnostics.Reset();
        for (int i = 0; i < WeatherGuardDiagnostics.WearSampleLimit; i++)
            diagnostics.TryWearSample(0, "covered-" + i, true);
        if (!diagnostics.TryWearSample(0, "exposed", true, true))
            throw new Exception("Covered pieces starved exposed evidence.");
        diagnostics.Reset();
        diagnostics.Report(0, () => new WeatherGuardStatus { OwnerLosses = 2, WeatherGates = 12, WetOpportunities = 5,
            NativeCommands = 3, SupportPasses = 10, Exclusions = new() { ["world-created"] = 4 } });
        if (!messages[^2].Contains("world-created:4") || !messages[^1].Contains("owner_losses_interval=2") ||
            !messages[^1].Contains("weather_gates_interval=12") || !messages[^2].Contains("wet_gates_interval=5") ||
            !messages[^1].Contains("support_passes_interval=10")) throw new Exception("Actionable evidence summary missing.");
        var history = new RainEvidence { Identity = new() { ["object"] = "exposed" }, Eligibility = "eligible", FirstHealth = 400,
            Health = 400, MinimumHealth = 400, Owner = 7, Creator = 10, Protected = true };
        history.ObserveGate(100, true);
        history.ObserveGate(105, true);
        history.ObserveGate(110, false);
        history.ObserveGate(115, true);
        if (history.WetSeconds != 0 || history.LongestWetSeconds != 5) throw new Exception("Dry processing counted as continuous wet exposure.");
        history.ObserveGate(120, true);
        history.ObserveGate(140, true);
        if (history.WetSeconds != 0) throw new Exception("Missing processing interval counted as continuous exposure.");
        history.ObserveState(380, 8, false, 10);
        history.ObserveState(400, 7, true, 10);
        if (history.MinimumHealth != 380 || history.OwnerChanges != 2 || history.ProtectionChanges != 2 || history.CreatorChanged)
            throw new Exception("Health loss or ownership interruption lost from history.");
        history.ObserveState(400, 7, true, 11);
        if (!history.CreatorChanged) throw new Exception("Creator change not detected.");
        disabled.Report(0, () => throw new Exception("Disabled history snapshot evaluated."),
            () => throw new Exception("Disabled rain history evaluated."));
        diagnostics.Reset();
        diagnostics.Report(0, () => new WeatherGuardStatus(), () => new[] { history });
        if (!messages[^1].Contains("health_minimum=380") || !messages[^1].Contains("management_changes_observed_total=2") ||
            !messages[^3].Contains("watched_player_pieces_now=1")) throw new Exception("Longitudinal rain evidence missing.");
        if (!messages[^1].Contains("Health loss observed; weather cause unconfirmed")) throw new Exception("Current conditions hid recorded health loss.");
        diagnostics.Reset();
        var control = new RainEvidence { Eligibility = "world-created", FirstHealth = 400, Health = 400, MinimumHealth = 400 };
        diagnostics.Report(0, () => new WeatherGuardStatus { Acquired = 10 }, () => new[] { control });
        if (!messages[^1].Contains("wet_observed_s=unknown") || messages[^1].Contains("wet_observed_s=0"))
            throw new Exception("Unavailable client rain calculation reported as measured zero.");
        diagnostics.Report(60, () => new WeatherGuardStatus { Acquired = 13, Released = 2 });
        if (!messages[^2].Contains("acquired_interval=3") || !messages[^1].Contains("acquired_total=13"))
            throw new Exception("Interval counts were confused with lifetime counts.");
        // A bypass is an observed action even when roof/dry conditions prevent a rain opportunity.
        diagnostics.Reset();
        var protectedWatch = new RainEvidence { Eligibility = "eligible", ProtectionReason = "eligible", Protected = true,
            FirstHealth = 400, Health = 400, MinimumHealth = 400, MaximumHealth = 400,
            Fields = new() { ["processor"] = "server", ["cached_roof"] = true, ["environment_wet"] = true } };
        protectedWatch.ObserveGate(1, false, true);
        protectedWatch.ObserveGate(2, false, true);
        diagnostics.Report(0, () => new WeatherGuardStatus(), () => new[] { protectedWatch });
        if (!messages[^1].Contains("protection=active") || !messages[^1].Contains("action=weather_branch_bypassed") ||
            !messages[^1].Contains("weather_bypasses_interval=2") || !messages[^1].Contains("weather_opportunity=conditions_not_met") ||
            !messages[^1].Contains("weather_damage=unconfirmed")) throw new Exception("Bypass was confused with a qualifying damage opportunity.");
        // Loss then repair must survive equal endpoint health and detail-sample suppression.
        protectedWatch.ObserveState(380, 0, true, 0);
        protectedWatch.ObserveState(400, 0, true, 0);
        diagnostics.Report(60, () => new WeatherGuardStatus(), () => new[] { protectedWatch });
        if (!messages[^1].Contains("health_change=loss_observed") || !messages[^1].Contains("health_lost_interval=20") ||
            !messages[^1].Contains("health_before=400") || !messages[^1].Contains("health_after=400") ||
            !messages[^1].Contains("action=weather_bypass_not_observed") || !messages[^1].Contains("weather_bypasses_interval=0") ||
            !messages[^3].Contains("watched_health_losses_interval=1")) throw new Exception("Repaired loss or interval action evidence was lost.");
        diagnostics.Report(120, () => new WeatherGuardStatus(), () => new[] { protectedWatch });
        if (!messages[^1].Contains("health_change=unchanged") || !messages[^1].Contains("health_lost_interval=0") ||
            !messages[^1].Contains("health_minimum=380")) throw new Exception("Historical damage repeated as interval damage.");
        diagnostics.Reset();
        control.ProtectionReason = "world-created";
        control.ObserveState(380, 0, false, 0);
        diagnostics.Report(0, () => new WeatherGuardStatus(), () => new[] { control });
        if (!messages[^1].Contains("protection=excluded") || !messages[^1].Contains("protection_reason=world-created") ||
            !messages[^1].Contains("action=left_to_native_owner") || !messages[^1].Contains("damage_source=unobserved") ||
            !messages[^1].Contains("weather_opportunity=owner_calculation_unavailable")) throw new Exception("Client health loss was attributed to weather or a server action.");
        diagnostics.Reset();
        var waiting = new RainEvidence { Eligibility = "eligible", ProtectionReason = "waiting_terrain", FirstHealth = 400, Health = 400, MinimumHealth = 400 };
        diagnostics.Report(0, () => new WeatherGuardStatus(), () => new[] { waiting });
        if (!messages[^1].Contains("protection=waiting") || !messages[^1].Contains("protection_reason=waiting_terrain"))
            throw new Exception("Eligible waiting piece was reported as protected or excluded.");
        protectedWatch.WetSeconds = 65;
        protectedWatch.Fields["cached_roof"] = false;
        diagnostics.Reset();
        diagnostics.Report(0, () => new WeatherGuardStatus(), () => new[] { protectedWatch });
        if (!messages[^1].Contains("weather_opportunity=qualifying_duration_observed") || !messages[^1].Contains("weather_damage=unconfirmed"))
            throw new Exception("Qualifying duration asserted prevented damage.");
        protectedWatch.Fields["no_building_fall"] = true;
        diagnostics.Report(60, () => new WeatherGuardStatus(), () => new[] { protectedWatch });
        if (!messages[^1].Contains("weather_opportunity=conditions_not_met")) throw new Exception("Duration overrode a native damage restriction.");
        diagnostics.Command(new WearObservation { Protected = true, Owner = true, Before = 400, Fields = new() { ["command"] = "RPC_Damage" } }, 380, false);
        if (!messages[^1].Contains("protection=active") || !messages[^1].Contains("action=native_command_executed") ||
            !messages[^1].Contains("health_change=loss_observed") || !messages[^1].Contains("damage_context=native_command"))
            throw new Exception("Native command outcome omitted protection, action or observed health change.");
        diagnostics.Reset();
        messages.Clear();
        var medium = new WeatherGuardDiagnostics(logger, true, DebugLogLevel.Medium, false);
        var statusItems = Enumerable.Range(0, 15).Select(i => new ProtectionObservation
        { Identity = new() { ["object"] = "player-" + i, ["prefab"] = "wood_floor" }, Protected = i != 14,
            Reason = i == 14 ? "waiting_terrain" : "eligible" }).ToList();
        statusItems.Add(new ProtectionObservation { Identity = new() { ["object"] = "world" }, Origin = "world_created", Reason = "world-created" });
        medium.Report(0, () => throw new Exception("Medium evaluated detailed snapshot."),
            () => throw new Exception("Medium gathered detailed histories."), () => statusItems);
        if (messages.Count != 13 || messages.Any(m => m.Contains("DETAIL") || m.Contains("object=world")) ||
            !messages[0].Contains("qualifying_items_now=15") || !messages[0].Contains("pending_changed_samples_now=3"))
            throw new Exception("Medium scope or output cap failed.");
        medium.Sample(0, () => throw new Exception("Medium formatted detailed sample."));
        medium.Wear(new WearObservation(), 1, false);
        if (medium.TryWearSample(0, "x", true) || medium.ReserveCommand(0))
            throw new Exception("Medium gathered detailed wear evidence.");
        medium.Report(59, () => throw new Exception(), null, () => throw new Exception("Medium interval reopened early."));
        medium.Report(60, () => throw new Exception(), null, () => statusItems);
        if (!messages.Any(m => m.Contains("object=player-14") && m.Contains("protected=false") && m.Contains("reason=waiting_terrain")))
            throw new Exception("Pending unprotected qualifying item was not sampled.");
        statusItems[0].Protected = false; statusItems[0].Reason = "waiting_objects";
        medium.Report(120, () => throw new Exception(), null, () => statusItems);
        if (!messages[^12].Contains("object=player-0") || !messages[^12].Contains("reason=waiting_objects"))
            throw new Exception("Medium did not prioritize changed protection status.");
        messages.Clear();
        var mediumWorld = new WeatherGuardDiagnostics(logger, true, DebugLogLevel.Medium, true);
        mediumWorld.Report(0, () => throw new Exception(), null, () => statusItems);
        mediumWorld.Report(60, () => throw new Exception(), null, () => statusItems);
        if (!messages.Any(m => m.Contains("object=world") && m.Contains("protected=false") && m.Contains("reason=world-created")))
            throw new Exception("World inclusion changed eligibility or failed to log the world item.");
        var mediumOff = new WeatherGuardDiagnostics(logger, false, DebugLogLevel.Medium, true);
        mediumOff.Report(0, () => throw new Exception(), null, () => throw new Exception("Debug-off gathered medium status."));
        mediumWorld.Reset();
        mediumWorld.Report(0, () => throw new Exception(), null, () => Array.Empty<ProtectionObservation>());
        if (!messages[^1].Contains("qualifying_items_now=0")) throw new Exception("Empty/reset medium status failed.");
        System.Console.WriteLine("PASS: WeatherGuard diagnostics respect debug mode, format status snapshots, limit summaries and samples, defer suppressed formatting and reset.");
    }
}
