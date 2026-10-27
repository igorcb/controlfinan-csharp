using System.Globalization;

namespace ControlFinan.Models;

/// <summary>Formatting that matches Rails' pt-BR helpers (number_to_currency, l(date)) and BigDecimal#to_s.</summary>
public static class Format
{
    public static readonly string[] Months =
        ["janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];

    /// <summary>"R$ 1.234,50"; negatives as "-R$ 1.234,50".</summary>
    public static string Currency(decimal value)
    {
        var amount = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        var sign = amount < 0 ? "-" : "";
        var text = Math.Abs(amount).ToString("#,0.00", CultureInfo.InvariantCulture); // "1,234.50"
        return sign + "R$ " + text.Replace(",", "#").Replace(".", ",").Replace("#", ".");
    }

    /// <summary>The color of an amount: red when negative, green otherwise.</summary>
    public static string AmountClass(decimal value) => value < 0 ? "text-red-700" : "text-emerald-700";

    /// <summary>How Ruby prints a BigDecimal: 150 -> "150.0", 12.50 -> "12.5".</summary>
    public static string DecimalText(decimal value)
    {
        var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : text + ".0";
    }

    public static string BrDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static string DayMonth(DateTime date) => date.ToString("dd/MM", CultureInfo.InvariantCulture);

    public static string Iso(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
