using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Restocker.Common;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly RepriceTab repriceTab;
    private readonly ListTab listTab;
    private readonly ConfirmDialog confirmDialog = new();

    public MainWindow(Configuration configuration, Executor executor, MarketWatcher marketWatcher, AutoCycleController autoCycle)
        : base($"{Strings.WindowTitle}##MainWindow",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.repriceTab = new RepriceTab(configuration, executor, confirmDialog, marketWatcher.Cache, autoCycle);
        this.listTab = new ListTab(configuration, executor, confirmDialog);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 420),
            MaximumSize = new Vector2(2400, 1600),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        confirmDialog.Draw();
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##restocker-tabs"))
        {
            if (ImGui.BeginTabItem(Strings.TabReprice))
            {
                repriceTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Strings.TabList))
            {
                listTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Strings.TabSettings))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        var snapshots = configuration.Snapshots.Values.ToList();
        var freshest = snapshots.Count == 0
            ? Strings.HeaderUnknown
            : snapshots.Max(s => s.LastRefreshedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        ImGui.TextUnformatted($"{Strings.HeaderCachedRetainers} {snapshots.Count} {Strings.HeaderRetainers} / {Strings.HeaderLastUpdated} {freshest}");

        ImGui.SameLine();
        if (executor.IsRunning)
        {
            var statusLabel = executor.Mode == ExecutionMode.RefreshAll
                ? string.Format(Strings.HeaderRefreshing, executor.CompletedJobs, executor.TotalJobs)
                : string.Format(Strings.ApplyProgress, executor.CompletedActions, executor.TotalActions, executor.CompletedJobs, executor.TotalJobs);
            ImGui.TextUnformatted(statusLabel);
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.HeaderStop)) executor.Cancel();
            if (!string.IsNullOrEmpty(executor.CurrentStateLabel))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"[{executor.CurrentStateLabel}]");
            }
        }
        else
        {
            var bellOpen = AddonHelper.IsOpen("RetainerList");
            if (!bellOpen) ImGui.BeginDisabled();
            if (ImGui.SmallButton(Strings.HeaderRefreshAll))
            {
                if (Executor.ActiveRetainersInDisplayOrder().Count == 0)
                {
                    Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
                    {
                        Title = Strings.WindowTitle,
                        Content = Strings.ToastNoRetainersInBell,
                        Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
                    });
                }
                else
                {
                    executor.StartRefreshAll();
                }
            }
            if (!bellOpen) ImGui.EndDisabled();
            if (!bellOpen)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(" + Strings.ToastBellNotOpen + ")");
            }
        }

        // 直近の停止理由を見える化
        if (!string.IsNullOrEmpty(executor.StatusMessage))
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.4f, 1f), $"⚠ {executor.StatusMessage}");
        }
    }

    private void DrawSettingsTab()
    {
        var autoOpen = configuration.AutoOpenOnBell;
        if (ImGui.Checkbox(Strings.SettingsAutoOpenOnBell, ref autoOpen))
        {
            configuration.AutoOpenOnBell = autoOpen;
            configuration.Save();
        }

        ImGui.Spacing();

        var langIdx = configuration.Language;
        var labels = new[]
        {
            Strings.SettingsLanguageAuto,
            Strings.LanguageNameEnglish,
            Strings.LanguageNameJapanese,
            Strings.LanguageNameGerman,
            Strings.LanguageNameFrench,
            Strings.LanguageNameChinese,
            Strings.LanguageNameKorean,
        };
        // langIdx: -1 = auto / 0..5 = explicit。コンボの 0 番目を auto にマップする。
        var combo = langIdx + 1;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo(Strings.SettingsLanguage, ref combo, labels, labels.Length))
        {
            configuration.Language = combo - 1;
            configuration.Save();
            Strings.SetLanguage(configuration.ResolveLanguage(Plugin.ClientState.ClientLanguage));
        }

        DrawForkSettings();
    }

    // ---------------- Fork (Restocky) settings ----------------

    private string floorFilter = string.Empty;

    private void DrawForkSettings()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Full / auto cycle");

        var undercut = configuration.UndercutGil;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Undercut (gil below lowest competitor)", ref undercut))
        {
            configuration.UndercutGil = Math.Max(0, undercut);
            configuration.Save();
        }

        var maxDrop = (float)configuration.MaxDropPercent;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Max drop % per change (0 = off)", ref maxDrop, 1f, 5f, "%.0f"))
        {
            configuration.MaxDropPercent = Math.Clamp(maxDrop, 0f, 100f);
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A new price more than this % below the current one is NOT applied; it is logged as max_drop.");

        var interval = configuration.AutoCycleIntervalMinutes;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Auto cycle interval (minutes)", ref interval))
        {
            configuration.AutoCycleIntervalMinutes = Math.Max(1, interval);
            configuration.Save();
        }

        var jitter = configuration.AutoCycleJitterSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Random jitter (+/- seconds)", ref jitter))
        {
            configuration.AutoCycleJitterSeconds = Math.Max(0, jitter);
            configuration.Save();
        }

        var maxFail = configuration.AutoCycleMaxFailures;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Stop after N failed cycles in a row", ref maxFail))
        {
            configuration.AutoCycleMaxFailures = Math.Max(1, maxFail);
            configuration.Save();
        }

        ImGui.TextDisabled("Price history: pluginConfigs\\Restocky\\price-history.csv");

        ImGui.Spacing();
        ImGui.TextUnformatted("Price floors (per item, HQ and NQ share it; 0 = none)");
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##floor-filter", Strings.Filter, ref floorFilter, 64);

        // Items currently listed on any retainer (from snapshots) plus any item that already has a floor.
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var ids = configuration.Snapshots.Values.SelectMany(s => s.Listings).Select(l => l.ItemId)
            .Concat(configuration.PriceFloors.Keys).Distinct()
            .Select(id => (Id: id, Name: sheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}"))
            .Where(x => floorFilter.Length == 0 || x.Name.Contains(floorFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name)
            .ToList();

        if (ids.Count == 0)
        {
            ImGui.TextDisabled(Strings.EmptyHint);
            return;
        }
        if (!ImGui.BeginTable("##floors", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 260))) return;
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Floor", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableHeadersRow();
        foreach (var (id, name) in ids)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            var v = (int)Math.Min(configuration.FloorFor(id), int.MaxValue);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##floor-{id}", ref v, 0))
            {
                if (v <= 0) configuration.PriceFloors.Remove(id);
                else configuration.PriceFloors[id] = v;
                configuration.Save();
            }
        }
        ImGui.EndTable();
    }
}
