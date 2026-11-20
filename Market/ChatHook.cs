using System;
using System.Linq;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace Restocker.Market;

/// <summary>RetainerSale chat subscription, isolated because the chat API shape changes between Dalamud versions.</summary>
internal static class ChatHook
{
    private static Action<uint, bool, string>? onSale;

    public static void Subscribe(IChatGui chat, Action<uint, bool, string> handler)
    {
        onSale = handler;
        chat.ChatMessage += OnChat;
    }

    public static void Unsubscribe(IChatGui chat)
    {
        chat.ChatMessage -= OnChat;
        onSale = null;
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
            var item = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
            if (item == null) return;
            onSale?.Invoke(item.ItemId, item.IsHQ, message.TextValue);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Restocker] RetainerSale parse failed");
        }
    }
}
