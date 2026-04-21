using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Cookies.Contract;
using SwiftlyS2.Core;
using SwiftlyS2.Core.Extensions;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CenterSpeed;

[PluginMetadata(Id = "centerspeed", Version = "1.0.1", Name = "CenterSpeed", Author = "Lethal & Retro, Ported by Low", Description = "Center-screen particle speedometer HUD")]
public sealed partial class CenterSpeed : BasePlugin
{
    // -------------------------------------------------------------------------
    // State

    private readonly PlayerHudState?[]    _huds           = new PlayerHudState?[65];
    private readonly PlayerHudSettings?[] _playerSettings = new PlayerHudSettings?[65];
    private readonly float[]              _lastSpeed   = new float[65];

    private IConVar<string>?      _particleConVar;
    private IPlayerCookiesAPIv1?  _cookies;
    private PluginConfig          _config          = new();
    private int                   _tickCounter;
    private int                   _transmitLogCounter;

    private static readonly Dictionary<int, int> _digitMap = new()
    {
        [0] = 1,  [1] = 2,  [2] = 3,  [3] = 4,
        [4] = 5,  [5] = 6,  [6] = 7,  [7] = 8,
        [8] = 9,  [9] = 10,
    };

    // -------------------------------------------------------------------------
    // Settings / state classes

    private sealed class PlayerHudSettings
    {
        public float[] DigitOffsets { get; set; } = { -1.5f, -0.5f, 0.5f, 1.5f };
        public float   HudScale     { get; set; } = 0.04f;
        public float   YOffset      { get; set; } = -1f;
        public bool    Enabled      { get; set; } = false;
    }

    private sealed class PluginConfig
    {
        public string  ConfigVersion       { get; set; } = "1.0.1";
        public float   DefaultScale        { get; set; } = 0.04f;
        public float   DefaultYOffset      { get; set; } = -1f;
        public float[] DefaultDigitOffsets { get; set; } = { -1.5f, -0.5f, 0.5f, 1.5f };
        public bool    EnableDatabase      { get; set; } = true;
        public bool    Debug               { get; set; } = false;
    }

    private sealed class PlayerHudState
    {
        public CParticleSystem?[] Digits     { get; } = new CParticleSystem?[4];
        public bool               IsDisposed { get; set; }
        public int                OwnerSlot  { get; init; }
    }

    private sealed record PlayerSettingsData(float[] Offsets, float Scale, float YOffset, bool Enabled);

    // -------------------------------------------------------------------------
    // BasePlugin lifecycle

    public CenterSpeed(ISwiftlyCore core) : base(core) { }

    public override void Load(bool hotReload)
    {
        LoadConfig();
        try
        {
            _particleConVar = Core.ConVar.Create<string>(
                "cs_speed_particle",
                "Particle file for center-speed HUD",
                "flowassets/particles/digits_x.vpcf"
            );
        }
        catch
        {
            // Convar already exists on hot reload � _particleConVar stays null,
            // all call sites fall back to the hardcoded default via ??.
        }
        LogDebug("[CenterSpeed] Plugin loaded. HotReload={HotReload} PluginPath={Path}", hotReload, Core.PluginPath);
    }
    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        if (!_config.EnableDatabase)
        {
            Core.Logger.LogWarning("[CenterSpeed] Database disabled in config — using local JSON for settings.");
            return;
        }
        if (interfaceManager.TryGetSharedInterface<IPlayerCookiesAPIv1>("Cookies.Player.v1", out var cookies))
        {
            _cookies = cookies;
            Core.Logger.LogWarning("[CenterSpeed] Cookies.Player.v1 acquired — using Cookies for settings persistence.");
        }
        else
        {
            Core.Logger.LogWarning("[CenterSpeed] Cookies plugin not available — falling back to local JSON settings.");
        }
    }
    public override void Unload()
    {
        for (var i = 0; i < 65; i++)
            KillPlayerHud(i);
    }

    // -------------------------------------------------------------------------
    // Framework event listeners

    [EventListener<EventDelegates.OnPrecacheResource>]
    public void OnPrecacheResource(IOnPrecacheResourceEvent @event)
    {
        // Always precache the configured particle � handles workshop-mounted assets too.
        var particlePath = _particleConVar?.Value ?? "flowassets/particles/digits_x.vpcf";
        LogDebug("[CenterSpeed][Precache] Registering particle: {Path}", particlePath);
        @event.AddItem(particlePath);

        // Also scan any locally bundled assets.
        var assetPath = Path.Combine(Core.PluginPath, "assets");
        if (!Directory.Exists(assetPath))
        {
            LogDebug("[CenterSpeed][Precache] No local assets folder found at: {Path} (using workshop/addon assets)", assetPath);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories))
        {
            var relative = file[(assetPath.Length + 1)..].Replace("\\", "/");
            if (!relative.StartsWith("particles/", StringComparison.OrdinalIgnoreCase))
                continue;

            var asset = relative.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                ? relative[..^2]
                : relative;

            LogDebug("[CenterSpeed][Precache] Local asset: {Asset}", asset);
            @event.AddItem(asset);
        }
    }

    [EventListener<EventDelegates.OnMapUnload>]
    public void OnMapUnload(IOnMapUnloadEvent @event)
    {
        // Drop entity references — the engine has already cleaned them up.
        for (var i = 0; i < 65; i++)
            _huds[i] = null;
    }

    [EventListener<EventDelegates.OnTick>]
    public void OnTick()
    {
        // Update every 4 ticks (~16 Hz at 64 tick).
        if (++_tickCounter % 4 != 0) return;

        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (player == null || !player.IsValid || player.IsFakeClient) continue;
            UpdatePlayerHud(player);
        }
    }

    // -------------------------------------------------------------------------
    // CS2 game events

    [GameEventHandler(HookMode.Post)]
    public HookResult OnClientConnect(EventPlayerConnectFull @event)
    {
        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;
        if (player.IsFakeClient) return HookResult.Continue;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return HookResult.Continue;

        var settings = NewDefaultSettings();
        LoadSettings(player.SteamID, settings);
        _playerSettings[id] = settings;

        LogDebug("[CenterSpeed][Connect] {Name} slot={Id} steam={Steam} HudEnabled={Enabled}",
            player.Name, id, player.SteamID, settings.Enabled);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnClientDisconnect(EventPlayerDisconnect @event)
    {
        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return HookResult.Continue;

        KillPlayerHud(id);
        _playerSettings[id] = null;

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;
        if (player.IsFakeClient) return HookResult.Continue;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return HookResult.Continue;

        LogDebug("[CenterSpeed][Spawn] EventPlayerSpawn fired for slot={Id} team={Team}",
            id, player.Controller?.TeamNum);

        // Delay by one tick so that any OnPlayerTeam that fires in the same frame
        // is processed before we create new particles.
        Core.Scheduler.NextTick(() =>
        {
            if (player.IsValid && !player.IsFakeClient)
                SpawnPlayerHud(player);
        });
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return HookResult.Continue;

        KillPlayerHud(id);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        if (@event.IsBot) return HookResult.Continue;

        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return HookResult.Continue;

        var oldTeam = @event.OldTeam;
        var newTeam = @event.Team;

        LogDebug("[CenterSpeed][Team] slot={Id} oldTeam={Old} newTeam={New}", id, oldTeam, newTeam);

        // Only kill the HUD when the player is leaving a playing team (going to spectator/unassigned).
        // When joining a playing team, OnPlayerSpawn will handle spawning.
        if (oldTeam >= 2 && newTeam < 2)
            KillPlayerHud(id);

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // HUD management

    /// <summary>
    /// Updates scale (CP34) and position (CP33) on existing live particles without
    /// destroying and recreating them. Used by menu adjustments to avoid ghost images.
    /// Falls back to a full respawn if no live HUD exists yet.
    /// </summary>
    private void ApplyHudSettings(int id, PlayerHudSettings settings)
    {
        var state = _huds[id];
        if (state == null || state.IsDisposed)
        {
            // No live HUD — nothing to update in-place.
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || !particle.IsValidEntity) continue;

            // Update scale and position CPs in-place on the running particle.
            SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f));
            SetControlPointValue(particle, 33, new Vector(settings.DigitOffsets[i], settings.YOffset, 0f));
        }
    }

    private void SpawnPlayerHud(IPlayer player)
    {
        if (!player.IsValid || player.IsFakeClient) return;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return;

        // Only spawn for players on playing teams (T=2, CT=3).
        var team = player.Controller?.TeamNum ?? 0;
        LogDebug("[CenterSpeed][SpawnHUD] Enter slot={Id} team={Team}", id, team);

        // Always kill any existing HUD first before spawning new particles.
        KillPlayerHud(id);

        if (team < 2)
        {
            LogDebug("[CenterSpeed][SpawnHUD] Skipping — not on a playing team (team={Team})", team);
            return;
        }

        var settings = _playerSettings[id] ??= NewDefaultSettings();
        LogDebug("[CenterSpeed][SpawnHUD] Settings: Enabled={Enabled} Scale={Scale} YOffset={Y}",
            settings.Enabled, settings.HudScale, settings.YOffset);

        if (!settings.Enabled)
        {
            LogDebug("[CenterSpeed][SpawnHUD] HUD disabled for slot={Id} � use !hudsettings toggle to enable", id);
            return;
        }

        var particleName = _particleConVar?.Value ?? "flowassets/particles/digits_x.vpcf";
        LogDebug("[CenterSpeed][SpawnHUD] Spawning 4 particles with effect={Name}", particleName);

        // Spawn particle at the player's current position so it's near the player from the start.
        var playerOrigin = (player.PlayerPawn?.IsValid == true ? player.PlayerPawn.AbsOrigin : null) ?? new Vector(0f, 0f, 0f);
        LogDebug("[CenterSpeed][SpawnHUD] Player origin={Pos}", playerOrigin);

        var state = new PlayerHudState { OwnerSlot = id };

        for (var i = 0; i < 4; i++)
        {
            var particle = Core.EntitySystem.CreateEntityByDesignerName<CParticleSystem>("info_particle_system");
            if (particle == null)
            {
                Core.Logger.LogWarning("[CenterSpeed][SpawnHUD] FAILED to create info_particle_system for digit {Index}", i);
                continue;
            }

            LogDebug("[CenterSpeed][SpawnHUD] Particle[{I}] created index={Idx}", i, particle.Index);

            // Set effect_name, start_active, and origin at spawn time via CEntityKeyValues.
            using var kv = new CEntityKeyValues();
            kv.SetString("effect_name", particleName);
            kv.SetBool("start_active", false);
            kv.SetVector("origin", playerOrigin);
            particle.DispatchSpawn(kv);

            LogDebug("[CenterSpeed][SpawnHUD] Particle[{I}] post-spawn: IsValid={IsValid} EffectName={Effect}",
                i, particle.IsValidEntity, particle.EffectName);

            // DataCP/DataCPValue does NOT reliably propagate to the client particle system.
            // CP33 (X/Y position offset) MUST be a ServerControlPoint.
            // We have 4 server CP slots total:
            //   Slot 0 = CP16: (R, G, B) in 0-255 — color; C_OP_RemapCPtoVector → field 6 (0-1)
            //   Slot 1 = CP32: (frame, 0, 0) — digit sprite frame
            //   Slot 2 = CP34: (scale, 0, 0) — sprite size
            //   Slot 3 = CP33: (xOffset, yOffset, 0) — screen position
            bool r16 = SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f)); // white default
            bool r32 = SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f));
            bool r34 = SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f));
            bool r33 = SetControlPointValue(particle, 33, new Vector(settings.DigitOffsets[i], settings.YOffset, 0f));
            LogDebug("[CenterSpeed][SpawnHUD] SetCP: CP16={R16} CP32={R32} CP34={R34} CP33={R33} offset=({X},{Y})",
                r16, r32, r34, r33, settings.DigitOffsets[i], settings.YOffset);

            particle.AcceptInput<string>("Start", null);
            particle.Active = true;
            particle.ActiveUpdated();
            LogDebug("[CenterSpeed][SpawnHUD] Particle[{I}] started. Active={Active}", i, particle.Active);

            // Immediately make visible to the owner (per-player only, not global).
            particle.SetTransmitState(true, id);
            LogDebug("[CenterSpeed][SpawnHUD] Particle[{I}] SetTransmitState(true, ownerSlot={Id})", i, id);

            state.Digits[i] = particle;
        }

        _huds[id] = state;

        // Apply initial transmit so the owner sees the HUD immediately.
        ApplyTransmit(id, state, settings);
        LogDebug("[CenterSpeed][SpawnHUD] Done. Particles created={Count}",
            Array.FindAll(state.Digits, d => d != null).Length);
    }

    private void KillPlayerHud(int slot)
    {
        var state = _huds[slot];
        if (state == null) return;

        state.IsDisposed = true;
        _huds[slot]      = null;
        _lastSpeed[slot] = 0;

        foreach (var particle in state.Digits)
        {
            if (particle == null || !particle.IsValidEntity) continue;

            particle.AcceptInput<string>("Stop", null);
            particle.AcceptInput<string>("DestroyImmediately", null);
            particle.Active = false;
        }
    }

    // -------------------------------------------------------------------------
    // Per-tick HUD update

    private void UpdatePlayerHud(IPlayer player)
    {
        var id    = player.PlayerID;
        var state = _huds[id];

        if (state == null || state.IsDisposed) return;

        var settings = _playerSettings[id];
        if (settings == null) return;

        // Kill HUD when the player is no longer on a playing team.
        if (player.Controller?.TeamNum < 2)
        {
            KillPlayerHud(id);
            return;
        }

        // Calculate 2D speed (without Z component).
        int speed = 0;
        if (player.PlayerPawn != null && player.PlayerPawn.IsValid && player.PlayerPawn.LifeState == 0)
        {
            var v = player.PlayerPawn.AbsVelocity;
            speed = (int)Math.Clamp(Math.Sqrt(v.X * v.X + v.Y * v.Y), 0.0, 9999.0);
        }

        var digits = new int[4]
        {
            speed / 1000,
            speed / 100  % 10,
            speed / 10   % 10,
            speed        % 10,
        };

        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || !particle.IsValidEntity || state.IsDisposed) continue;

            SetControlPointValue(particle, 32, new Vector(_digitMap.GetValueOrDefault(digits[i], 1), 0f, 0f));

            if (_lastSpeed[id] > speed)
                SetControlPointValue(particle, 16, new Vector(255f, 0f, 0f));   // losing — red
            else if (_lastSpeed[id] < speed)
                SetControlPointValue(particle, 16, new Vector(0f, 255f, 0f));   // gaining — green
            else
                SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f)); // steady — white
        }

        _lastSpeed[id] = speed;
    }

    private void ApplyTransmit(int ownerSlot, PlayerHudState state, PlayerHudSettings settings)
    {
        bool shouldSeeOwner = settings.Enabled && !state.IsDisposed;
        int playerCount = 0;

        foreach (var p in Core.PlayerManager.GetAllPlayers())
        {
            if (p == null || !p.IsValid) continue;
            playerCount++;

            bool visible = shouldSeeOwner && p.PlayerID == ownerSlot;

            // Log every transmit pass for the owner so we can confirm it fires.
            if (p.PlayerID == ownerSlot)
            {
                LogDebug("[CenterSpeed][Transmit] owner slot={Owner} visible={Vis} shouldSeeOwner={Should} tick={T}",
                    ownerSlot, visible, shouldSeeOwner, _transmitLogCounter++);
            }

            foreach (var particle in state.Digits)
            {
                if (particle == null || !particle.IsValidEntity) continue;
                particle.SetTransmitState(visible, p.PlayerID);
            }
        }

        // Warn if GetAllPlayers returned nobody � that means transmit was never set.
        if (playerCount == 0)
            Core.Logger.LogWarning("[CenterSpeed][Transmit] WARNING: GetAllPlayers() returned 0 players � transmit not applied for ownerSlot={Owner}", ownerSlot);
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static void PrintHudSettings(IPlayer player, PlayerHudSettings settings)
    {
        var o = settings.DigitOffsets;
        var stateText = settings.Enabled ? "[lime]ON[/]" : "[lightred]OFF[/]";
        player.SendChat($"[gold][ HUD ][/] Offsets: [white]1={o[0]:F2}  2={o[1]:F2}  3={o[2]:F2}  4={o[3]:F2}[/]");
        player.SendChat($"[gold][ HUD ][/] Scale: [lime]{settings.HudScale:F4}[/]  Y-Offset: [lime]{settings.YOffset:F4}[/]  Status: {stateText}");
    }

    private void LogDebug(string template, params object?[] args)
    {
        if (_config.Debug)
            Core.Logger.LogWarning(template, args);
    }

    private PlayerHudSettings NewDefaultSettings() => new()
    {
        HudScale     = _config.DefaultScale,
        YOffset      = _config.DefaultYOffset,
        DigitOffsets = (float[])_config.DefaultDigitOffsets.Clone(),
        Enabled      = false,
    };

    /// <summary>
    /// Writes cpIndex+value into the first available server-CP slot (slot 255 = unassigned).
    /// </summary>
    private bool SetControlPointValue(CParticleSystem particle, int cpIndex, Vector value)
    {
        for (var i = 0; i < 4; i++)
        {
            var assignment = particle.ServerControlPointAssignments[i];
            // 255 = unassigned sentinel in CS2 particle schema.
            // Also accept 0 as free if the CP index we want isn't 0 � some runtime defaults fill with 0.
            bool isFree = assignment == 255 || (assignment == 0 && cpIndex != 0);
            if (assignment == cpIndex || isFree)
            {
                particle.ServerControlPointAssignments[i] = (byte)cpIndex;
                particle.ServerControlPoints[i]           = value;
                particle.ServerControlPointAssignmentsUpdated();
                particle.ServerControlPointsUpdated();
                return true;
            }
        }

        Core.Logger.LogWarning("[CenterSpeed] No free server control-point slot for CP {CpIndex}. Current slots: [{A0},{A1},{A2},{A3}]",
            cpIndex,
            particle.ServerControlPointAssignments[0],
            particle.ServerControlPointAssignments[1],
            particle.ServerControlPointAssignments[2],
            particle.ServerControlPointAssignments[3]);
        return false;
    }

}
