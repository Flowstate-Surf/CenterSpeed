using System;
using System.Threading.Tasks;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;

namespace CenterSpeed;

public sealed partial class CenterSpeed
{
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
}
