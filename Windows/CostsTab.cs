using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

/// <summary>Fork: search remaining cost lots and record manual acquisitions.</summary>
internal sealed class CostsTab(CostLedger ledger)
{
    private string filter = string.Empty;
    private string search = string.Empty;
    private (uint Id, string Name)[]? itemNames;
    private (uint Id, string Name)[] matches = [];
    private (uint Id, string Name) selected;
    private bool hq;
    private int quantity = 1;
    private string unitCost = "0";

    public void Draw()
    {
        if (ImGui.CollapsingHeader(Strings.CostAddManual + "###cost-manual"))
        {
            DrawManualLot();
            ImGui.Separator();
        }

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##cost-filter", Strings.Filter, ref filter, 64);
        DrawLots();
    }

    private void DrawManualLot()
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##cost-item-search", Strings.CostSearchItems, ref search, 64))
        {
            selected = default;
            matches = search.Trim().Length < 2 ? [] : SearchItems(search.Trim());
        }

        if (selected.Id == 0)
        {
            foreach (var item in matches)
            {
                if (!ImGui.SmallButton($"{item.Name}##cost-item-{item.Id}")) continue;
                selected = item;
                break;
            }
            if (search.Trim().Length >= 2 && matches.Length == 0) ImGui.TextDisabled(Strings.CostNoMatchingItems);
        }
        if (selected.Id == 0) return;

        ImGui.TextUnformatted(selected.Name);
        ImGui.SameLine();
        ImGui.Checkbox(Strings.CostHighQuality + "###cost-hq", ref hq);
        ImGui.SetNextItemWidth(85);
        ImGui.InputInt(Strings.CostQuantity + "###cost-quantity", ref quantity, 0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText(Strings.CostUnitCost + "###cost-price", ref unitCost, 24);

        var valid = decimal.TryParse(unitCost, NumberStyles.Number, CultureInfo.CurrentCulture, out var cost)
            && cost >= 0 && cost <= long.MaxValue && quantity > 0;
        ImGui.BeginDisabled(!valid);
        if (ImGui.SmallButton(Strings.CostAddManual + "###cost-submit") && valid)
        {
            ledger.AddManualLot(selected.Id, hq, quantity, cost, DateTime.UtcNow);
            search = string.Empty;
            matches = [];
            selected = default;
            hq = false;
            quantity = 1;
            unitCost = "0";
        }
        ImGui.EndDisabled();
    }

    private (uint Id, string Name)[] SearchItems(string query)
    {
        // Fork: include items with no current inventory snapshot.
        itemNames ??= Plugin.DataManager.GetExcelSheet<Item>()
            .Select(row => (Id: row.RowId, Name: row.Name.ExtractText()))
            .Where(item => item.Id != 0 && item.Name.Length > 0)
            .OrderBy(item => item.Name)
            .ToArray();
        return itemNames.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(6).ToArray();
    }

    private void DrawLots()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var query = filter.Trim();
        var groups = ledger.GetLots().GroupBy(lot => (lot.ItemId, lot.IsHq))
            .Select(group => (Lots: group, Name: sheet.TryGetRow(group.Key.ItemId, out var row) ? row.Name.ExtractText() : $"#{group.Key.ItemId}"))
            .Where(group => group.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(group => group.Name).ThenBy(group => group.Lots.Key.IsHq)
            .ToArray();

        if (groups.Length == 0)
        {
            ImGui.TextDisabled(Strings.CostNoLots);
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##cost-lots", 6, flags, new Vector2(0, Math.Max(60f, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Strings.CostDate, ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn(Strings.CostRemaining, ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn(Strings.CostUnitCost, ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn(Strings.CostSource, ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("##delete", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();

        foreach (var (lots, name) in groups)
        foreach (var lot in lots)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(lot.IsHq ? $"{name} ({Strings.CostHighQuality})" : name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(lot.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(lot.RemainingQuantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(lot.UnitCost.ToString("N2"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Strings.CostSourceName(lot.Source));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Strings.CostDeleteLot}##cost-delete-{lot.Id}")) ledger.RemoveLot(lot.Id);
        }

        ImGui.EndTable();
    }
}
