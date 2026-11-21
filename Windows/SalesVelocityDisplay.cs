using Dalamud.Bindings.ImGui;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

// Fork: keep the units, sales and interval formatting consistent across the two views.
internal static class SalesVelocityDisplay
{
    internal static string Counts(SaleWindow window)
        => string.Format(Strings.VelocityCounts, window.Units, window.Sales);

    internal static string CompactCounts(SaleWindow window) => $"{window.Units:N0} / {window.Sales:N0}";

    internal static string Number(double? value) => value?.ToString("N1") ?? "—";

    internal static void DrawTooltip(SalesVelocity velocity, long listed, long available)
    {
        ImGui.TextUnformatted($"{Strings.Velocity24h}  {Counts(velocity.Last24h)}");
        ImGui.TextUnformatted($"{Strings.Velocity3d}  {Counts(velocity.Last3d)}");
        ImGui.TextUnformatted($"{Strings.Velocity7d}  {Counts(velocity.Last7d)}");
        ImGui.TextUnformatted($"{Strings.Velocity30d}  {Counts(velocity.Last30d)}");
        ImGui.TextUnformatted($"{Strings.VelocityHours7d}  {Number(velocity.Last7d.AverageHoursBetweenSales)}");
        ImGui.TextUnformatted($"{Strings.VelocityHours30d}  {Number(velocity.Last30d.AverageHoursBetweenSales)}");
        ImGui.TextUnformatted($"{Strings.VelocityStockDays}  {Number(velocity.DaysOfStock(listed, available))}");
    }
}
