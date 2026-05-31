using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private void SyncEditableFieldsToSelectedThermogram()
    {
        if (SelectedThermogram is null) return;
        SelectedThermogram.EquipmentTag = ThermogramEquipmentTag;
        SelectedThermogram.EquipmentDescription = ThermogramEquipmentDescription;
        SelectedThermogram.EquipmentLocation = ThermogramEquipmentLocation;
        SelectedThermogram.Criticality = ThermogramCriticality;
        SelectedThermogram.Notes = ThermogramNotes;
        SelectedThermogram.InspectionId = SelectedInspection?.Id;
    }

    private void PersistCurrentStateToSelectedThermogram()
    {
        if (SelectedThermogram is null) return;
        SelectedThermogram.ProcessingJson = JsonSerializer.Serialize(new ThermalProcessingState
        {
            ViewMode = MapToCoreImageViewMode(ImageViewMode),
            AutoScale = AutoScaleEnabled,
            LevelMinC = LevelMinC,
            LevelMaxC = LevelMaxC,
            VisualScaleMinC = _loadedImage?.Metadata.VisualScaleMinC,
            VisualScaleMaxC = _loadedImage?.Metadata.VisualScaleMaxC,
            VisualScaleSource = _loadedImage?.Metadata.VisualScaleSource ?? VisualScaleSource.Unknown,
            VisualScaleConfidence = _loadedImage?.Metadata.VisualScaleConfidence,
            MaxAdmissibleC = MaxAdmissibleC,
            Emissivity = Emissivity,
            Palette = NormalizeSupportedPalette(SelectedPalette),
            BlendFactor = BlendFactor,
            PipScale = PipScale,
            MsxStrength = Math.Clamp(MsxStrength, 0.0, 1.0),
            MetadataDetectedMode = _metadataDetectedMode,
            VisualInferenceInitialized = true,
            VisualInferenceRuleVersion = CurrentVisualInferenceRuleVersion,
            VisibleImagePath = PairedVisibleImagePath,
            Illustrations = Illustrations.OfType<ThermalIllustration>().Select(CloneIllustration).ToList()
        });
        SelectedThermogram.MetadataJson = SaveVisibleImagePath(SelectedThermogram.MetadataJson, PairedVisibleImagePath);
    }

    private async Task PersistSelectedThermogramViewStateAsync()
    {
        if (_loadingThermogram || SelectedThermogram is null) return;
        try
        {
            PersistCurrentStateToSelectedThermogram();
            await _dataService.UpdateThermogramAsync(SelectedThermogram);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STATE_SAVE] Falha ao persistir estado do termograma: {ex.Message}");
            LogToFile($"[STATE_SAVE] Falha ao persistir estado do termograma: {ex.Message}");
        }
    }

    private static string SaveVisibleImagePath(string? metadataJson, string? visiblePath)
    {
        try
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(metadataJson ?? "{}");
            if (doc is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["VisibleImagePath"] = visiblePath;
                return obj.ToJsonString();
            }
        }
        catch { }
        return metadataJson ?? "{}";
    }

    private static ThermalProcessingState BuildDefaultProcessingState(ThermalImageData? imageData)
    {
        if (imageData is null) return new ThermalProcessingState();
        var (min, max) = GetPreferredThermalRange(imageData);
        return new ThermalProcessingState
        {
            ViewMode = imageData.Metadata.DetectedViewMode ?? global::ThermixStudio.Core.ImageViewMode.Thermal,
            AutoScale = true,
            LevelMinC = min,
            LevelMaxC = max,
            VisualScaleMinC = imageData.Metadata.VisualScaleMinC,
            VisualScaleMaxC = imageData.Metadata.VisualScaleMaxC,
            VisualScaleSource = imageData.Metadata.VisualScaleSource,
            VisualScaleConfidence = imageData.Metadata.VisualScaleConfidence,
            Emissivity = imageData.Metadata.Emissivity ?? 0.95,
            Palette = ResolvePaletteFromMetadata(imageData.Metadata),
            MetadataDetectedMode = imageData.Metadata.DetectedViewMode,
            VisibleImagePath = imageData.Metadata.VisibleImagePath,
            VisualInferenceInitialized = false,
            VisualInferenceRuleVersion = 0
        };
    }

    private static ThermalProcessingState ExtractProcessingState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ThermalProcessingState();
        try
        {
            return JsonSerializer.Deserialize<ThermalProcessingState>(json) ?? new ThermalProcessingState();
        }
        catch
        {
            return new ThermalProcessingState();
        }
    }

    private static global::ThermixStudio.Core.ImageViewMode MapToCoreImageViewMode(ImageViewMode mode)
    {
        return mode switch
        {
            ImageViewMode.Original => global::ThermixStudio.Core.ImageViewMode.Original,
            ImageViewMode.Thermal => global::ThermixStudio.Core.ImageViewMode.Thermal,
            ImageViewMode.Visible => global::ThermixStudio.Core.ImageViewMode.Visible,
            ImageViewMode.Fusion => global::ThermixStudio.Core.ImageViewMode.Fusion,
            ImageViewMode.Blending => global::ThermixStudio.Core.ImageViewMode.Blending,
            ImageViewMode.PiP => global::ThermixStudio.Core.ImageViewMode.PiP,
            ImageViewMode.Msx => global::ThermixStudio.Core.ImageViewMode.Msx,
            _ => global::ThermixStudio.Core.ImageViewMode.Thermal
        };
    }

    private static ImageViewMode MapFromCoreImageViewMode(global::ThermixStudio.Core.ImageViewMode mode)
    {
        return mode switch
        {
            global::ThermixStudio.Core.ImageViewMode.Original => ImageViewMode.Original,
            global::ThermixStudio.Core.ImageViewMode.Thermal => ImageViewMode.Thermal,
            global::ThermixStudio.Core.ImageViewMode.Visible => ImageViewMode.Visible,
            global::ThermixStudio.Core.ImageViewMode.Fusion => ImageViewMode.Fusion,
            global::ThermixStudio.Core.ImageViewMode.Blending => ImageViewMode.Blending,
            global::ThermixStudio.Core.ImageViewMode.PiP => ImageViewMode.PiP,
            global::ThermixStudio.Core.ImageViewMode.Msx => ImageViewMode.Msx,
            _ => ImageViewMode.Thermal
        };
    }
}
