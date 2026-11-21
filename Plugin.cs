using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Restocker.Windows;
using Restocker.Localization;
using Restocker.Execution;
using Restocker.Market;

namespace Restocker;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    internal RetainerHistoryCapture SaleHistory { get; }

    private const string CommandName = "/restocky";

    public static Configuration Configuration { get; private set; } = null!;
    public static Plugin Instance { get; private set; } = null!;
    public RetainerWatcher RetainerWatcher { get; private set; } = null!;
    public readonly WindowSystem WindowSystem = new("Restocky");

    private MainWindow MainWindow { get; }
    private ProgressWindow ProgressWindow { get; }
    private BellWatcher BellWatcher { get; }
    private AutoRetainerDetector ArDetector { get; }
    private Executor Executor { get; }
    private MarketWatcher MarketWatcher { get; }
    private UndercutHistoryHost Undercuts { get; }
    internal UndercutHistory UndercutHistory => Undercuts.History;
    private AutoCycleController AutoCycle { get; }
    public TrackerHost Tracker { get; }
    public Restocker.Plan.LowStockService LowStock { get; private set; } = null!;

    public Plugin()
    {
        Instance = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Strings.SetLanguage(Configuration.ResolveLanguage(ClientState.ClientLanguage));

        MarketWatcher = new MarketWatcher(MarketBoard, Log);
        Undercuts = new UndercutHistoryHost(MarketWatcher, Configuration, Framework, PlayerState, ObjectTable, Log,
            PluginInterface.GetPluginConfigDirectory());
        Tracker = new TrackerHost(Framework, ChatGui, DataManager, Log, PluginInterface.GetPluginConfigDirectory());
        SaleHistory = new RetainerHistoryCapture(Tracker.Tracker);
        MarketWatcher.CompetitorsObserved += (item, hq, offers) =>
            Tracker.Tracker.ObserveCompetitors(item, hq, offers, DateTime.UtcNow);
        Executor = new Executor(Framework, Log, Configuration, MarketWatcher.Cache);
        Executor.History = new PriceHistoryLog(
            System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "price-history.csv"));

        ArDetector = new AutoRetainerDetector(PluginInterface, NotificationManager, Log);
        var arIpc = new AutoRetainerIpc(PluginInterface, Log, () => ArDetector.IsAutoRetainerLoaded());
        AutoCycle = new AutoCycleController(
            Framework, Condition, ObjectTable, ChatGui, Log, Executor, Configuration,
            () => ArDetector.IsAutoRetainerLoaded(), arIpc);

        LowStock = new Restocker.Plan.LowStockService(Framework, Configuration, Tracker.Tracker, MarketWatcher.Cache);
        MainWindow = new MainWindow(Configuration, Executor, MarketWatcher, AutoCycle);
        WindowSystem.AddWindow(MainWindow);
        ProgressWindow = new ProgressWindow(Executor);
        WindowSystem.AddWindow(ProgressWindow);

        RetainerWatcher = new RetainerWatcher(
            Configuration,
            AddonLifecycle,
            PlayerState,
            ObjectTable,
            DataManager,
            Log
        );

        BellWatcher = new BellWatcher(
            Framework,
            GameGui,
            open => MainWindow.IsOpen = open,
            () => Configuration.AutoOpenOnBell,
            () => RetainerWatcher.CaptureCharacterSnapshot()
        );

        ArDetector.WarnIfPresent();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Restocky window."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        // 設定タブは MainWindow 内にあるので OpenConfigUi も同じウィンドウへ
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        AutoCycle.Dispose();
        LowStock.Dispose();
        MarketWatcher.Dispose();
        SaleHistory.Dispose();
        Undercuts.Dispose();
        Tracker.Dispose();
        Executor.Dispose();
        BellWatcher.Dispose();
        RetainerWatcher.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    public void ToggleMainUi() => MainWindow.Toggle();

    /// <summary>Fork: every Restocky chat line goes through here, on the configured channel.</summary>
    public static void ChatPrint(string message)
    {
        try
        {
            ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
            {
                Type = (Dalamud.Game.Text.XivChatType)(Configuration?.ChatChannel ?? (int)Dalamud.Game.Text.XivChatType.RetainerSale),
                Message = $"[Restocky] {message}",
            });
        }
        catch (Exception ex) { Log.Warning(ex, "[Restocker] chat print failed"); }
    }

    /// <summary>Executor 用: 自動 fetch 中に「期待アイテム」フィルタを設定。</summary>
    public void SetMarketWatcherExpected(uint itemId, bool isHq) => MarketWatcher.SetExpectedItem(itemId, isHq);

    public void ClearMarketWatcherExpected() => MarketWatcher.ClearExpectedItem();
}
