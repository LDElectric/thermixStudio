using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Win32;
using ThermixStudio.Core;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private async Task LoadDataAsync()
    {
        Inspections.Clear();
        foreach (var insp in await _dataService.GetInspectionsAsync())
        {
            Inspections.Add(insp);
        }

        Thermograms.Clear();
        foreach (var therm in await _dataService.GetAllThermogramsAsync())
        {
            Thermograms.Add(therm);
        }

        SelectedThermogram = Thermograms.FirstOrDefault();
        StatusMessage = "Dados carregados.";
    }

    partial void OnSelectedThermogramChanged(Thermogram? value)
    {
        if (value is null)
        {
            CurrentImagePath = null;
            DisplayImage = null;
            _metadataDetectedMode = null;
            PairedVisibleImagePath = null;
            Measurements.Clear();
            Illustrations.Clear();
                ClearPerThermogramCaches();
                _renderDebounceCts?.Cancel();
            
            // Sincronizar UI para campos editáveis
            ThermogramEquipmentTag = string.Empty;
            ThermogramEquipmentDescription = string.Empty;
            ThermogramEquipmentLocation = string.Empty;
            ThermogramNotes = string.Empty;
            ThermogramCriticality = EquipmentCriticality.Medium;

            StatusMessage = "Nenhum termograma selecionado.";
            return;
        }

            // Cancelar carga anterior para evitar empilhamento de operações pesadas
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var loadToken = _loadCts.Token;

        CurrentImagePath = value.FilePath;
        
        // Sincronizar UI a partir do modelo
        ThermogramEquipmentTag = value.EquipmentTag ?? string.Empty;
        ThermogramEquipmentDescription = value.EquipmentDescription ?? string.Empty;
        ThermogramEquipmentLocation = value.EquipmentLocation ?? string.Empty;
        ThermogramNotes = value.Notes ?? string.Empty;
        ThermogramCriticality = value.Criticality;

            _ = LoadSelectedThermogramAsync(value, loadToken);
    }

        private async Task LoadSelectedThermogramAsync(Thermogram thermogram, CancellationToken cancellationToken = default)
    {
        _loadingThermogram = true;
            ClearPerThermogramCaches();
        Measurements.Clear();
        Illustrations.Clear();

        if (!File.Exists(thermogram.FilePath))
        {
            StatusMessage = $"Arquivo nao encontrado: {thermogram.FilePath}";
            _loadedImage = null;
            DisplayImage = null;
            RefreshTrendPlot([]);
            _loadingThermogram = false;
            return;
        }

        try
        {
            // Verifica cache LRU antes de recarregar do disco
            if (!TryGetCachedImage(thermogram.FilePath, out var cachedImage))
            {
                _loadedImage = await _thermalService.LoadImageAsync(thermogram.FilePath);
                if (_loadedImage is not null)
                    CacheLoadedImage(thermogram.FilePath, _loadedImage);
            }
            else
            {
                _loadedImage = cachedImage;
                Debug.WriteLine($"[CACHE] Hit: {Path.GetFileName(thermogram.FilePath)}");
            }
        }
        catch
        {
            _loadedImage = null;
        }

        await _viewPipeline.PrepareThermogramAsync(thermogram.FilePath);

        var processing = ExtractProcessingState(thermogram.ProcessingJson);
       var shouldPersistProcessingMetadataUpdate = false; // note: CancellationToken already checked below
        _inferredCaptureMode = null;
        _metadataDetectedMode = _loadedImage?.Metadata.DetectedViewMode;
        
        if (!_metadataDetectedMode.HasValue)
        {
          _metadataDetectedMode = await DetectOriginalCaptureModeAsync(thermogram.FilePath, cancellationToken);
        }
            if (cancellationToken.IsCancellationRequested) { _loadingThermogram = false; return; }

            if (processing.MetadataDetectedMode != _metadataDetectedMode)
        {
            shouldPersistProcessingMetadataUpdate = true;
        }

        Emissivity = processing.Emissivity;
        SelectedPalette = NormalizeSupportedPalette(processing.Palette);
        AutoScaleEnabled = processing.AutoScale;
        BlendFactor = Math.Clamp(processing.BlendFactor, 0.0, 1.0);
        PipScale = Math.Clamp(processing.PipScale, 0.10, 1.0);
        MsxStrength = Math.Clamp(processing.MsxStrength, 0.0, 1.0);
        MaxAdmissibleC = processing.MaxAdmissibleC;
        ImageViewMode = MapFromCoreImageViewMode(processing.ViewMode);

        if (processing.ViewMode == global::ThermixStudio.Core.ImageViewMode.Thermal &&
            _metadataDetectedMode is global::ThermixStudio.Core.ImageViewMode.PiP or global::ThermixStudio.Core.ImageViewMode.Visible)
        {
            ImageViewMode = MapFromCoreImageViewMode(_metadataDetectedMode.Value);
            shouldPersistProcessingMetadataUpdate = true;
        }

        foreach (var item in processing.Illustrations)
        {
            Illustrations.Add(item);
        }

        if (_loadedImage is not null)
        {
            shouldPersistProcessingMetadataUpdate |= await EnsureVisualScaleAsync(thermogram.FilePath, _loadedImage, processing);
            var (min, max) = GetPreferredThermalRange(_loadedImage);
            SetScaleSliderLimits(_loadedImage);
            LevelMinC = processing.AutoScale ? min : processing.LevelMinC ?? min;
            LevelMaxC = processing.AutoScale ? max : processing.LevelMaxC ?? max;
            
            if (!string.IsNullOrWhiteSpace(_loadedImage.Metadata.VisibleImagePath))
            {
                PairedVisibleImagePath = NormalizeVisibleImagePath(_loadedImage.Metadata.VisibleImagePath, thermogram.FilePath);
            }

            var metadataPalette = ResolvePaletteFromMetadata(_loadedImage.Metadata);
            if (processing.Palette == ThermalPalette.Original ||
                (processing.Palette == ThermalPalette.Iron && metadataPalette != ThermalPalette.Iron))
            {
                SelectedPalette = metadataPalette;
            }

            if (string.IsNullOrWhiteSpace(PairedVisibleImagePath))
            {
                TryEnsureVisiblePairOnDemand();
            }

            var shouldDetectPalette = string.IsNullOrWhiteSpace(_loadedImage.Metadata.PaletteName) &&
                                      !_loadedImage.Metadata.DetectedPalette.HasValue;
            if (shouldDetectPalette)
            {
                try
                {
                    var detectedPalette = await _viewPipeline.DetectPaletteFromFileAsync(thermogram.FilePath);
                    if (!string.IsNullOrEmpty(detectedPalette) &&
                        Enum.TryParse<ThermalPalette>(detectedPalette, out var paletteEnum))
                    {
                        SelectedPalette = paletteEnum;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erro ao detectar paleta: {ex.Message}");
                }
            }

            var shouldInferCapturePresentation =
                (!processing.VisualInferenceInitialized ||
                 processing.VisualInferenceRuleVersion < CurrentVisualInferenceRuleVersion) &&
                ShouldInferCaptureModeFromPixels(_metadataDetectedMode);

            if (shouldInferCapturePresentation)
            {
                try
                {
                    var imagePath = thermogram.FilePath;
                    var visiblePath = PairedVisibleImagePath;
                    var inferTask = Task.Run(() =>
                    {
                        var success = TryInferCapturePresentation(_loadedImage!, imagePath, visiblePath, out var mode, out var palette);
                        return (success, mode, palette);
                    });

                        var completed = await Task.WhenAny(inferTask, Task.Delay(1800, cancellationToken));
                    if (completed == inferTask)
                    {
                        var inference = await inferTask;
                        if (inference.success)
                        {
                            _inferredCaptureMode = MapToCoreImageViewMode(inference.mode);
                            ImageViewMode = inference.mode;
                            if (inference.palette != SelectedPalette)
                            {
                                SelectedPalette = inference.palette;
                            }
                            processing.VisualInferenceInitialized = true;
                            processing.VisualInferenceRuleVersion = CurrentVisualInferenceRuleVersion;
                            shouldPersistProcessingMetadataUpdate = true;
                        }
                    }
                }
                catch (Exception inferEx)
                {
                    Debug.WriteLine($"[MODE_INFER] Falha na inferencia visual: {inferEx.Message}");
                }
            }
        }

        if (cancellationToken.IsCancellationRequested) { _loadingThermogram = false; return; }

        UpdateDisplayImage();
        OnPropertyChanged(nameof(ImagePixelWidth));
        OnPropertyChanged(nameof(ImagePixelHeight));

        if (shouldPersistProcessingMetadataUpdate)
        {
            PersistCurrentStateToSelectedThermogram();
            await _dataService.UpdateThermogramAsync(thermogram);
        }

        foreach (var m in await _dataService.GetMeasurementsByThermogramAsync(thermogram.Id))
        {
            Measurements.Add(m);
        }

        var trend = await _dataService.GetThermogramTrendAsync(thermogram.Id);
        RefreshTrendPlot(trend);

        _loadingThermogram = false;
        StatusMessage = $"Termograma: {Path.GetFileName(thermogram.FilePath)} | {Measurements.Count} medicao(oes)";
    }

    private async Task OpenFileAsync()
    {
        var cameraSources = DetectConnectedCameraSources();
        var defaultThermogramFolder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Termogramas");
        defaultThermogramFolder = Path.GetFullPath(defaultThermogramFolder);
        var initialDirectory = Directory.Exists(defaultThermogramFolder)
            ? defaultThermogramFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        if (cameraSources.Count > 0)
        {
            var camera = cameraSources[0];
            initialDirectory = camera.RootPath;
            var promptResult = System.Windows.MessageBox.Show(
                $"Camera detectada em {camera.DisplayName}.\n\n" +
                "Sim: importar automaticamente todos os termogramas da camera.\n" +
                "Nao: abrir seletor manual de arquivos/pastas.\n" +
                "Cancelar: abortar.",
                "Importacao de termogramas",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            if (promptResult == System.Windows.MessageBoxResult.Cancel) return;
            if (promptResult == System.Windows.MessageBoxResult.Yes)
            {
                var filesFromCamera = EnumerateSupportedThermogramFiles(camera.RootPath).ToList();
                if (filesFromCamera.Count == 0)
                {
                    StatusMessage = $"Camera detectada, mas nenhum termograma suportado foi encontrado em {camera.DisplayName}.";
                    return;
                }
                await ImportFilesAsync(filesFromCamera, camera.DisplayName);
                return;
            }
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Termogramas|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.csv|Todos os arquivos|*.*",
            Title = "Abrir termograma",
            Multiselect = true,
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            await ImportFilesAsync(dialog.FileNames, "selecao manual");
        }
    }

    private async Task ImportFilesAsync(IEnumerable<string> files, string sourceLabel)
    {
        var imported = 0;
        var skipped = 0;
        var errors = 0;
        var libraryRoot = EnsureManagedLibraryRoot();

        foreach (var sourcePath in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(sourcePath)) { skipped++; continue; }
            var filePath = ResolveManagedCopyPath(sourcePath, libraryRoot);

            // Se o arquivo destino já existe ou o termograma já está cadastrado com esse nome, removemos o antigo para importar o novo atualizado
            var existingThermogram = Thermograms.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existingThermogram is not null)
            {
                await _dataService.RemoveThermogramAsync(existingThermogram.Id);
                Thermograms.Remove(existingThermogram);
            }

            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Copy(sourcePath, filePath, overwrite: true);
            }
            catch { errors++; continue; }

            ThermalImageData? imageData = null;
            try
            {
                imageData = await _thermalService.LoadImageAsync(filePath);
                await _viewPipeline.PrepareThermogramAsync(filePath);
                await EnsureVisualScaleAsync(filePath, imageData, null);
            }
            catch { }

            var defaultState = BuildDefaultProcessingState(imageData);
            if (imageData is not null)
            {
                var exifMode = await _viewPipeline.DetectCaptureModeFromMetadataAsync(filePath);
                if (exifMode is global::ThermixStudio.Core.ImageViewMode.PiP or global::ThermixStudio.Core.ImageViewMode.Visible)
                {
                    defaultState.ViewMode = exifMode.Value;
                    defaultState.MetadataDetectedMode = exifMode;
                }
            }

            var thermogram = new Thermogram
            {
                InspectionId = SelectedInspection?.Id,
                FilePath = filePath,
                CaptureAtUtc = DateTime.UtcNow,
                CameraModel = imageData?.Metadata.CameraModel ?? "Unknown",
                MetadataJson = imageData is not null ? JsonSerializer.Serialize(imageData.Metadata) : "{}",
                ProcessingJson = JsonSerializer.Serialize(defaultState),
                Status = "imported"
            };

            var saved = await _dataService.AddThermogramAsync(thermogram);
            Thermograms.Insert(0, saved);
            imported++;
            SelectedThermogram = saved;
        }
        StatusMessage = $"Importacao ({sourceLabel}) concluida: {imported} arquivo(s), {skipped} ignorado(s), {errors} erro(s).";
    }

    private async Task<global::ThermixStudio.Core.ImageViewMode?> DetectOriginalCaptureModeAsync(string? filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        try { return await _viewPipeline.DetectCaptureModeFromMetadataAsync(filePath, cancellationToken); }
        catch { return null; }
    }

    private async Task<bool> EnsureVisualScaleAsync(string imagePath, ThermalImageData imageData, ThermalProcessingState? processing)
    {
        if (processing is not null &&
            processing.VisualScaleMinC.HasValue &&
            processing.VisualScaleMaxC.HasValue &&
            processing.VisualScaleMaxC.Value > processing.VisualScaleMinC.Value)
        {
            imageData.Metadata.VisualScaleMinC = processing.VisualScaleMinC;
            imageData.Metadata.VisualScaleMaxC = processing.VisualScaleMaxC;
            imageData.Metadata.VisualScaleSource = processing.VisualScaleSource;
            imageData.Metadata.VisualScaleConfidence = processing.VisualScaleConfidence;
            return false;
        }

        try
        {
            var detected = await _visualScaleDetector.DetectAsync(imagePath, imageData);
            if (!detected.Success ||
                !detected.MinC.HasValue ||
                !detected.MaxC.HasValue ||
                detected.MaxC.Value <= detected.MinC.Value)
            {
                return false;
            }

            imageData.Metadata.VisualScaleMinC = detected.MinC.Value;
            imageData.Metadata.VisualScaleMaxC = detected.MaxC.Value;
            imageData.Metadata.VisualScaleSource = detected.Source;
            imageData.Metadata.VisualScaleConfidence = detected.Confidence;
            imageData.Metadata.Notes = string.IsNullOrWhiteSpace(imageData.Metadata.Notes)
                ? detected.Notes
                : $"{imageData.Metadata.Notes} {detected.Notes}";

            if (processing is not null)
            {
                processing.VisualScaleMinC = detected.MinC.Value;
                processing.VisualScaleMaxC = detected.MaxC.Value;
                processing.VisualScaleSource = detected.Source;
                processing.VisualScaleConfidence = detected.Confidence;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VISUAL_SCALE] Falha ao detectar escala visual: {ex.Message}");
            return false;
        }
    }

    private static string EnsureManagedLibraryRoot()
    {
        var current = AppContext.BaseDirectory;
        var projectRoot = current;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "src")) || 
                File.Exists(Path.Combine(current, "Thermix Studio.sln")))
            {
                projectRoot = current;
                break;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current || string.IsNullOrEmpty(parent)) break;
            current = parent;
        }

        var root = Path.Combine(projectRoot, "thermixStudioDB", "Library");
        Directory.CreateDirectory(root);
        return root;
    }

    private string ResolveManagedCopyPath(string sourcePath, string libraryRoot)
    {
        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(libraryRoot, fileName);
    }

    private List<CameraImportSource> DetectConnectedCameraSources()
    {
        var sources = new List<CameraImportSource>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Removable))
            {
                var dcim = Path.Combine(drive.RootDirectory.FullName, "DCIM");
                if (Directory.Exists(dcim)) sources.Add(new CameraImportSource(dcim, drive.VolumeLabel ?? drive.Name));
            }
        }
        catch { }
        return sources;
    }

    private static IEnumerable<string> EnumerateSupportedThermogramFiles(string rootPath)
    {
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".csv" };
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current); } catch { continue; }
            foreach (var file in files)
            {
                if (allowedExtensions.Contains(Path.GetExtension(file))) yield return file;
            }
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(current); } catch { continue; }
            foreach (var dir in dirs) pending.Push(dir);
        }
    }

    private sealed record CameraImportSource(string RootPath, string DisplayName);
}
