using System;
using System.Globalization;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace CenterSpeed;

public sealed partial class CenterSpeed
{
    [Command("hudsettings")]
    public void OnHudSettingsCommand(ICommandContext context)
    {
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

        var settings = _playerSettings[id] ??= NewDefaultSettings();
        OpenMainMenu(player, settings);
    }
}
