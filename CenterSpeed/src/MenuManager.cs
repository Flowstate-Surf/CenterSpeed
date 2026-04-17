using System;
using System.Threading.Tasks;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;

namespace CenterSpeed;

public sealed partial class CenterSpeed
{
    private void OpenMainMenu(IPlayer player, PlayerHudSettings settings)
    {
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle("[ CenterSpeed ]");
        builder.SetPlayerFrozen(false);

        var toggle = new ToggleMenuOption("<font color='#FFD700'>Speedometer</font>", settings.Enabled, "<font color='#00FF00'>● ON</font>", "<font color='#FF4444'>● OFF</font>");
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

        builder.AddOption(new SubmenuMenuOption("<font color='#FFD700'>▶</font>  Size",     () => BuildSizeMenu(player, settings)));
        builder.AddOption(new SubmenuMenuOption("<font color='#FFD700'>▶</font>  Position", () => BuildPositionMenu(player, settings)));

        Core.MenusAPI.OpenMenuForPlayer(player, builder.Build());
    }

    private IMenuAPI BuildSizeMenu(IPlayer player, PlayerHudSettings settings)
    {
        const float ScaleStep = 0.002f;
        const float ScaleMin  = 0.001f;
        const float ScaleMax  = 0.500f;

        // Title is the live progress bar: gold filled, grey empty, lime value
        const int BarWidth = 14;
        int filled = (int)Math.Round((settings.HudScale - ScaleMin) / (ScaleMax - ScaleMin) * BarWidth);
        filled = Math.Clamp(filled, 0, BarWidth);
        string title = $"<font color='#FFD700'>{new string('█', filled)}</font><font color='#888888'>{new string('░', BarWidth - filled)}</font>  <font color='#00FF00'>{settings.HudScale:F4}</font>";

        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(title);
        builder.SetPlayerFrozen(false);

        // index 0 — Scale Up
        var upBtn = new ButtonMenuOption("<font color='#00FF00'>[+]  Scale Up</font>");
        upBtn.Click += (_, _2) =>
        {
            settings.HudScale = Math.Clamp(settings.HudScale + ScaleStep, ScaleMin, ScaleMax);
            SaveSettings(player.SteamID, settings);
            var id = player.PlayerID;
            Core.Scheduler.NextTick(() => ApplyHudSettings(id, settings));
            var m = BuildSizeMenu(player, settings);
            Core.MenusAPI.OpenMenuForPlayer(player, m);
            m.MoveToOptionIndex(player, 0);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(upBtn);

        // index 1 — Scale Down
        var downBtn = new ButtonMenuOption("<font color='#FF4444'>[-]  Scale Down</font>");
        downBtn.Click += (_, _2) =>
        {
            settings.HudScale = Math.Clamp(settings.HudScale - ScaleStep, ScaleMin, ScaleMax);
            SaveSettings(player.SteamID, settings);
            var id = player.PlayerID;
            Core.Scheduler.NextTick(() => ApplyHudSettings(id, settings));
            var m = BuildSizeMenu(player, settings);
            Core.MenusAPI.OpenMenuForPlayer(player, m);
            m.MoveToOptionIndex(player, 1);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(downBtn);

        // index 2 — Back
        var backBtn = new ButtonMenuOption("<font color='#888888'>« Back</font>");
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

        // Title shows live X/Y coordinates
        float cx = (settings.DigitOffsets[1] + settings.DigitOffsets[2]) / 2f;
        string title = $"<font color='#FFD700'>X</font>: <font color='#ADD8E6'>{cx:+0.00;-0.00;0.00}</font>   <font color='#FFD700'>Y</font>: <font color='#ADD8E6'>{settings.YOffset:+0.00;-0.00;0.00}</font>";

        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(title);
        builder.SetPlayerFrozen(false);

        int optionIndex = 0;
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

        // indices 0-3 — directional buttons
        AddMoveButton("<font color='#ADD8E6'>◄  Left</font>", () =>
        {
            for (var i = 0; i < 4; i++)
                settings.DigitOffsets[i] = Math.Clamp(settings.DigitOffsets[i] - MoveStep, -10f, 10f);
        });
        AddMoveButton("<font color='#ADD8E6'>►  Right</font>", () =>
        {
            for (var i = 0; i < 4; i++)
                settings.DigitOffsets[i] = Math.Clamp(settings.DigitOffsets[i] + MoveStep, -10f, 10f);
        });
        AddMoveButton("<font color='#ADD8E6'>▲  Up</font>",   () => settings.YOffset = Math.Clamp(settings.YOffset + MoveStep, -10f, 10f));
        AddMoveButton("<font color='#ADD8E6'>▼  Down</font>", () => settings.YOffset = Math.Clamp(settings.YOffset - MoveStep, -10f, 10f));

        // index 4 — Reset to Center
        int centerIndex = optionIndex++;
        var centerBtn = new ButtonMenuOption("<font color='#FFA500'>↺  Reset to Center</font>");
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

        // index 5 — Back
        var backBtn = new ButtonMenuOption("<font color='#888888'>« Back</font>");
        backBtn.Click += (_, _2) =>
        {
            OpenMainMenu(player, settings);
            return ValueTask.CompletedTask;
        };
        builder.AddOption(backBtn);

        return builder.Build();
    }
}
