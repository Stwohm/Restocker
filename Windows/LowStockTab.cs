using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Restocker.Common;
using Restocker.Execution;
using Restocker.Plan;

namespace Restocker.Windows;

/// <summary>Fork: watched items, their listed / available units, alert threshold and a Restock button.</summary>
public sealed class LowStockTab
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly ConfirmDialog confirmDialog;
    private readonly LowStockService service;
    private string search = string.Empty;
    private readonly Dictionary<(uint, bool), int> restockTo = new();
    private string? lastMessage;

    public LowStockTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog, LowStockService service)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.confirmDialog = confirmDialog;
        this.service = service;
    }

    private static string ItemName(uint id)
        => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}";

    public void Draw()
    {
        DrawAdd();
        ImGui.Separator();
        if (!string.IsNullOrEmpty(lastMessage)) ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.8f, 0.4f, 1f), lastMessage);
        DrawTable();
    }

    /// <summary>Search over items we list or hold (retainers, bag, saddlebag) and add them to the watch list.</summary>
    private void DrawAdd()
    {
        ImGui.TextUnformatted("Watch an item:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##lowstock-search", "Search item you list or hold", ref search, 64);
        if (search.Length < 2) return;

        var known = configuration.Snapshots.Values.SelectMany(s => s.Listings.Select(l => (l.ItemId, l.IsHQ))
                .Concat(s.Inventory.Select(i => (i.ItemId, i.IsHQ))))
            .Concat(configuration.Characters.Values.SelectMany(c => c.Bag.Concat(c.Saddlebag).Concat(c.PremiumSaddlebag)
                .Where(i => i.IsListable).Select(i => (i.ItemId, i.IsHQ))))
            .Distinct()
            .Select(k => (k.ItemId, k.IsHQ, Name: ItemName(k.ItemId)))
            .Where(k => k.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(k => !configuration.LowStock.Any(e => e.ItemId == k.ItemId && e.IsHq == k.IsHQ))
            .OrderBy(k => k.Name).Take(10).ToList();

        foreach (var k in known)
        {
            if (ImGui.SmallButton($"+ {k.Name}{(k.IsHQ ? " (HQ)" : "")}##add-{k.ItemId}-{k.IsHQ}"))
            {
                configuration.LowStock.Add(new LowStockEntry { ItemId = k.ItemId, IsHq = k.IsHQ });
                configuration.Save();
                search = string.Empty;
            }
        }
        if (known.Count == 0) ImGui.TextDisabled("no match among items on your retainers / bag / saddlebag (open the bell once to refresh)");
    }

    private void DrawTable()
    {
        if (configuration.LowStock.Count == 0)
        {
            ImGui.TextDisabled("No watched items. Alerts go to the Restocky chat channel when listed units drop to the threshold.");
            return;
        }

        var bellOpen = AddonHelper.IsOpen("RetainerList");
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##lowstock", 9, flags, new System.Numerics.Vector2(0, Math.Max(120f, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Available", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Alert ≤", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Per listing", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Restock to", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("##restock", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableHeadersRow();

        LowStockEntry? remove = null;
        foreach (var e in configuration.LowStock)
        {
            var id = $"{e.ItemId}-{e.IsHq}";
            var listed = service.ListedUnits(e.ItemId, e.IsHq);
            var avail = service.Sources(e.ItemId, e.IsHq).Sum(s => s.Available);
            var low = listed <= e.Threshold;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (low) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.4f, 1f), ItemName(e.ItemId) + (e.IsHq ? " (HQ)" : ""));
            else ImGui.TextUnformatted(ItemName(e.ItemId) + (e.IsHq ? " (HQ)" : ""));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(listed.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(avail.ToString());

            ImGui.TableNextColumn();
            var th = e.Threshold;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##th-{id}", ref th, 0)) { e.Threshold = Math.Max(0, th); configuration.Save(); }

            ImGui.TableNextColumn();
            var per = e.PerListing;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##per-{id}", ref per, 0)) { e.PerListing = Math.Max(0, per); configuration.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"0 = same as your current listings ({service.PerListing(e)})");

            ImGui.TableNextColumn();
            var price = (int)Math.Min(e.Price, int.MaxValue);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##price-{id}", ref price, 0)) { e.Price = Math.Max(0, price); configuration.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"0 = your current listing price, else market lowest -undercut (now {service.Price(e):N0})");

            ImGui.TableNextColumn();
            var to = restockTo.TryGetValue((e.ItemId, e.IsHq), out var t) ? t : e.Target;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##to-{id}", ref to, 0))
            {
                to = Math.Max(0, to);
                restockTo[(e.ItemId, e.IsHq)] = to;
                e.Target = to;
                configuration.Save();
            }

            ImGui.TableNextColumn();
            var can = bellOpen && !executor.IsRunning && to > listed;
            if (!can) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Restock##rs-{id}")) Restock(e, to);
            if (!can) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(bellOpen ? $"List up to {to} units from retainer inventory, bag or saddlebag" : "open the summoning bell first");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##rm-{id}")) remove = e;
        }
        ImGui.EndTable();

        if (remove != null)
        {
            configuration.LowStock.Remove(remove);
            configuration.Save();
        }
    }

    private void Restock(LowStockEntry e, int target)
    {
        var actions = service.PlanRestock(e, target);
        if (actions.Count == 0)
        {
            var why = service.Price(e) <= 0 ? "no price (set one, or fetch market prices first)"
                : service.Sources(e.ItemId, e.IsHq).Count == 0 ? "none in retainer inventories / bag / saddlebag"
                : "no free market slot";
            lastMessage = $"{ItemName(e.ItemId)}: nothing to list: {why}";
            return;
        }
        lastMessage = null;
        confirmDialog.Request(actions, list => executor.StartApplyActions(list));
    }
}
