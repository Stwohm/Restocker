using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Windows;

/// <summary>
/// 出品中アイテムのリプライス画面。各 listing 行で「+追加」を押すとプランに積まれ、
/// 最後に「適用」ボタンで pendingPlans を一括実行する (新規出品タブと同じ流儀)。
/// 「全てのアイテム ...」ボタンは fetch → editedPrice 反映までを行い、その後ユーザーが
/// 「全部プランに追加」で pendingPlans に流し込めるようにしてある。
/// </summary>
public sealed class RepriceTab
{
    // Fork: fixed seller tax used for the profit estimate.
    private const decimal SaleTaxRate = 0.05m;
    private readonly CostLedger costLedger;
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly ConfirmDialog confirmDialog;
    private readonly MarketCache marketCache;
    private readonly AutoCycleController autoCycle;
    // Fork: share ImGui sort state across retainers and seed a fresh table on plugin reload.
    private readonly string listingTableId = $"##reprice-listings-{Guid.NewGuid():N}";
    private readonly HashSet<string> reportedBaits = new();
    private string filter = string.Empty;
    /// <summary>編集中の新価格。キーは "{retainerKey}#{listingIndex}"。</summary>
    private readonly Dictionary<string, long> editedPrice = new();
    private string? lastMatchSummary;
    /// <summary>明示的に展開したリテイナーセクションの key。デフォルトは折りたたみ。</summary>
    private readonly HashSet<string> expandedSections = new();
    /// <summary>「最安値 -Xギル」の X。セッション中のみ有効 (永続化しない)。</summary>
    private int undercutDelta = 1;
    /// <summary>適用前のプランリスト。</summary>
    private readonly List<RepricePlan> pendingPlans = new();

    public RepriceTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog, MarketCache marketCache, AutoCycleController autoCycle, CostLedger costLedger)
    {
        this.autoCycle = autoCycle;
        this.configuration = configuration;
        this.executor = executor;
        this.confirmDialog = confirmDialog;
        this.marketCache = marketCache;
        this.costLedger = costLedger;
    }

    public void Draw()
    {
        DrawToolbar();
        ImGui.Separator();
        DrawRetainers();
        ImGui.Separator();
        DrawPendingPlans();
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##reprice-filter", Strings.Filter, ref filter, 64);
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.CollapseAll + "##reprice-collapse"))
        {
            expandedSections.Clear();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.ExpandAll + "##reprice-expand"))
        {
            foreach (var snap in configuration.Snapshots.Values)
                expandedSections.Add("reprice-" + snap.Key);
        }
        ImGui.SameLine();
        DrawApplyButton();

        DrawCycleControls();

        if (!string.IsNullOrEmpty(lastMatchSummary))
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.85f, 0.85f, 0.4f, 1f), lastMatchSummary);
        }
    }

    /// <summary>Fork: one-click full cycle + timed auto cycle toggle.</summary>
    private void DrawCycleControls()
    {
        var bellOpen = AutoCycleController.IsBellOpen();

        var canRun = bellOpen && !executor.IsRunning;
        if (!canRun) ImGui.BeginDisabled();
        if (ImGui.Button("Full cycle (once)##restocky-once"))
            autoCycle.RunOnce();
        if (!canRun) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Every retainer: fetch live prices, lowest competitor -undercut, apply with the floor / max-drop guards. No confirm dialog.");

        ImGui.SameLine();
        if (!canRun) ImGui.BeginDisabled();
        if (ImGui.Button("Full cycle (I set prices)##restocky-manual"))
        {
            var undercut = configuration.UndercutGil;
            executor.OnFetchMarketCompleted = null;
            executor.OnFetchJobCompleted = key =>
            {
                if (!configuration.Snapshots.TryGetValue(key, out var s)) return;
                expandedSections.Add("reprice-" + key);
                ApplyMatchLowestForRetainer(s, offset: -undercut, respectHold: true);
            };
            executor.StartFetchAll();
        }
        if (!canRun) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Every retainer: fetch live prices and pre-fill the New column with lowest -undercut. Nothing is applied: edit prices, '+ Add all to plan', then Apply.");

        ImGui.SameLine();
        var enabled = autoCycle.Enabled;
        if (!enabled && !bellOpen) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Auto cycle every##restocky-auto", ref enabled))
        {
            if (enabled) autoCycle.Enable();
            else autoCycle.Disable("turned off");
        }
        if (!autoCycle.Enabled && !bellOpen) ImGui.EndDisabled();

        // Editable interval (minutes). Changing it while the loop is on restarts the countdown.
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(50);
        var minutes = configuration.AutoCycleIntervalMinutes;
        if (ImGui.InputInt("##restocky-interval", ref minutes, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue)
            || ImGui.IsItemDeactivatedAfterEdit())
        {
            minutes = System.Math.Clamp(minutes, 1, 1440);
            if (minutes != configuration.AutoCycleIntervalMinutes)
            {
                configuration.AutoCycleIntervalMinutes = minutes;
                configuration.Save();
                autoCycle.RestartCountdown();
            }
        }
        ImGui.SameLine(0, 4);
        ImGui.TextUnformatted("min");

        if (autoCycle.Enabled)
        {
            ImGui.SameLine();
            if (executor.IsRunning)
                ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.9f, 0.5f, 1f), "running…");
            else if (autoCycle.WaitingReason is { } why)
                ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.8f, 0.4f, 1f), why);
            else if (autoCycle.NextRunUtc is { } next)
            {
                var left = next - System.DateTime.UtcNow;
                if (left < System.TimeSpan.Zero) left = System.TimeSpan.Zero;
                ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.9f, 0.5f, 1f), $"next in {(int)left.TotalMinutes:00}:{left.Seconds:00}");
            }
            if (autoCycle.ConsecutiveFailures > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.4f, 1f), $"failures {autoCycle.ConsecutiveFailures}/{configuration.AutoCycleMaxFailures}");
            }
        }
        if (!string.IsNullOrEmpty(autoCycle.LastSummary))
            ImGui.TextDisabled(autoCycle.LastSummary);
    }

    private void DrawApplyButton()
    {
        var actions = BuildActionsFromPlans();
        var disabled = actions.Count == 0 || executor.IsRunning;
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Button(string.Format(Strings.ApplyWithCount, Strings.Apply, actions.Count)))
        {
            confirmDialog.Request(actions, list =>
            {
                executor.StartApplyActions(list);
                pendingPlans.Clear();
            });
        }
        if (disabled) ImGui.EndDisabled();

        if (executor.IsRunning && executor.Mode == ExecutionMode.ApplyActions)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(string.Format(Strings.ApplyProgress, executor.CompletedActions, executor.TotalActions, executor.CompletedJobs, executor.TotalJobs));
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.HeaderStop)) executor.Cancel();
        }
    }

    private List<PlannedAction> BuildActionsFromPlans()
    {
        var result = new List<PlannedAction>();
        foreach (var p in pendingPlans)
        {
            result.Add(new PlannedAction
            {
                Kind = PlannedActionKind.Reprice,
                RetainerKey = p.RetainerKey,
                ItemId = p.ItemId,
                IsHQ = p.IsHQ,
                Quantity = p.Quantity,
                UnitPrice = p.NewPrice,
                ListingIndex = p.ListingIndex,
                OldPrice = p.OldPrice,
            });
        }
        return result;
    }

    private void DrawRetainers()
    {
        if (configuration.Snapshots.Count == 0)
        {
            ImGui.TextDisabled(Strings.EmptyHint);
            return;
        }

        // 上 60% を listings、下 40% を pending plans に振る
        var avail = ImGui.GetContentRegionAvail();
        var topHeight = System.Math.Max(160f, avail.Y * 0.6f);
        if (!ImGui.BeginChild("##reprice-scroll", new System.Numerics.Vector2(avail.X, topHeight), false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var snapshots = configuration.Snapshots.Values
            .OrderBy(s => s.CharacterName).ThenBy(s => s.RetainerName).ToList();

        foreach (var snap in snapshots)
        {
            var filteredListings = FilterListings(snap, sheet);
            if (filteredListings.Count == 0 && filter.Length > 0) continue;

            var header = string.Format(Strings.RetainerHeader, snap.RetainerName, filteredListings.Count, FreshnessSuffix(snap));
            if (!DrawCollapsingHeader("reprice-" + snap.Key, header)) continue;

            // 行 1: undercut input + fetch ボタン群
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("-");
            ImGui.SameLine(0, 2);
            ImGui.SetNextItemWidth(60);
            if (ImGui.InputInt($"##undercut-{snap.Key}", ref undercutDelta, 0))
            {
                if (undercutDelta < 0) undercutDelta = 0;
            }
            ImGui.SameLine(0, 4);
            ImGui.TextUnformatted("g");
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.RepriceMatchLowestThisRetainer + "##matchlowest-" + snap.Key))
            {
                var capturedSnap = snap;
                var capturedOffset = -undercutDelta;
                executor.OnFetchJobCompleted = null;
                executor.OnFetchMarketCompleted = () => ApplyMatchLowestForRetainer(capturedSnap, offset: capturedOffset);
                executor.StartFetchMarketPricesForRetainer(snap.Key);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.RepriceMatchLowestTooltip);
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.RepriceMatchLowestExactThisRetainer + "##matchexact-" + snap.Key))
            {
                var capturedSnap = snap;
                executor.OnFetchJobCompleted = null;
                executor.OnFetchMarketCompleted = () => ApplyMatchLowestForRetainer(capturedSnap, offset: 0);
                executor.StartFetchMarketPricesForRetainer(snap.Key);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.RepriceMatchLowestTooltip);
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.RepriceAddAllToPlan + "##addall-" + snap.Key))
            {
                AddAllEditedToPlans(snap, sheet);
            }

            DrawRetainerListings(snap, filteredListings);
        }

        ImGui.EndChild();
    }

    private List<(ListingEntry Entry, string ItemName)> FilterListings(RetainerSnapshot snap, Lumina.Excel.ExcelSheet<Item> sheet)
    {
        var rows = new List<(ListingEntry, string)>();
        foreach (var l in snap.Listings)
        {
            var name = sheet.TryGetRow(l.ItemId, out var row) ? row.Name.ExtractText() : $"#{l.ItemId}";
            if (filter.Length > 0 && !DisplayListingName(l, name).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add((l, name));
        }
        return rows;
    }

    private static string FreshnessSuffix(RetainerSnapshot snap)
    {
        if (snap.LastRefreshedUtc == System.DateTime.MinValue) return Strings.HeaderUnknown;
        var age = System.DateTime.UtcNow - snap.LastRefreshedUtc;
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private bool DrawCollapsingHeader(string id, string label)
    {
        var open = expandedSections.Contains(id);
        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        var nowOpen = ImGui.CollapsingHeader(label + "##" + id);
        if (nowOpen != open)
        {
            if (nowOpen) expandedSections.Add(id);
            else expandedSections.Remove(id);
        }
        return nowOpen;
    }

    /// <summary>
    /// Fork: edited prices are keyed by slot AND item/HQ. Upstream keyed by slot only, so a
    /// price computed for one item showed up (and was applied) on whatever item later sat in
    /// that slot after the listings changed.
    /// </summary>
    private static string EditKey(string snapKey, ListingEntry l)
        => $"{snapKey}#{l.ListingIndex}#{l.ItemId}{(l.IsHQ ? "h" : "n")}";

    /// <summary>fetch 完了後に editedPrice に「最安値 + offset」を入れる。プランには勝手に積まない。</summary>
    private void ApplyMatchLowestForRetainer(RetainerSnapshot capturedSnap, long offset = -1, bool respectHold = false)
    {
        // Fork: the fetch reopened the sell list, so RetainerWatcher stored a NEW snapshot
        // object. The one captured at click time is stale; always use the current one.
        var snap = configuration.Snapshots.TryGetValue(capturedSnap.Key, out var fresh) ? fresh : capturedSnap;
        var seen = new HashSet<(uint, bool)>();
        var missing = new HashSet<(uint, bool, string)>();
        var applied = 0;
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();

        Plugin.Log.Information($"[Restocker] reprice apply: retainer={snap.RetainerName} listings={snap.Listings.Count} offset={offset}");

        foreach (var listing in snap.Listings)
        {
            var name = sheet.TryGetRow(listing.ItemId, out var row) ? row.Name.ExtractText() : $"#{listing.ItemId}";
            if (listing.IsHQ) name += " (HQ)";
            if (filter.Length > 0 && !name.Contains(filter, System.StringComparison.OrdinalIgnoreCase)) continue;

            seen.Add((listing.ItemId, listing.IsHQ));
            if (respectHold && configuration.IsHeld(listing.ItemId, listing.IsHQ))
            {
                editedPrice[EditKey(snap.Key, listing)] = listing.UnitPrice;
                Plugin.Log.Information($"  slot={listing.ListingIndex} item={listing.ItemId} hq={listing.IsHQ} name='{name}' HELD (left at {listing.UnitPrice:N0})");
                continue;
            }
            if (!marketCache.HasData(listing.ItemId, listing.IsHQ))
            {
                missing.Add((listing.ItemId, listing.IsHQ, name));
                Plugin.Log.Information($"  slot={listing.ListingIndex} item={listing.ItemId} hq={listing.IsHQ} name='{name}' MISS (no cache)");
                continue;
            }
            var lowest = marketCache.GetLowest(listing.ItemId, listing.IsHQ);
            var current = listing.UnitPrice;
            var dropped = BaitFinder.Dropped(marketCache.GetOffers(listing.ItemId, listing.IsHQ), lowest);
            if (dropped.Count > 0)
            {
                var notice = new BaitNotice(listing.ItemId, listing.IsHQ, snap.RetainerName, dropped, lowest);
                if (reportedBaits.Add(BaitFinder.Key(notice)))
                    Plugin.ChatPrint(BaitFinder.Format(sheet.TryGetRow(listing.ItemId, out var br) ? br.Name.ExtractText() : $"#{listing.ItemId}", notice));
            }
            if (lowest <= 0)
            {
                Plugin.Log.Information($"  slot={listing.ListingIndex} item={listing.ItemId} hq={listing.IsHQ} name='{name}' lowest={lowest} (skipped, no data)");
                continue;
            }
            var newPrice = lowest + offset;
            if (newPrice < 1) newPrice = 1;
            editedPrice[EditKey(snap.Key, listing)] = newPrice;
            applied++;
            Plugin.Log.Information($"  slot={listing.ListingIndex} item={listing.ItemId} hq={listing.IsHQ} name='{name}' current={current:N0} lowest={lowest:N0} offset={offset} -> new={newPrice:N0}");
        }

        if (missing.Count == 0)
            lastMatchSummary = $"{snap.RetainerName}: " + string.Format(Strings.MatchAppliedSummary, applied, seen.Count);
        else
            lastMatchSummary = $"{snap.RetainerName}: " + string.Format(Strings.MatchAppliedSummaryWithMissing, applied, seen.Count, missing.Count,
                string.Join(", ", missing.Take(5).Select(m => m.Item3)) + (missing.Count > 5 ? "…" : ""));
    }

    /// <summary>このリテイナーの editedPrice 全行をまとめて pendingPlans に追加。</summary>
    private void AddAllEditedToPlans(RetainerSnapshot snap, Lumina.Excel.ExcelSheet<Item> sheet)
    {
        var added = 0;
        foreach (var listing in snap.Listings)
        {
            var key = EditKey(snap.Key, listing);
            if (!editedPrice.TryGetValue(key, out var price)) continue;
            if (price <= 0) continue;
            if (price == listing.UnitPrice) continue; // 同額は積まない
            var name = sheet.TryGetRow(listing.ItemId, out var row) ? row.Name.ExtractText() : $"#{listing.ItemId}";
            if (listing.IsHQ) name += " (HQ)";
            UpsertPlan(new RepricePlan
            {
                RetainerKey = snap.Key,
                RetainerName = snap.RetainerName,
                ItemId = listing.ItemId,
                IsHQ = listing.IsHQ,
                ItemName = name,
                ListingIndex = listing.ListingIndex,
                OldPrice = listing.UnitPrice,
                NewPrice = price,
                Quantity = listing.Quantity,
            });
            added++;
        }
        Plugin.Log.Information($"[Restocker] reprice plan: added/updated {added} plans for {snap.RetainerName}");
    }

    /// <summary>同じ (retainer, slot) のプランは上書き、無ければ追加。</summary>
    private void UpsertPlan(RepricePlan plan)
    {
        for (var i = 0; i < pendingPlans.Count; i++)
        {
            var p = pendingPlans[i];
            if (p.RetainerKey == plan.RetainerKey && p.ListingIndex == plan.ListingIndex)
            {
                pendingPlans[i] = plan;
                return;
            }
        }
        pendingPlans.Add(plan);
    }

    private void RemovePlanIfExists(string retainerKey, int slot)
    {
        for (var i = 0; i < pendingPlans.Count; i++)
        {
            if (pendingPlans[i].RetainerKey == retainerKey && pendingPlans[i].ListingIndex == slot)
            {
                pendingPlans.RemoveAt(i);
                return;
            }
        }
    }

    private bool HasPlan(string retainerKey, int slot)
        => pendingPlans.Any(p => p.RetainerKey == retainerKey && p.ListingIndex == slot);

    // Fork: quality is displayed and searchable alongside the base item name.
    private static string DisplayListingName(ListingEntry listing, string name) => listing.IsHQ ? name + " (HQ)" : name;

    private void DrawRetainerListings(RetainerSnapshot snap, List<(ListingEntry Entry, string ItemName)> rows)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.Sortable;

        // Fork: #, Item, Qty, Current, undercutters icon, New, Profit, Hold, fill, +Add = 10
        if (!ImGui.BeginTable(listingTableId, 10, flags)) return;

        SetupSortableColumn("#", ImGuiTableColumnFlags.WidthFixed, 30, RepriceSortColumn.Slot);
        SetupSortableColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.42f, RepriceSortColumn.Item);
        SetupSortableColumn(Strings.ColTotalQty, ImGuiTableColumnFlags.WidthFixed, 60, RepriceSortColumn.Quantity);
        SetupSortableColumn(Strings.ColCurrentPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f, RepriceSortColumn.CurrentPrice);
        const ImGuiTableColumnFlags fixedNoSort = ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort;
        ImGui.TableSetupColumn("##cheaper", fixedNoSort, 24);
        ImGui.TableSetupColumn(Strings.ColNewPrice, ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 0.2f);
        ImGui.TableSetupColumn(Strings.ColProfit, fixedNoSort, 75);
        ImGui.TableSetupColumn(Strings.ColHold, fixedNoSort, 55);
        ImGui.TableSetupColumn("##fill", fixedNoSort, 70);
        ImGui.TableSetupColumn("##add", fixedNoSort, 60);
        ImGui.TableHeadersRow();

        // Fork: sort the filtered display rows while keeping the original listing entries and slot keys.
        UpdateListingSort();
        var sortColumn = configuration.RepriceSortColumn;
        var descending = configuration.RepriceSortDescending;
        rows.Sort((left, right) => RepriceSort.Compare(left, right, sortColumn, descending));

        var tracker = Plugin.Instance.Tracker.Tracker;
        var now = DateTime.UtcNow;
        var tracked = tracker.Records.Where(r => r.RetainerKey == snap.Key && r.Outcome == ListingTracker.Active)
            .ToLookup(r => (r.Slot, r.ItemId, r.IsHq, r.Quantity));
        foreach (var (l, itemName) in rows)
        {
            var name = DisplayListingName(l, itemName);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.ListingIndex.ToString());
            ImGui.TableNextColumn();
            var record = tracked[(l.ListingIndex, l.ItemId, l.IsHQ, l.Quantity)].FirstOrDefault();
            DrawItemName(l, name, record, tracker, now);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.UnitPrice == 0 ? Strings.PriceUnknown : l.UnitPrice.ToString("N0"));
            ImGui.TableNextColumn();
            DrawUndercutters(l);
            ImGui.TableNextColumn();
            var editKey = EditKey(snap.Key, l);
            DrawPriceInput(editKey);
            ImGui.TableNextColumn();
            DrawProfit(l, editedPrice.GetValueOrDefault(editKey));
            ImGui.TableNextColumn();
            DrawHoldCheckbox(snap.Key, l);
            ImGui.TableNextColumn();
            DrawFillButton(snap, l);
            ImGui.TableNextColumn();
            DrawAddPlanButton(snap, l, name);
        }

        ImGui.EndTable();
    }

    private void SetupSortableColumn(string label, ImGuiTableColumnFlags flags, float width, RepriceSortColumn column)
    {
        var savedColumn = configuration.RepriceSortColumn;
        if (!Enum.IsDefined(savedColumn)) savedColumn = RepriceSortColumn.Item;
        if (column == savedColumn)
        {
            flags |= ImGuiTableColumnFlags.DefaultSort;
            if (configuration.RepriceSortDescending)
                flags |= ImGuiTableColumnFlags.PreferSortDescending;
        }
        ImGui.TableSetupColumn(label, flags, width, (uint)column);
    }

    private unsafe void UpdateListingSort()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.Handle == null || !specs.SpecsDirty || specs.SpecsCount == 0) return;

        var column = (RepriceSortColumn)specs.Specs.ColumnUserID;
        var descending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
        specs.SpecsDirty = false;
        if (column == configuration.RepriceSortColumn && descending == configuration.RepriceSortDescending) return;

        configuration.RepriceSortColumn = column;
        configuration.RepriceSortDescending = descending;
        configuration.Save();
    }

    // Fork: show listing age and recorded sales together on the item name.
    private void DrawItemName(ListingEntry listing, string name, ListingRecord? record, ListingTracker tracker, DateTime now)
    {
        var stuck = record != null && ListingTracker.IsStuck(record, now, configuration.StuckListingDays);
        var age = record == null ? 0 : (now - record.FirstSeenUtc).TotalDays;
        ImGui.TextUnformatted(stuck ? $"{name} ({string.Format(Strings.DaysShort, Math.Floor(age))})" : name);
        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        if (stuck && record != null)
        {
            var offers = marketCache.GetOffers(listing.ItemId, listing.IsHQ);
            var lowest = offers.Count > 0 ? offers[0].Price.ToString("N0") : "—";
            ImGui.TextUnformatted(string.Format(Strings.StuckTooltip, age, record.RepriceCount, lowest));
            ImGui.Separator();
        }
        var velocity = SalesVelocity.Calculate(tracker.GetSales(listing.ItemId, listing.IsHQ), now);
        var stock = Plugin.Instance.LowStock.GetTrackedStock().GetValueOrDefault((listing.ItemId, listing.IsHQ));
        SalesVelocityDisplay.DrawTooltip(velocity, stock.Listed, stock.Available);
        ImGui.EndTooltip();
    }

    // Fork: show per-unit profit using the next lot's cost.
    private void DrawProfit(ListingEntry listing, long edited)
    {
        var price = edited > 0 ? edited : listing.UnitPrice;
        var afterTax = price * (1m - SaleTaxRate);
        var unitCost = costLedger.CurrentUnitCost(listing.ItemId, listing.IsHQ);
        if (unitCost is { } cost)
        {
            var profit = afterTax - cost;
            var color = profit > 0
                ? new System.Numerics.Vector4(0.4f, 0.85f, 0.45f, 1f)
                : new System.Numerics.Vector4(1f, 0.45f, 0.4f, 1f);
            ImGui.TextColored(color, profit.ToString("N0"));
        }
        else
            ImGui.TextDisabled("-");

        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"{Strings.CostUnitCost}  {unitCost?.ToString("N2") ?? "-"}");
        ImGui.TextUnformatted(string.Format(Strings.CostPriceAfterTax, SaleTaxRate, afterTax));
        ImGui.TextUnformatted($"{Strings.CostWeightedAverage}  {costLedger.WeightedAverageCost(listing.ItemId, listing.IsHQ)?.ToString("N2") ?? "-"}");
        if (unitCost.HasValue)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(Strings.CostFifoLots);
            foreach (var lot in costLedger.GetLots(listing.ItemId, listing.IsHQ))
                ImGui.TextUnformatted(string.Format(Strings.CostLotDetails, lot.Utc.ToLocalTime(), lot.RemainingQuantity, lot.UnitCost, Strings.CostSourceName(lot.Source)));
        }
        ImGui.EndTooltip();
    }

    // Fork: display competitors from the last scan and the past week's most active rivals.
    private void DrawUndercutters(ListingEntry listing)
    {
        var updated = marketCache.UpdatedUtc(listing.ItemId, listing.IsHQ);
        var offers = updated == null ? System.Array.Empty<MarketOffer>() : marketCache.GetOffers(listing.ItemId, listing.IsHQ);
        var price = listing.UnitPrice;
        // The cache sorts by unit price, so the first offer is enough to color the icon.
        if (offers.Count > 0 && offers[0].Price < price)
            ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.8f, 0.4f, 1f), "(!)");
        else
            ImGui.TextDisabled("(!)");

        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        var nowUtc = System.DateTime.UtcNow;
        var history = Plugin.Instance.UndercutHistory;
        var summary = UndercutSummary.Create(offers, price);
        if (updated == null)
            ImGui.TextUnformatted(Strings.UndercuttersNoScan);
        else if (price == 0)
            ImGui.TextUnformatted(Strings.UndercuttersUnknownPrice);
        else if (summary.ListingCount == 0)
            ImGui.TextUnformatted(Strings.UndercuttersNone);
        else
            ImGui.TextUnformatted(string.Format(Strings.UndercuttersHeader, price, summary.ListingCount, summary.UnitCount));

        const ImGuiTableFlags flags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings;
        if (summary.Offers.Count > 0 && ImGui.BeginTable("##undercutters", 6, flags))
        {
            ImGui.TableSetupColumn(Strings.UndercuttersSeller);
            ImGui.TableSetupColumn(Strings.UndercuttersQuantity);
            ImGui.TableSetupColumn(Strings.UndercuttersUnitPrice);
            SetupUndercutCountColumns();
            ImGui.TableHeadersRow();
            foreach (var offer in summary.Offers)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(offer.Seller) ? "?" : offer.Seller);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(offer.Quantity.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(offer.Price.ToString("N0"));
                DrawUndercutCounts(history.Counts(listing.ItemId, listing.IsHQ, offer.Seller, nowUtc));
            }
            ImGui.EndTable();
        }

        if (summary.RemainingCount > 0)
            ImGui.TextUnformatted(string.Format(Strings.UndercuttersMore, summary.RemainingCount));
        if (updated is { } scannedUtc)
        {
            var minutes = (nowUtc - scannedUtc).TotalMinutes;
            ImGui.TextDisabled(minutes < 1 ? Strings.UndercuttersScannedNow
                : string.Format(Strings.UndercuttersScannedMinutes, (int)minutes));
        }
        var rivals = history.TopRivals(listing.ItemId, listing.IsHQ, nowUtc, 5);
        if (rivals.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(Strings.UndercuttersMostActiveRivals);
            if (ImGui.BeginTable("##undercut-rivals", 4, flags))
            {
                ImGui.TableSetupColumn(Strings.UndercuttersSeller);
                SetupUndercutCountColumns();
                ImGui.TableHeadersRow();
                foreach (var rival in rivals)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rival.Seller);
                    DrawUndercutCounts(rival.Counts);
                }
                ImGui.EndTable();
            }
        }
        if (summary.Offers.Count > 0 || rivals.Count > 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextDisabled(Strings.UndercuttersCountHelp);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndTooltip();
    }

    private static void SetupUndercutCountColumns()
    {
        ImGui.TableSetupColumn(Strings.UndercuttersLast24h);
        ImGui.TableSetupColumn(Strings.UndercuttersLast3d);
        ImGui.TableSetupColumn(Strings.UndercuttersLast7d);
    }

    private static void DrawUndercutCounts(UndercutCounts counts)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(counts.Last24h.ToString("N0"));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(counts.Last3d.ToString("N0"));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(counts.Last7d.ToString("N0"));
    }

    private void DrawHoldCheckbox(string snapshotKey, ListingEntry listing)
    {
        var held = configuration.IsHeld(listing.ItemId, listing.IsHQ);
        if (ImGui.Checkbox($"##hold-{snapshotKey}-{listing.ItemId}-{listing.IsHQ}-{listing.ListingIndex}", ref held))
        {
            configuration.SetHeld(listing.ItemId, listing.IsHQ, held);
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.HoldTooltip);
    }

    /// <summary>この行で入力された価格を、同じリテイナーの他の全 listing の editedPrice にコピー。</summary>
    private void DrawFillButton(RetainerSnapshot snap, ListingEntry sourceListing)
    {
        var sourceKey = EditKey(snap.Key, sourceListing);
        var sourcePrice = editedPrice.GetValueOrDefault(sourceKey, 0);
        var canFill = sourcePrice > 0;
        if (!canFill) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{Strings.RepriceFillRetainer}##fill-{sourceKey}"))
        {
            foreach (var l in snap.Listings)
            {
                if (l.ListingIndex == sourceListing.ListingIndex) continue;
                editedPrice[EditKey(snap.Key, l)] = sourcePrice;
            }
            Plugin.Log.Information($"[Restocker] fill: copied {sourcePrice} to {snap.Listings.Count - 1} other listings on {snap.RetainerName}");
        }
        if (!canFill) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.RepriceFillTooltip);
    }

    private void DrawPriceInput(string key)
    {
        var v = (int)editedPrice.GetValueOrDefault(key, 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##price-{key}", ref v, 0))
        {
            if (v < 0) v = 0;
            editedPrice[key] = v;
        }
    }

    private void DrawAddPlanButton(RetainerSnapshot snap, ListingEntry l, string name)
    {
        var key = EditKey(snap.Key, l);
        var price = editedPrice.GetValueOrDefault(key, 0);
        var canAdd = price > 0 && price != l.UnitPrice;

        if (HasPlan(snap.Key, l.ListingIndex))
        {
            // 既にプランにあれば「-」(削除) を出す
            if (ImGui.SmallButton($"-##rm-{key}"))
                RemovePlanIfExists(snap.Key, l.ListingIndex);
            return;
        }

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{Strings.AddToPlan}##add-{key}"))
        {
            UpsertPlan(new RepricePlan
            {
                RetainerKey = snap.Key,
                RetainerName = snap.RetainerName,
                ItemId = l.ItemId,
                IsHQ = l.IsHQ,
                ItemName = name,
                ListingIndex = l.ListingIndex,
                OldPrice = l.UnitPrice,
                NewPrice = price,
                Quantity = l.Quantity,
            });
        }
        if (!canAdd) ImGui.EndDisabled();
    }

    private void DrawPendingPlans()
    {
        ImGui.TextUnformatted(string.Format(Strings.PendingPlansHeader, pendingPlans.Count));
        ImGui.SameLine();
        if (pendingPlans.Count > 0 && ImGui.SmallButton(Strings.ClearAllPlans + "##clear-reprice-plans"))
        {
            pendingPlans.Clear();
        }

        if (pendingPlans.Count == 0)
        {
            ImGui.TextDisabled(Strings.PendingPlansEmpty);
            return;
        }

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.ScrollY;
        // Fork: fill the remaining height and scroll instead of overflowing the window.
        var pendingHeight = System.Math.Max(80f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("##reprice-pending", 6, flags, new System.Numerics.Vector2(0, pendingHeight))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(Strings.ColTarget, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn(Strings.ColTotalQty, ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn(Strings.ColCurrentPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn(Strings.ColNewPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableHeadersRow();

        var removeIdx = -1;
        for (var i = 0; i < pendingPlans.Count; i++)
        {
            var p = pendingPlans[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.ItemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.RetainerName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.OldPrice.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.NewPrice.ToString("N0"));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Strings.PendingPlanRemove}##rmp-{i}")) removeIdx = i;
        }
        ImGui.EndTable();

        if (removeIdx >= 0) pendingPlans.RemoveAt(removeIdx);
    }
}

internal sealed class RepricePlan
{
    public string RetainerKey = string.Empty;
    public string RetainerName = string.Empty;
    public uint ItemId;
    public bool IsHQ;
    public string ItemName = string.Empty;
    public int ListingIndex;
    public long OldPrice;
    public long NewPrice;
    public int Quantity;
}
