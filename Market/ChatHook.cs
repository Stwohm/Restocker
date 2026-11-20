using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace Restocker.Market;

/// <summary>RetainerSale chat subscription, isolated because the chat API shape changes between Dalamud versions.</summary>
internal static class ChatHook
{
    private static Action<uint, bool, string>? onGameSale;
    private static Action<IReadOnlyList<uint>, int, string, long>? onAllaganSale;
    private static IDataManager? dataManager;
    private static Dictionary<string, IReadOnlyList<uint>>? itemIdsByName;

    public static void Subscribe(
        IChatGui chat,
        IDataManager data,
        Action<uint, bool, string> gameHandler,
        Action<IReadOnlyList<uint>, int, string, long> allaganHandler)
    {
        dataManager = data;
        itemIdsByName = null;
        onGameSale = gameHandler;
        onAllaganSale = allaganHandler;
        chat.ChatMessage += OnChat;
    }

    public static void Unsubscribe(IChatGui chat)
    {
        chat.ChatMessage -= OnChat;
        onGameSale = null;
        onAllaganSale = null;
        dataManager = null;
    }

    private static void OnChat(Dalamud.Game.Chat.IHandleableChatMessage m)
    {
        if (m.LogKind != XivChatType.RetainerSale) return;
        Handle(m.Message);
    }

    private static void Handle(SeString message)
    {
        try
        {
            if (message.TextValue.StartsWith("[Restocky]")) return;
            var item = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
            if (item != null)
            {
                onGameSale?.Invoke(item.ItemId, item.IsHQ, message.TextValue);
                return;
            }

            if (!AllaganSaleMessage.TryParse(message.TextValue, out var qty, out var itemName, out var grossGil))
                return;
            onAllaganSale?.Invoke(ResolveItemIds(itemName), qty, itemName, grossGil);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Restocker] RetainerSale parse failed");
        }
    }

    private static IReadOnlyList<uint> ResolveItemIds(string itemName)
    {
        if (itemIdsByName == null)
        {
            var building = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            if (dataManager != null)
            {
                foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>())
                {
                    var name = row.Name.ExtractText();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!building.TryGetValue(name, out var ids)) building[name] = ids = new List<uint>();
                    ids.Add(row.RowId);
                }
            }
            itemIdsByName = building.ToDictionary(p => p.Key, p => (IReadOnlyList<uint>)p.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        return itemIdsByName.TryGetValue(itemName, out var result) ? result : Array.Empty<uint>();
    }
}
