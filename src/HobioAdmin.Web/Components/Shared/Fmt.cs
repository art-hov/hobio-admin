using System.Globalization;

namespace HobioAdmin.Web.Components.Shared;

/// <summary>Display formatting (Canadian English, CAD).</summary>
public static class Fmt
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-CA");

    public static string Number(long value) => value.ToString("N0", Culture);

    public static string Money(decimal value) => value.ToString("C2", Culture);

    public static string Money(decimal? value) => value is null ? "–" : Money(value.Value);

    public static string MoneyShort(decimal value) => Math.Abs(value) >= 10_000
        ? value.ToString("C0", Culture)
        : value.ToString("C2", Culture);

    public static string Percent(double? value, int decimals = 1) =>
        value is null ? "–" : (value.Value * 100).ToString($"F{decimals}", Culture) + "%";

    public static string Ratio(double? value) => value is null ? "–" : value.Value.ToString("F2", Culture);

    public static string Date(DateOnly value) => value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    public static string Month(DateOnly value) => value.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    public static string RangeText(Analytics.DateRange range) =>
        $"{Date(range.From)} – {Date(range.To.AddDays(-1))}";

    /// <summary>Change from previous to current, e.g. +12.5%. Null when there is no meaningful base.</summary>
    public static double? Change(double current, double previous)
    {
        if (previous == 0)
        {
            return current == 0 ? 0 : null;
        }

        return (current - previous) / Math.Abs(previous);
    }

    public static string ChangeText(double? change, bool previousWasZero) =>
        change is null
            ? (previousWasZero ? "new" : "–")
            : (change.Value >= 0 ? "+" : "") + (change.Value * 100).ToString("F1", Culture) + "%";

    /// <summary>A "nice" axis step so charts get about five grid lines.</summary>
    public static int NiceTickStep(double max)
    {
        if (max <= 5)
        {
            return 1;
        }

        var raw = max / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return (int)Math.Max(1, nice * magnitude);
    }
}
