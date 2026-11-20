using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Restocker.Market;

namespace Restocker.Windows;

/// <summary>Fork: listing lifecycles from <see cref="ListingTracker"/> (active first, then most recently ended).</summary>
public sealed class SalesTab
{
    private string filter = string.Empty;
    private bool showEnded = true;

    public void Draw()
    {
        var host = Plugin.Instance?.Tracker;
        if (host == null) return;
        var records = host.Tracker.Records;

        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##sales-filter", "Filter item / retainer / competitor", ref filter, 64);
        ImGui.SameLine();
        ImGui.Checkbox("Show sold / gone", ref showEnded);
        ImGui.SameLine();
        if (ImGui.SmallButton("Open CSV folder"))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Plugin.PluginInterface.GetPluginConfigDirectory(),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[Restocker] open folder failed"); }
        }

        var sold = records.Where(r => r.Outcome == ListingTracker.Sold).ToList();
        if (sold.Count > 0)
        {
            ImGui.TextDisabled($"Sold: {sold.Count} listings, {sold.Sum(r => r.SoldTotalGil):N0} gil, avg {sold.Average(r => r.DaysListed ?? 0):0.0} days, avg {sold.Average(r => r.RepriceCount):0.0} reprices");
        }

        var rows = records
            .Where(r => showEnded || r.Outcome == ListingTracker.Active)
            .Where(r => filter.Length == 0
                        || r.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.RetainerName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Competitors.Keys.Any(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Outcome == ListingTracker.Active ? 0 : 1)
            .ThenByDescending(r => r.EndedUtc ?? r.LastSeenUtc)
            .ToList();

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##sales", 10, flags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 0.1f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("First", ImGuiTableColumnFlags.WidthStretch, 0.09f);
        ImGui.TableSetupColumn("Last", ImGuiTableColumnFlags.WidthStretch, 0.09f);
        ImGui.TableSetupColumn("Reprices", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Undercut by", ImGuiTableColumnFlags.WidthStretch, 0.22f);
        ImGui.TableSetupColumn("Days", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Sold for", ImGuiTableColumnFlags.WidthStretch, 0.09f);
        ImGui.TableHeadersRow();

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.ItemName + (r.IsHq ? " (HQ)" : ""));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.RetainerName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Quantity.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.FirstPrice.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.LastPrice.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{r.RepriceCount} ({r.UndercutCount})");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("price changes (times a competitor was below us)");
            ImGui.TableNextColumn();
            var by = string.Join(", ", r.Competitors.Values.Where(c => c.TimesBelowUs > 0)
                .OrderByDescending(c => c.TimesBelowUs).Select(c => $"{c.Seller} {c.MaxQuantity}x ({c.TimesBelowUs})"));
            ImGui.TextUnformatted(by);
            if (ImGui.IsItemHovered() && r.Competitors.Count > 0)
                ImGui.SetTooltip(string.Join("\n", r.Competitors.Values.OrderBy(c => c.LowestPrice)
                    .Select(c => $"{c.Seller}: up to {c.MaxQuantity}x, lowest {c.LowestPrice:N0}, seen {c.TimesSeen}, below us {c.TimesBelowUs}")));
            ImGui.TableNextColumn();
            var days = r.DaysListed ?? (DateTime.UtcNow - r.FirstSeenUtc).TotalDays;
            ImGui.TextUnformatted(days.ToString("0.0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Outcome);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.SoldTotalGil > 0 ? r.SoldTotalGil.ToString("N0") : "");
        }
        ImGui.EndTable();
    }
}
