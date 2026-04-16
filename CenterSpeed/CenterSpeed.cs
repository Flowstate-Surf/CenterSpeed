using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cookies.Contract;
using SwiftlyS2.Core;
using SwiftlyS2.Core.Extensions;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
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
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;

namespace CenterSpeed;

[PluginMetadata(Id = "centerspeed", Version = "1.0.1", Name = "CenterSpeed", Author = "Lethal & Retro, Ported by Low", Description = "Center-screen particle speedometer HUD")]
public sealed class CenterSpeed : BasePlugin
{
    // -------------------------------------------------------------------------
    // State

    private readonly PlayerHudState?[]    _huds           = new PlayerHudState?[65];
    private readonly PlayerHudSettings?[] _playerSettings = new PlayerHudSettings?[65];
    private readonly float[]              _lastSpeed      = new float[65];

    private IConVar<string>?      _particleConVar;
    private IPlayerCookiesAPIv1?  _cookies;
    private PluginConfig          _config          = new();
    private int                   _tickCounter;
    private int                   _transmitLogCounter;

    private static readonly Dictionary<int, int> _digitMap = new()
    {
        [0] = 1,  [1] = 2,  [2] = 4,  [3] = 5,
        [4] = 7,  [5] = 8,  [6] = 10, [7] = 11,
        [8] = 12, [9] = 13,
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
                "particles/digits_x/digits_x.vpcf"
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
        var particlePath = _particleConVar?.Value ?? "particles/digits_x/digits_x.vpcf";
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
        // Drop entity references � the engine has already cleaned them up.
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
        SpawnPlayerHud(player);
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

        if (id >= 0 && id < 65)
            KillPlayerHud(id);

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Command

    [Command("hudsettings")]
    public void OnHudSettingsCommand(ICommandContext context)
    {
        // Debug: log every invocation so we can see what the engine passes.
        LogDebug("[CenterSpeed][Cmd] hudsettings fired. IsSentByPlayer={IsBP} Sender={S} Args.Length={L} Args=[{Args}]",
            context.IsSentByPlayer,
            (context.Sender as IPlayer)?.Name ?? "null",
            context.Args.Length,
            string.Join(", ", context.Args));

        if (context.Sender is not IPlayer player) return;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return;

        var settings = _playerSettings[id] ??= NewDefaultSettings();

        if (context.Args.Length == 0 ||
            context.Args[0].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            PrintHudSettings(player, settings);
            return;
        }

        var sub = context.Args[0].ToLowerInvariant();

        switch (sub)
        {
            case "offset":
            {
                if (context.Args.Length < 3 ||
                    !int.TryParse(context.Args[1], out var index1) ||
                    !float.TryParse(context.Args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    player.SendChat(" [HUD] Usage: !hudsettings offset <1-4> <-10 to 10>");
                    return;
                }

                index1 = Math.Clamp(index1, 1, 4);
                value  = Math.Clamp(value, -10f, 10f);
                settings.DigitOffsets[index1 - 1] = value;
                SaveSettings(player.SteamID, settings);
                SpawnPlayerHud(player);
                player.SendChat($" [HUD] Digit {index1} offset set to {value:F2}");
                break;
            }

            case "scale":
            {
                if (context.Args.Length < 2 ||
                    !float.TryParse(context.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    player.SendChat(" [HUD] Usage: !hudsettings scale <value> (e.g. 0.001 to 1.0)");
                    return;
                }

                settings.HudScale = Math.Clamp(value, 0.0001f, 10f);
                SaveSettings(player.SteamID, settings);
                SpawnPlayerHud(player);
                player.SendChat($" [HUD] Scale set to {settings.HudScale:F6}");
                break;
            }

            case "yoffset":
            {
                if (context.Args.Length < 2 ||
                    !float.TryParse(context.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
                {
                    player.SendChat(" [HUD] Usage: !hudsettings yoffset <-10 to 10>");
                    return;
                }

                settings.YOffset = Math.Clamp(offset, -10f, 10f);
                SaveSettings(player.SteamID, settings);
                SpawnPlayerHud(player);
                player.SendChat($" [HUD] Y-Offset set to {settings.YOffset:F2}");
                break;
            }

            case "toggle":
            {
                settings.Enabled = !settings.Enabled;
                SaveSettings(player.SteamID, settings);

                if (settings.Enabled)
                    SpawnPlayerHud(player);
                else
                    KillPlayerHud(id);

                player.SendChat($" [HUD] Enabled: {settings.Enabled}");
                break;
            }

            default:
                player.SendChat(" [HUD] Subcommands: offset <1-4> <-10..10> | scale <0-10> | yoffset <-10..10> | toggle | info");
                break;
        }
    }

    [Command("cs")]
    public void OnCsCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player) return;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return;

        var settings = _playerSettings[id] ??= new PlayerHudSettings();
        OpenMainMenu(player, settings);
    }

    // -------------------------------------------------------------------------
    // Menu builders
    // Color codes work in OPTION TEXT only, not titles.
    // \x01=white  \x04=green  \x0C=red

    private void OpenMainMenu(IPlayer player, PlayerHudSettings settings)
    {
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle("[ CenterSpeed ]");
        builder.SetPlayerFrozen(false);

        var toggle = new ToggleMenuOption("\x01Speedometer", settings.Enabled, "\x04ON", "\x0COFF");
        toggle.ValueChanged += (_, args) =>
        {
            settings.Enabled = args.NewValue;
            SaveSettings(player.SteamID, settings);
            var id      = player.PlayerID;
            var enabled = args.NewValue;
            Core.Scheduler.NextTick(() =>
            {
                if (enabled)
                    SpawnPlayerHud(player);
                else
                    KillPlayerHud(id);
            });
        };
        builder.AddOption(toggle);

        builder.AddOption(new SubmenuMenuOption("\x04▶ \x01Size",     () => BuildSizeMenu(player, settings)));
        builder.AddOption(new SubmenuMenuOption("\x04▶ \x01Position", () => BuildPositionMenu(player, settings)));

        Core.MenusAPI.OpenMenuForPlayer(player, builder.Build());
    }

    private IMenuAPI BuildSizeMenu(IPlayer player, PlayerHudSettings settings)
    {
        const float ScaleStep = 0.002f;
        const float ScaleMin  = 0.001f;
        const float ScaleMax  = 0.500f;

        // 14-slot block bar, green filled / white empty, value on the right
        const int BarWidth = 14;
        int filled = (int)Math.Round((settings.HudScale - ScaleMin) / (ScaleMax - ScaleMin) * BarWidth);
        filled = Math.Clamp(filled, 0, BarWidth);
        string bar = $"\x04{new string('█', filled)}\x01{new string('░', BarWidth - filled)}  \x040.{(int)(settings.HudScale * 10000):D4}";

        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle("[ Size ]");
        builder.SetPlayerFrozen(false);

        // index 0 — visual bar (clicking it just re-opens the menu)
        var barBtn = new ButtonMenuOption(bar);
        barBtn.Click += (_, _2) =>
        {
            var m = BuildSizeMenu(player, settings);
            Core.MenusAPI.OpenMenuForPlayer(player, m);
            m.MoveToOptionIndex(player, 0);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(barBtn);

        // index 1
        var upBtn = new ButtonMenuOption("\x04[+]  Scale Up");
        upBtn.Click += (_, _2) =>
        {
            settings.HudScale = Math.Clamp(settings.HudScale + ScaleStep, ScaleMin, ScaleMax);
            SaveSettings(player.SteamID, settings);
            var id = player.PlayerID;
            Core.Scheduler.NextTick(() => ApplyHudSettings(id, settings));
            var m = BuildSizeMenu(player, settings);
            Core.MenusAPI.OpenMenuForPlayer(player, m);
            m.MoveToOptionIndex(player, 1);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(upBtn);

        // index 2
        var downBtn = new ButtonMenuOption("\x0C[-]  Scale Down");
        downBtn.Click += (_, _2) =>
        {
            settings.HudScale = Math.Clamp(settings.HudScale - ScaleStep, ScaleMin, ScaleMax);
            SaveSettings(player.SteamID, settings);
            var id = player.PlayerID;
            Core.Scheduler.NextTick(() => ApplyHudSettings(id, settings));
            var m = BuildSizeMenu(player, settings);
            Core.MenusAPI.OpenMenuForPlayer(player, m);
            m.MoveToOptionIndex(player, 2);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(downBtn);

        // index 3
        var backBtn = new ButtonMenuOption("\x01« Back");
        backBtn.Click += (_, _2) =>
        {
            OpenMainMenu(player, settings);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        return builder.Build();
    }

    private IMenuAPI BuildPositionMenu(IPlayer player, PlayerHudSettings settings)
    {
        const float MoveStep = 0.2f;

        float cx = (settings.DigitOffsets[1] + settings.DigitOffsets[2]) / 2f;

        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle("[ Position ]");
        builder.SetPlayerFrozen(false);

        // index 0 — coordinate readout (non-interactive display)
        var coordBtn = new ButtonMenuOption($"\x04X\x01: {cx:+0.00;-0.00;0.00}   \x04Y\x01: {settings.YOffset:+0.00;-0.00;0.00}");
        coordBtn.Click += (_, _2) =>
        {
            var m = BuildPositionMenu(player, settings);
            Core.MenusAPI.OpenMenuForPlayer(player, m);
            m.MoveToOptionIndex(player, 0);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(coordBtn);

        int optionIndex = 1;
        void AddMoveButton(string label, Action applyMove)
        {
            int myIndex = optionIndex++;
            var btn = new ButtonMenuOption(label);
            btn.Click += (_, _2) =>
            {
                applyMove();
                SaveSettings(player.SteamID, settings);
                var id = player.PlayerID;
                Core.Scheduler.NextTick(() => ApplyHudSettings(id, settings));
                var m = BuildPositionMenu(player, settings);
                Core.MenusAPI.OpenMenuForPlayer(player, m);
                m.MoveToOptionIndex(player, myIndex);
                return ValueTask.CompletedTask;
            };
            builder.AddOption(btn);
        }

        // indices 1-4
        AddMoveButton("\x04◄  Left", () =>
        {
            for (var i = 0; i < 4; i++)
                settings.DigitOffsets[i] = Math.Clamp(settings.DigitOffsets[i] - MoveStep, -10f, 10f);
        });
        AddMoveButton("\x04►  Right", () =>
        {
            for (var i = 0; i < 4; i++)
                settings.DigitOffsets[i] = Math.Clamp(settings.DigitOffsets[i] + MoveStep, -10f, 10f);
        });
        AddMoveButton("\x04▲  Up",   () => settings.YOffset = Math.Clamp(settings.YOffset + MoveStep, -10f, 10f));
        AddMoveButton("\x04▼  Down", () => settings.YOffset = Math.Clamp(settings.YOffset - MoveStep, -10f, 10f));

        // index 5
        int centerIndex = optionIndex++;
        var centerBtn = new ButtonMenuOption("\x0C»  Reset to Center");
        centerBtn.Click += (_, _2) =>
        {
            settings.DigitOffsets[0] = _config.DefaultDigitOffsets[0];
            settings.DigitOffsets[1] = _config.DefaultDigitOffsets[1];
            settings.DigitOffsets[2] = _config.DefaultDigitOffsets[2];
            settings.DigitOffsets[3] = _config.DefaultDigitOffsets[3];
            settings.YOffset = _config.DefaultYOffset;
            SaveSettings(player.SteamID, settings);
            var id = player.PlayerID;
            Core.Scheduler.NextTick(() => ApplyHudSettings(id, settings));
            var m = BuildPositionMenu(player, settings);
            Core.MenusAPI.OpenMenuForPlayer(player, m);
            m.MoveToOptionIndex(player, centerIndex);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(centerBtn);

        // index 6
        var backBtn = new ButtonMenuOption("\x01« Back");
        backBtn.Click += (_, _2) =>
        {
            OpenMainMenu(player, settings);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        return builder.Build();
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

        var particleName = _particleConVar?.Value ?? "particles/digits_x/digits_x.vpcf";
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
            //   Slot 0 = CP17: (0, 1, 2) ? alpha=1, self-illum=2
            //   Slot 1 = CP32: (frame, 0, 0) ? digit sprite frame
            //   Slot 2 = CP34: (scale, 0, 0) ? sprite size
            //   Slot 3 = CP33: (xOffset, yOffset, 0) ? screen position
            // CP16 (color) is dropped � white is hardcoded in the particle.
            bool r17 = SetControlPointValue(particle, 17, new Vector(0f, 1f, 2f));
            bool r32 = SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f));
            bool r34 = SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f));
            bool r33 = SetControlPointValue(particle, 33, new Vector(settings.DigitOffsets[i], settings.YOffset, 0f));
            LogDebug("[CenterSpeed][SpawnHUD] SetCP: CP17={R17} CP32={R32} CP34={R34} CP33={R33} offset=({X},{Y})",
                r17, r32, r34, r33, settings.DigitOffsets[i], settings.YOffset);

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

            // Hide from all players first, then schedule removal.
            foreach (var p in Core.PlayerManager.GetAllPlayers())
            {
                if (p == null || !p.IsValid) continue;
                particle.SetTransmitState(false, p.PlayerID);
            }
            particle.AddEntityIOEvent<string>("Kill", null, null, null, 0f);
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

            // Update digit frame (CP32)
            SetControlPointValue(particle, 32, new Vector(_digitMap.GetValueOrDefault(digits[i], 1), 0f, 0f));
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
        player.SendChat($" [HUD] Offsets: 1={o[0]:F2}  2={o[1]:F2}  3={o[2]:F2}  4={o[3]:F2}");
        player.SendChat($" [HUD] Scale: {settings.HudScale:F4}  Y-Offset: {settings.YOffset:F4}  Enabled: {settings.Enabled}");
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

    // -------------------------------------------------------------------------
    // -------------------------------------------------------------------------
    // Settings persistence — Cookies plugin (primary) with JSON fallback

    private const string SettingsFile = "player_settings.json";
    private const string CkScale    = "cs_scale";
    private const string CkYOffset  = "cs_yoffset";
    private const string CkEnabled  = "cs_enabled";
    private const string CkOffset0  = "cs_offset0";
    private const string CkOffset1  = "cs_offset1";
    private const string CkOffset2  = "cs_offset2";
    private const string CkOffset3  = "cs_offset3";

    private void LoadSettings(ulong steamId, PlayerHudSettings settings)
    {
        if (_cookies != null)
        {
            // Cookies are already loaded into memory by the Cookies plugin on connect.
            try
            {
                var dummy = Core.PlayerManager.GetAllPlayers()
                    .FirstOrDefault(p => p.IsValid && p.SteamID == steamId);
                if (dummy != null)
                {
                    settings.HudScale        = _cookies.GetOrDefault<float>(dummy, CkScale,   settings.HudScale);
                    settings.YOffset         = _cookies.GetOrDefault<float>(dummy, CkYOffset, settings.YOffset);
                    settings.Enabled         = _cookies.GetOrDefault<bool> (dummy, CkEnabled, settings.Enabled);
                    settings.DigitOffsets[0] = _cookies.GetOrDefault<float>(dummy, CkOffset0, settings.DigitOffsets[0]);
                    settings.DigitOffsets[1] = _cookies.GetOrDefault<float>(dummy, CkOffset1, settings.DigitOffsets[1]);
                    settings.DigitOffsets[2] = _cookies.GetOrDefault<float>(dummy, CkOffset2, settings.DigitOffsets[2]);
                    settings.DigitOffsets[3] = _cookies.GetOrDefault<float>(dummy, CkOffset3, settings.DigitOffsets[3]);
                }
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning("[CenterSpeed] Cookies load error for {SteamId}: {Msg}", steamId, ex.Message);
            }
            return;
        }

        // JSON fallback
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(GetConfigPath())!, SettingsFile);
            if (!File.Exists(path)) return;

            var dict = JsonSerializer.Deserialize<Dictionary<string, PlayerSettingsData>>(File.ReadAllText(path));
            if (dict == null || !dict.TryGetValue(steamId.ToString(), out var data)) return;

            if (data.Offsets?.Length == 4)
                data.Offsets.CopyTo(settings.DigitOffsets, 0);

            settings.HudScale = data.Scale;
            settings.YOffset  = data.YOffset;
            settings.Enabled  = data.Enabled;
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[CenterSpeed] Failed to load settings for {SteamId}: {Msg}", steamId, ex.Message);
        }
    }

    private void SaveSettings(ulong steamId, PlayerHudSettings settings)
    {
        if (_cookies != null)
        {
            try
            {
                var player = Core.PlayerManager.GetAllPlayers()
                    .FirstOrDefault(p => p.IsValid && p.SteamID == steamId);
                if (player != null)
                {
                    _cookies.Set<float>(player, CkScale,   settings.HudScale);
                    _cookies.Set<float>(player, CkYOffset, settings.YOffset);
                    _cookies.Set<bool> (player, CkEnabled, settings.Enabled);
                    _cookies.Set<float>(player, CkOffset0, settings.DigitOffsets[0]);
                    _cookies.Set<float>(player, CkOffset1, settings.DigitOffsets[1]);
                    _cookies.Set<float>(player, CkOffset2, settings.DigitOffsets[2]);
                    _cookies.Set<float>(player, CkOffset3, settings.DigitOffsets[3]);
                    // Save is async — fire and forget
                    Task.Run(async () =>
                    {
                        try   { await _cookies.Save(player); }
                        catch (Exception ex) { Core.Logger.LogWarning("[CenterSpeed] Cookies save error: {Msg}", ex.Message); }
                    });
                }
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning("[CenterSpeed] Cookies set error for {SteamId}: {Msg}", steamId, ex.Message);
            }
            return;
        }

        // JSON fallback
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(GetConfigPath())!, SettingsFile);

            Dictionary<string, PlayerSettingsData> dict;
            try
            {
                dict = File.Exists(path)
                    ? JsonSerializer.Deserialize<Dictionary<string, PlayerSettingsData>>(File.ReadAllText(path)) ?? new()
                    : new();
            }
            catch { dict = new(); }

            dict[steamId.ToString()] = new PlayerSettingsData(
                (float[])settings.DigitOffsets.Clone(),
                settings.HudScale,
                settings.YOffset,
                settings.Enabled
            );

            File.WriteAllText(path, JsonSerializer.Serialize(dict));
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[CenterSpeed] Failed to save settings for {SteamId}: {Msg}", steamId, ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Config

    private string GetConfigPath() =>
        Path.Combine(
            Path.GetFullPath(Path.Combine(Core.PluginPath, "..", "..")),
            "configs", "plugins", "CenterSpeed", "config.jsonc");

    private void LoadConfig()
    {
        var configPath = GetConfigPath();
        var configDir  = Path.GetDirectoryName(configPath)!;

        if (!File.Exists(configPath))
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(configPath, GenerateDefaultConfigText());
            Core.Logger.LogWarning("[CenterSpeed] Default config created at: {Path}", configPath);
            return;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                ReadCommentHandling     = JsonCommentHandling.Skip,
                AllowTrailingCommas     = true,
                PropertyNameCaseInsensitive = true,
            };
            var loaded = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(configPath), options);
            if (loaded != null)
            {
                _config = loaded;
                if (_config.DefaultDigitOffsets?.Length != 4)
                    _config.DefaultDigitOffsets = new[] { -1.5f, -0.5f, 0.5f, 1.5f };
            }
            Core.Logger.LogWarning("[CenterSpeed] Config loaded. Debug={Debug} EnableDatabase={DB}",
                _config.Debug, _config.EnableDatabase);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[CenterSpeed] Failed to load config, using defaults: {Msg}", ex.Message);
        }
    }

    private static string GenerateDefaultConfigText() =>
        """
        {
            // CenterSpeed Configuration
            // Configuration schema version - do not modify.
            "ConfigVersion": "1.0.1",

            // Default HUD scale for new players (recommended: 0.001 to 0.500).
            "DefaultScale": 0.04,

            // Default vertical offset for new players.
            // Negative values move the HUD upward, positive values downward.
            "DefaultYOffset": -1.0,

            // Default horizontal digit offsets for new players [D1, D2, D3, D4].
            "DefaultDigitOffsets": [ -1.5, -0.5, 0.5, 1.5 ],

            // Enable database storage for player settings.
            // true  = use Cookies plugin (requires Cookies plugin to be installed)
            // false = use local JSON file
            "EnableDatabase": true,

            // Enable verbose debug logging to the server console.
            // false = silent (recommended for production servers)
            // true  = verbose output for troubleshooting
            "Debug": false
        }
        """;

}
