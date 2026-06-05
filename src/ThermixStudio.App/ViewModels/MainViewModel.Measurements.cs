using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using ThermixStudio.Core;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private void ConfigurePlot()
    {
        TrendPlotModel.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "HH:mm", Title = "Hora", TextColor = OxyColors.White, TitleColor = OxyColors.White });
        TrendPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Temp (oC)", TextColor = OxyColors.White, TitleColor = OxyColors.White });
    }

    private void RefreshTrendPlot(IReadOnlyList<TrendPoint> points)
    {
        TrendPlotModel.Series.Clear();
        if (points.Count > 0)
        {
            var series = new LineSeries { Title = "Tmax", Color = OxyColor.FromRgb(255, 102, 0), MarkerType = MarkerType.Circle, MarkerSize = 4 };
            foreach (var p in points) series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(p.DateUtc), p.Temperature));
            TrendPlotModel.Series.Add(series);
        }
        TrendPlotModel.InvalidatePlot(true);
    }

    public async Task<ThermalMeasurement?> AddSpotAtNormalizedAsync(double normalizedX, double normalizedY)
    {
        if (_loadedImage is null || SelectedThermogram is null) return null;
        var x = Math.Clamp((int)Math.Round(normalizedX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y = Math.Clamp((int)Math.Round(normalizedY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var temperature = _thermalService.GetTemperatureAt(_loadedImage, x, y);
        var measurement = new ThermalMeasurement { ThermogramId = SelectedThermogram.Id, Type = MeasurementType.Spot, Tmin = temperature, Tmax = temperature, Tavg = temperature, CoordinatesJson = JsonSerializer.Serialize(new { x, y }), Notes = "Spot manual no canvas" };
        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Spot em ({x},{y}): {temperature:F1} oC";
        return measurement;
    }

    public async Task<ThermalMeasurement?> AddAreaAtNormalizedAsync(double startX, double startY, double endX, double endY)
    {
        if (_loadedImage is null || SelectedThermogram is null) return null;
        var x1 = Math.Clamp((int)Math.Round(startX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y1 = Math.Clamp((int)Math.Round(startY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var x2 = Math.Clamp((int)Math.Round(endX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y2 = Math.Clamp((int)Math.Round(endY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var x = Math.Min(x1, x2);
        var y = Math.Min(y1, y2);
        var rw = Math.Max(2, Math.Abs(x2 - x1));
        var rh = Math.Max(2, Math.Abs(y2 - y1));
        var measurement = new ThermalMeasurement { ThermogramId = SelectedThermogram.Id, Type = MeasurementType.Area, Tmin = 0, Tmax = 0, Tavg = 0, DeltaT = 0, CoordinatesJson = JsonSerializer.Serialize(new { x, y, rw, rh }), Notes = "Area de atencao" };
        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Area de atencao ({rw}x{rh}) criada.";
        return measurement;
    }

    public async Task<ThermalMeasurement?> AddLineAtNormalizedAsync(double startY, double endY)
    {
        if (_loadedImage is null || SelectedThermogram is null) return null;
        var lineY = Math.Clamp((int)Math.Round(((startY + endY) / 2.0) * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var profile = _thermalService.GetHorizontalLineProfile(_loadedImage, lineY);
        var measurement = new ThermalMeasurement { ThermogramId = SelectedThermogram.Id, Type = MeasurementType.Line, Tmin = profile.Statistics.Tmin, Tmax = profile.Statistics.Tmax, Tavg = profile.Statistics.Tavg, DeltaT = profile.Statistics.DeltaT, CoordinatesJson = JsonSerializer.Serialize(new { y = lineY, profile = profile.Temperatures.Take(250).ToArray() }), Notes = "Linha horizontal manual no canvas" };
        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Linha: Tmax {profile.Statistics.Tmax:F1} oC";
        return measurement;
    }

    public async Task<ThermalMeasurement?> AddCircleAtNormalizedAsync(double startX, double startY, double endX, double endY)
    {
        if (_loadedImage is null || SelectedThermogram is null) return null;
        var x1 = Math.Clamp((int)Math.Round(startX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y1 = Math.Clamp((int)Math.Round(startY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var x2 = Math.Clamp((int)Math.Round(endX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y2 = Math.Clamp((int)Math.Round(endY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var cx = (x1 + x2) / 2;
        var cy = (y1 + y2) / 2;
        var radius = Math.Max(2, (int)Math.Round(Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)) / 2.0));
        var stats = GetCircleStatistics(_loadedImage, cx, cy, radius);
        var measurement = new ThermalMeasurement { ThermogramId = SelectedThermogram.Id, Type = MeasurementType.Circle, Tmin = stats.Tmin, Tmax = stats.Tmax, Tavg = stats.Tavg, DeltaT = stats.DeltaT, CoordinatesJson = JsonSerializer.Serialize(new { cx, cy, radius }), Notes = "Circulo manual no canvas" };
        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Circulo (r={radius}) Tmax {stats.Tmax:F1} oC";
        return measurement;
    }

    private async Task AddDifferenceAsync()
    {
        if (SelectedThermogram is null) { StatusMessage = "Selecione um termograma para calcular diferenca."; return; }
        var latestTwoSpots = Measurements.Where(x => x.Type == MeasurementType.Spot).OrderByDescending(x => x.CreatedAtUtc).Take(2).ToList();
        if (latestTwoSpots.Count < 2) { StatusMessage = "Para diferenca, adicione pelo menos 2 spots."; return; }
        var s1 = latestTwoSpots[0];
        var s2 = latestTwoSpots[1];
        var delta = Math.Abs(s1.Tmax - s2.Tmax);
        var measurement = new ThermalMeasurement { ThermogramId = SelectedThermogram.Id, Type = MeasurementType.Difference, Tmin = Math.Min(s1.Tmax, s2.Tmax), Tmax = Math.Max(s1.Tmax, s2.Tmax), DeltaT = delta, CoordinatesJson = JsonSerializer.Serialize(new { spot1Id = s1.Id, spot2Id = s2.Id }), Notes = $"DeltaT entre spots: {delta:F1} oC" };
        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        StatusMessage = $"Diferenca calculada: {delta:F1} oC";
    }

    private async Task AddIsothermAsync()
    {
        if (_loadedImage is null || SelectedThermogram is null) { StatusMessage = "Abra um termograma antes de criar isoterma."; return; }
        var stats = GetIsothermStatisticsByMode(_loadedImage, SelectedIsothermMode);
        if (stats.Tmax <= 0) { StatusMessage = "Nenhum pixel corresponde aos criterios da isoterma configurada."; return; }
        var measurement = new ThermalMeasurement { ThermogramId = SelectedThermogram.Id, Type = MeasurementType.Isotherm, Tmin = stats.Tmin, Tmax = stats.Tmax, Tavg = stats.Tavg, DeltaT = stats.DeltaT, CoordinatesJson = JsonSerializer.Serialize(new { mode = SelectedIsothermMode, lowerC = IsothermThresholdC, upperC = IsothermUpperThresholdC, humidityRelativeLimit = HumidityRelativeLimit, insulationIndoorC = InsulationIndoorC, insulationOutdoorC = InsulationOutdoorC, insulationThermalIndex = InsulationThermalIndex }), Notes = BuildIsothermNote() };
        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Isoterma {SelectedIsothermMode}: Tmax {stats.Tmax:F1} oC";
    }

    private (double Tmin, double Tmax, double Tavg, double DeltaT) GetCircleStatistics(ThermalImageData image, int cx, int cy, int radius)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        var count = 0;
        var r2 = radius * radius;
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(image.Height - 1, cy + radius); y++)
        {
            for (var x = Math.Max(0, cx - radius); x <= Math.Min(image.Width - 1, cx + radius); x++)
            {
                if (Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2) <= r2)
                {
                    var t = image.Temperatures[y, x];
                    if (t < min) min = t;
                    if (t > max) max = t;
                    sum += t;
                    count++;
                }
            }
        }
        return count > 0 ? (min, max, sum / count, max - min) : (0, 0, 0, 0);
    }

    private ThermalStatistics GetIsothermStatisticsByMode(ThermalImageData image, IsothermMode mode)
    {
        var lower = Math.Min(IsothermThresholdC, IsothermUpperThresholdC);
        var upper = Math.Max(IsothermThresholdC, IsothermUpperThresholdC);

        var ambient = image.Metadata.AmbientTemperatureC ?? (lower + upper) / 2.0;
        var humidityRiskTemp = ambient - ((100.0 - Math.Clamp(HumidityRelativeLimit, 1, 100)) / 5.0);
        var insulationRiskTemp = InsulationOutdoorC + (Math.Clamp(InsulationThermalIndex, 0.0, 1.0) * (InsulationIndoorC - InsulationOutdoorC));

        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        var count = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                var include = mode switch
                {
                    IsothermMode.Above => t >= IsothermThresholdC,
                    IsothermMode.Below => t <= IsothermThresholdC,
                    IsothermMode.Interval => t >= lower && t <= upper,
                    IsothermMode.Humidity => t <= humidityRiskTemp,
                    IsothermMode.Insulation => t <= insulationRiskTemp,
                    IsothermMode.Custom => t >= lower && t <= upper,
                    _ => false
                };

                if (!include) continue;

                if (t < min) min = t;
                if (t > max) max = t;
                sum += t;
                count++;
            }
        }

        if (count == 0) return new ThermalStatistics();

        return new ThermalStatistics
        {
            Tmin = min,
            Tmax = max,
            Tavg = sum / count
        };
    }

    private string BuildIsothermNote()
    {
        return SelectedIsothermMode switch
        {
            IsothermMode.Above => $"Isoterma Above >= {IsothermThresholdC:F1} oC",
            IsothermMode.Below => $"Isoterma Below <= {IsothermThresholdC:F1} oC",
            IsothermMode.Interval => $"Isoterma Interval {Math.Min(IsothermThresholdC, IsothermUpperThresholdC):F1}..{Math.Max(IsothermThresholdC, IsothermUpperThresholdC):F1} oC",
            IsothermMode.Humidity => $"Isoterma Humidity (RH limite {HumidityRelativeLimit:F0}%)",
            IsothermMode.Insulation => $"Isoterma Insulation (Ti {InsulationThermalIndex:F2})",
            IsothermMode.Custom => $"Isoterma Custom {Math.Min(IsothermThresholdC, IsothermUpperThresholdC):F1}..{Math.Max(IsothermThresholdC, IsothermUpperThresholdC):F1} oC",
            _ => "Isoterma personalizada"
        };
    }

    private async Task ActivateSpotToolAsync() { ActiveTool = AnalysisTool.Spot; StatusMessage = "Ferramenta Spot ativa. Clique na imagem para marcar."; await Task.CompletedTask; }
    private async Task ActivateAreaToolAsync() { ActiveTool = AnalysisTool.Area; StatusMessage = "Ferramenta Retangulo (ilustracao) ativa. Clique e arraste para destacar elementos."; await Task.CompletedTask; }
    private async Task ActivateLineToolAsync() { ActiveTool = AnalysisTool.Line; StatusMessage = "Ferramenta Seta (ilustracao) ativa. Clique e arraste para apontar elementos."; await Task.CompletedTask; }
    private async Task ActivateCircleToolAsync() { ActiveTool = AnalysisTool.Circle; StatusMessage = "Ferramenta Circulo (ilustracao) ativa. Clique e arraste para destacar elementos."; await Task.CompletedTask; }
    private async Task ActivateAutoAdjustRegionToolAsync() { ActiveTool = AnalysisTool.AutoAdjustRegion; StatusMessage = "Auto-adjust region ativa. Clique e arraste para definir a regiao de ajuste automatico."; await Task.CompletedTask; }
    private async Task ClearAutoAdjustRegionAsync() { _autoAdjustRegion = null; UpdateDisplayImage(); StatusMessage = "Regiao de auto-ajuste removida."; await Task.CompletedTask; }
    private async Task ApplyHumidityPresetAsync() { SelectedIsothermMode = IsothermMode.Humidity; HumidityRelativeLimit = 70; StatusMessage = "Preset de isoterma de umidade aplicado (RH limite 70%)."; await Task.CompletedTask; }
    private async Task ApplyInsulationPresetAsync() { SelectedIsothermMode = IsothermMode.Insulation; InsulationIndoorC = 22; InsulationOutdoorC = 12; InsulationThermalIndex = 0.70; StatusMessage = "Preset de isoterma de insulação aplicado (22/12 C, índice 0.70)."; await Task.CompletedTask; }
    public Task SetAutoAdjustRegionNormalizedAsync(double startX, double startY, double endX, double endY) { _autoAdjustRegion = (startX, startY, endX, endY); UpdateDisplayImage(); StatusMessage = "Regiao de auto-ajuste aplicada."; return Task.CompletedTask; }
}
