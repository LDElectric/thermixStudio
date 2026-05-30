using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThermixStudio.Core;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private async Task UndoLastActionAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para desfazer.";
            return;
        }

        if (await TryUndoIllustrationActionAsync()) return;

        var latest = Measurements.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        if (latest is null)
        {
            StatusMessage = "Nao ha acoes para desfazer neste termograma.";
            return;
        }

        var removed = await _dataService.RemoveMeasurementAsync(latest.Id);
        if (!removed)
        {
            StatusMessage = "Nao foi possivel desfazer a ultima acao.";
            return;
        }

        Measurements.Remove(latest);
        MeasurementRemoved?.Invoke(latest.Id);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = "Ultima acao desfeita (Ctrl+Z).";
    }

    private async Task RemoveSelectedMeasurementAsync()
    {
        if (SelectedThermogram is null || SelectedMeasurement is null)
        {
            StatusMessage = "Selecione uma medicao para remover.";
            return;
        }

        var measurementId = SelectedMeasurement.Id;
        var removed = await _dataService.RemoveMeasurementAsync(measurementId);
        if (!removed)
        {
            StatusMessage = "Nao foi possivel remover a medicao selecionada.";
            return;
        }

        Measurements.Remove(SelectedMeasurement);
        SelectedMeasurement = null;
        MeasurementRemoved?.Invoke(measurementId);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = "Medicao removida.";
    }

    private async Task RemoveSelectedThermogramAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para remover do programa.";
            return;
        }

        var thermogram = SelectedThermogram;
        var removed = await _dataService.RemoveThermogramAsync(thermogram.Id);
        if (!removed)
        {
            StatusMessage = "Nao foi possivel remover o termograma selecionado.";
            return;
        }

        Thermograms.Remove(thermogram);
        if (ReferenceEquals(SelectedThermogram, thermogram))
        {
            SelectedThermogram = Thermograms.FirstOrDefault();
        }

        if (SelectedThermogram is null)
        {
            Measurements.Clear();
            DisplayImage = null;
            _loadedImage = null;
            CurrentScaleLabel = "Escala: -";
        }

        try
        {
            var libraryRoot = EnsureManagedLibraryRoot();
            var fullLibraryRoot = Path.GetFullPath(libraryRoot);
            var fullFilePath = Path.GetFullPath(thermogram.FilePath);

            if (fullFilePath.StartsWith(fullLibraryRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullFilePath))
            {
                File.Delete(fullFilePath);
            }
        }
        catch
        {
            // Keep deletion best-effort
        }

        StatusMessage = $"Termograma removido do programa: {Path.GetFileName(thermogram.FilePath)} (arquivo original preservado).";
    }

    public async Task RemoveThermogramByReferenceAsync(Thermogram? thermogram)
    {
        if (thermogram is null) return;
        SelectedThermogram = thermogram;
        await RemoveSelectedThermogramAsync();
    }

    private async Task DeleteSelectionAsync()
    {
        if (SelectedMeasurement is not null) await RemoveSelectedMeasurementAsync();
        else if (SelectedThermogram is not null) await RemoveSelectedThermogramAsync();
        else StatusMessage = "Nada selecionado para remover.";
    }

    private async Task ApplyAutoScaleAsync()
    {
        if (_loadedImage is null) return;
        var (min, max) = GetPreferredThermalRange(_loadedImage);
        AutoScaleEnabled = true;
        LevelMinC = min;
        LevelMaxC = max;
        UpdateDisplayImage();
        await Task.CompletedTask;
    }

    private async Task ApplyCameraScaleAsync()
    {
        if (_loadedImage is null) return;
        var hasVisualScale = _loadedImage.Metadata.VisualScaleMinC.HasValue &&
            _loadedImage.Metadata.VisualScaleMaxC.HasValue;
        var hasExifScale = _loadedImage.Metadata.PaletteScaleMinC.HasValue &&
            _loadedImage.Metadata.PaletteScaleMaxC.HasValue;
        if (!hasVisualScale && !hasExifScale)
        {
            StatusMessage = "Escala visual da camera nao encontrada.";
            return;
        }

        var min = hasVisualScale
            ? _loadedImage.Metadata.VisualScaleMinC!.Value
            : _loadedImage.Metadata.PaletteScaleMinC!.Value;
        var max = hasVisualScale
            ? _loadedImage.Metadata.VisualScaleMaxC!.Value
            : _loadedImage.Metadata.PaletteScaleMaxC!.Value;
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
        {
            StatusMessage = "Escala visual da camera invalida.";
            return;
        }

        AutoScaleEnabled = false;
        LevelMinC = min;
        LevelMaxC = max;
        UpdateDisplayImage();
        await PersistSelectedThermogramViewStateAsync();
    }

    private async Task ToggleViewModeAsync()
    {
        if (_loadedImage is null) { StatusMessage = "Selecione um termograma para alternar o modo de visualizacao."; return; }
        var modes = ImageViewModes;
        var currentIndex = 0;
        for (var i = 0; i < modes.Count; i++) { if (modes[i] == ImageViewMode) { currentIndex = i; break; } }
        var nextIndex = (currentIndex + 1) % modes.Count;
        ImageViewMode = modes[nextIndex];
        var requiresVisible = ImageViewMode is ImageViewMode.Visible or ImageViewMode.Blending or ImageViewMode.PiP or ImageViewMode.Msx;
        var hasVisible = !string.IsNullOrWhiteSpace(PairedVisibleImagePath) && File.Exists(PairedVisibleImagePath);
        StatusMessage = requiresVisible && !hasVisible ? $"{GetViewModeDisplay(ImageViewMode)} selecionado sem imagem visivel pareada; exibindo termico." : $"Modo {GetViewModeDisplay(ImageViewMode)} ativado.";
        await Task.CompletedTask;
    }

    public async Task SetMeasurementMaxAdmissibleAsync(Guid measurementId, double? maxAdmissible)
    {
        var m = Measurements.FirstOrDefault(x => x.Id == measurementId);
        if (m != null)
        {
            m.MaxAdmissibleC = maxAdmissible;
            await _dataService.UpdateMeasurementAsync(m);
        }
    }

    public async Task RemoveMeasurementByIdAsync(Guid id)
    {
        var m = Measurements.FirstOrDefault(x => x.Id == id);
        if (m != null)
        {
            await _dataService.RemoveMeasurementAsync(id);
            Measurements.Remove(m);
            MeasurementRemoved?.Invoke(id);
        }
    }
}
