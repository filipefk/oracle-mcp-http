using System.Globalization;

namespace McpToolkit.Catalog;

// Formatos de data/duração das listagens (equivalentes ao DisplayFormat do OracleTools2026, com ano em 4 dígitos).
internal static class CatalogFormat
{
    private const string DateFormat = "dd/MM/yyyy HH:mm:ss";
    private const string TimeFormat = "hh':'mm':'ss";

    public static string? Date(DateTime? value) =>
        value?.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string? Duration(TimeSpan? value) => value switch
    {
        null => null,
        { Days: > 0 } duration => $"{duration.Days}d {duration.ToString(TimeFormat, CultureInfo.InvariantCulture)}",
        { } duration => duration.ToString(TimeFormat, CultureInfo.InvariantCulture)
    };
}
