using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CenterSpeed;

[PluginMetadata(Id = "centerspeed", Version = "1.0.0", Name = "CenterSpeed", Author = "Lethal & Retro", Description = "Center-screen particle speedometer HUD")]
public sealed class CenterSpeed : BasePlugin
{
    // -------------------------------------------------------------------------
    // State

    private readonly PlayerHudState?[]    _huds           = new PlayerHudState?[65];
    private readonly PlayerHudSettings?[] _playerSettings = new PlayerHudSettings?[65];
    private readonly float[]              _lastSpeed      = new float[65];

    private CBaseEntity?     _sharedTarget;
    private IConVar<string>? _particleConVar;
    private int              _tickCounter;

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
        public float[] DigitOffsets { get; set; } = { -1.4f, -0.45f, 0.45f, 1.4f };
        public float   HudScale     { get; set; } = 0.04f;
        public float   YOffset      { get; set; } = -1f;
        public bool    Enabled      { get; set; } = false;
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
        _particleConVar = Core.ConVar.Create<string>(
            "cs_speed_particle",
            "Particle file for center-speed HUD",
            "particles/digits_x/digits_x.vpcf"
        );
    }

    public override void Unload()
    {
        for (var i = 0; i < 65; i++)
            KillPlayerHud(i);

        if (_sharedTarget?.IsValidEntity == true)
            _sharedTarget.AcceptInput<string>("DestroyImmediately", null);

        _sharedTarget = null;
    }

    // -------------------------------------------------------------------------
    // Framework event listeners

    [EventListener<EventDelegates.OnPrecacheResource>]
    public void OnPrecacheResource(IOnPrecacheResourceEvent @event)
    {
        var assetPath = Path.Combine(Core.PluginPath, "assets");
        if (!Directory.Exists(assetPath)) return;

        foreach (var file in Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories))
        {
            var relative = file[(assetPath.Length + 1)..].Replace("\\", "/");
            if (!relative.StartsWith("particles/", StringComparison.OrdinalIgnoreCase))
                continue;

            var asset = relative.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                ? relative[..^2]
                : relative;

            @event.AddItem(asset);
        }
    }

    [EventListener<EventDelegates.OnMapUnload>]
    public void OnMapUnload(IOnMapUnloadEvent @event)
    {
        // Drop entity references — the engine has already cleaned them up.
        for (var i = 0; i < 65; i++)
            _huds[i] = null;

        _sharedTarget = null;
    }

    [EventListener<EventDelegates.OnTick>]
    public void OnTick()
    {
        // Throttle updates to every 10 ticks (~6 Hz at 64 tick).
        if (++_tickCounter % 10 != 0) return;

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

        var settings = new PlayerHudSettings();
        LoadSettings(player.SteamID, settings);
        _playerSettings[id] = settings;

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
        if (context.Sender is not IPlayer player) return;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return;

        var settings = _playerSettings[id] ??= new PlayerHudSettings();

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
                    player.SendChat(" [HUD] Usage: !hudsettings scale <0-10>");
                    return;
                }

                settings.HudScale = Math.Clamp(value, 0f, 10f);
                SaveSettings(player.SteamID, settings);
                SpawnPlayerHud(player);
                player.SendChat($" [HUD] Scale set to {settings.HudScale:F2}");
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

    // -------------------------------------------------------------------------
    // HUD management

    private void SpawnPlayerHud(IPlayer player)
    {
        if (!player.IsValid || player.IsFakeClient) return;

        var id = player.PlayerID;
        if (id < 0 || id >= 65) return;

        // Only spawn for players on playing teams (T=2, CT=3).
        if (player.Controller?.TeamNum < 2) return;

        KillPlayerHud(id);

        var settings = _playerSettings[id] ??= new PlayerHudSettings();
        if (!settings.Enabled) return;

        // Create (or reuse) the one shared info_target that all particles reference.
        if (_sharedTarget == null || !_sharedTarget.IsValidEntity)
        {
            var target = Core.EntitySystem.CreateEntityByDesignerName<CBaseEntity>("info_target");
            if (target == null)
            {
                Core.Logger.LogWarning("[CenterSpeed] Failed to create shared info_target");
                return;
            }
            target.DispatchSpawn();
            _sharedTarget = target;
        }

        var particleName = _particleConVar?.Value ?? "particles/digits_x/digits_x.vpcf";
        var state        = new PlayerHudState { OwnerSlot = id };

        for (var i = 0; i < 4; i++)
        {
            var particle = Core.EntitySystem.CreateEntityByDesignerName<CParticleSystem>("info_particle_system");
            if (particle == null)
            {
                Core.Logger.LogWarning("[CenterSpeed] Failed to spawn particle digit {Index} for slot {Slot}", i, id);
                continue;
            }

            particle.EffectName  = particleName;
            particle.StartActive = false;
            particle.DispatchSpawn();

            // CP17 → shared info_target (particle anchor entity)
            particle.ControlPointEnts[17] = Core.EntitySystem.GetRefEHandle<CBaseEntity>(_sharedTarget);
            particle.ControlPointEntsUpdated();

            // DataCP 33 carries the per-digit horizontal + vertical offset
            particle.DataCP      = 33;
            particle.DataCPValue = new Vector(settings.DigitOffsets[i], settings.YOffset, 0f);

            // CP32 = digit frame index, CP34 = scale, CP16 = color (white)
            SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f));
            SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f));
            SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f));

            particle.AcceptInput<string>("Start", null);
            particle.Active = true;
            particle.ActiveUpdated();

            // Hidden for everyone by default; per-tick will expose to the owner.
            particle.SetTransmitState(false);

            state.Digits[i] = particle;
        }

        _huds[id] = state;

        // Apply initial transmit so the owner sees the HUD immediately.
        ApplyTransmit(id, state, settings);
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

            particle.SetTransmitState(false);
            particle.AcceptInput<string>("Stop", null);
            particle.AcceptInput<string>("DestroyImmediately", null);
            particle.Active = false;
            particle.ActiveUpdated();

            // Defer final Despawn to let the engine propagate the stop.
            Core.Scheduler.DelayBySeconds(0.1f, () =>
            {
                if (particle.IsValidEntity)
                    particle.Despawn();
            });
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

            // Digit frame
            SetControlPointValue(particle, 32, new Vector(_digitMap.GetValueOrDefault(digits[i], 1), 0f, 0f));

            // Color: red = slowing, green = speeding, white = steady
            if (_lastSpeed[id] > speed)
                SetControlPointValue(particle, 16, new Vector(255f, 0f, 0f));
            else if (_lastSpeed[id] < speed)
                SetControlPointValue(particle, 16, new Vector(0f, 255f, 0f));
            else
                SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f));
        }

        _lastSpeed[id] = speed;

        // Keep transmit in sync every update cycle.
        ApplyTransmit(id, state, settings);
    }

    private void ApplyTransmit(int ownerSlot, PlayerHudState state, PlayerHudSettings settings)
    {
        bool shouldSeeOwner = settings.Enabled && !state.IsDisposed;

        foreach (var p in Core.PlayerManager.GetAllPlayers())
        {
            if (p == null || !p.IsValid) continue;

            bool visible = shouldSeeOwner && p.PlayerID == ownerSlot;

            foreach (var particle in state.Digits)
            {
                if (particle == null || !particle.IsValidEntity) continue;
                particle.SetTransmitState(visible, p.PlayerID);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static void PrintHudSettings(IPlayer player, PlayerHudSettings settings)
    {
        var o = settings.DigitOffsets;
        player.SendChat($" [HUD] Offsets: 1={o[0]:F2}  2={o[1]:F2}  3={o[2]:F2}  4={o[3]:F2}");
        player.SendChat($" [HUD] Scale: {settings.HudScale:F4}  Y-Offset: {settings.YOffset:F4}  Enabled: {settings.Enabled}");
    }

    /// <summary>
    /// Writes cpIndex+value into the first available server-CP slot (slot 255 = unassigned).
    /// </summary>
    private bool SetControlPointValue(CParticleSystem particle, int cpIndex, Vector value)
    {
        for (var i = 0; i < 4; i++)
        {
            if (particle.ServerControlPointAssignments[i] == cpIndex ||
                particle.ServerControlPointAssignments[i] == 255)
            {
                particle.ServerControlPointAssignments[i] = (byte)cpIndex;
                particle.ServerControlPoints[i]           = value;
                particle.ServerControlPointAssignmentsUpdated();
                particle.ServerControlPointsUpdated();
                return true;
            }
        }

        Core.Logger.LogWarning("[CenterSpeed] No free server control-point slot for CP {CpIndex}", cpIndex);
        return false;
    }

    // -------------------------------------------------------------------------
    // JSON settings persistence (replaces ClientPreferences)

    private const string SettingsFile = "player_settings.json";

    private void LoadSettings(ulong steamId, PlayerHudSettings settings)
    {
        try
        {
            var path = Path.Combine(Core.PluginDataDirectory, SettingsFile);
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
        try
        {
            var path = Path.Combine(Core.PluginDataDirectory, SettingsFile);

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
}
