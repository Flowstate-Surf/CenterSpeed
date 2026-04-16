using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cookies.Contract;
using SwiftlyS2.Shared.Players;

namespace CenterSpeed;

public sealed partial class CenterSpeed
{
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
                ReadCommentHandling         = JsonCommentHandling.Skip,
                AllowTrailingCommas         = true,
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
