using System.Globalization;
using System.IO;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Parser para arquivos CSV contendo matriz de temperaturas.
/// Suporta separadores: vírgula, ponto-e-vírgula e tab.
/// </summary>
internal static class CsvThermalParser
{
    public static ThermalImageData LoadCsvTemperatureMatrix(string path)
    {
        var lines = File.ReadAllLines(path)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (lines.Length == 0)
        {
            throw new InvalidOperationException("CSV vazio para importacao termica.");
        }

        var separators = new[] { ';', ',', '\t' };
        var rows = new List<double[]>();
        foreach (var line in lines)
        {
            var parts = line.Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var row = parts.Select(ParseDoubleFlexible).ToArray();
            if (row.Length > 0)
            {
                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("CSV sem dados numericos validos.");
        }

        var width = rows.Min(r => r.Length);
        var height = rows.Count;
        var matrix = new double[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                matrix[y, x] = rows[y][x];
            }
        }

        return new ThermalImageData
        {
            Width = width,
            Height = height,
            Temperatures = matrix
        };
    }

    private static double ParseDoubleFlexible(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedInvariant))
        {
            return parsedInvariant;
        }

        var normalized = value.Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNormalized))
        {
            return parsedNormalized;
        }

        throw new InvalidOperationException($"Valor de temperatura invalido no CSV: {value}");
    }
}
