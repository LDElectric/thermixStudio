using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private async Task ExportImageCsvAsync()
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para exportar.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV|*.csv",
            FileName = Path.GetFileNameWithoutExtension(SelectedThermogram.FilePath) + "_temp.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using (var writer = new StreamWriter(dialog.FileName, false, System.Text.Encoding.UTF8))
                {
                    for (var y = 0; y < _loadedImage.Height; y++)
                    {
                        var row = new string[_loadedImage.Width];
                        for (var x = 0; x < _loadedImage.Width; x++)
                        {
                            row[x] = _loadedImage.Temperatures[y, x].ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                        }
                        await writer.WriteLineAsync(string.Join(";", row));
                    }
                }
                StatusMessage = "CSV exportado com sucesso.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro ao exportar CSV: {ex.Message}";
            }
        }
    }

    private async Task ExportMeasurementsCsvAsync()
    {
        if (SelectedThermogram is null || Measurements.Count == 0)
        {
            StatusMessage = "Nenhuma medicao para exportar.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV|*.csv",
            FileName = Path.GetFileNameWithoutExtension(SelectedThermogram.FilePath) + "_medicoes.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using var writer = new StreamWriter(dialog.FileName, false, System.Text.Encoding.UTF8);
                await writer.WriteLineAsync("Type;Tmin;Tmax;Tavg;DeltaT;CreatedAtUtc;Notes");
                foreach (var m in Measurements.OrderBy(x => x.CreatedAtUtc))
                {
                    var line = string.Join(
                        ';',
                        m.Type,
                        m.Tmin.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        m.Tmax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        m.Tavg.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        m.DeltaT.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        m.CreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        (m.Notes ?? string.Empty).Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ')
                    );
                    await writer.WriteLineAsync(line);
                }
                StatusMessage = "Relatorio de medicoes exportado.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro ao exportar medicoes: {ex.Message}";
            }
        }
    }

    private async Task ExportIdenticalJpgAsync()
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para exportar.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "JPEG|*.jpg",
            FileName = Path.GetFileNameWithoutExtension(SelectedThermogram.FilePath) + "_analise.jpg"
        };

        if (dialog.ShowDialog() == true)
        {
            string? capturedCurrentViewPath = null;
            try
            {
                // 1. Obter a captura visual exata da View (modo de fusão, marcações, paleta)
                if (ReportSnapshotRequested is not null)
                {
                    foreach (var callback in ReportSnapshotRequested.GetInvocationList().OfType<Func<Task<string?>>>())
                    {
                        try
                        {
                            capturedCurrentViewPath = await callback();
                            if (!string.IsNullOrWhiteSpace(capturedCurrentViewPath)) break;
                        }
                        catch { }
                    }
                }

                System.Windows.Media.Imaging.BitmapSource visualSource;
                
                if (!string.IsNullOrWhiteSpace(capturedCurrentViewPath) && File.Exists(capturedCurrentViewPath))
                {
                    // Carrega a captura da UI com cache em OnLoad para liberar o arquivo no disco
                    var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.UriSource = new Uri(capturedCurrentViewPath);
                    bitmapImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    visualSource = bitmapImage;
                }
                else if (DisplayImage is not null)
                {
                    // Fallback caso a View não retorne o snapshot
                    visualSource = (System.Windows.Media.Imaging.BitmapSource)DisplayImage;
                }
                else
                {
                    StatusMessage = "Falha ao obter a imagem para exportação.";
                    return;
                }

                // 2. Extrair metadados (incluindo dados radiométricos) do termograma original
                System.Windows.Media.Imaging.BitmapMetadata? originalMetadata = null;
                if (File.Exists(SelectedThermogram.FilePath))
                {
                    using (var originalStream = File.OpenRead(SelectedThermogram.FilePath))
                    {
                        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                            originalStream, 
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, 
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        
                        if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is System.Windows.Media.Imaging.BitmapMetadata meta)
                        {
                            originalMetadata = meta.Clone();
                        }
                    }
                }

                // 3. Mesclar a imagem capturada com os metadados do arquivo original
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 100 };
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(visualSource, null, originalMetadata, null);
                encoder.Frames.Add(frame);

                // 4. Salvar o arquivo final
                using (var stream = File.Create(dialog.FileName))
                {
                    encoder.Save(stream);
                }

                var metadataCopied = await _metadataPreservationService.CopyOriginalMetadataAsync(
                    SelectedThermogram.FilePath,
                    dialog.FileName);
                if (!metadataCopied)
                {
                    StatusMessage = "Imagem exportada; ExifTool indisponivel ou falhou ao copiar todos os metadados FLIR.";
                    return;
                }
                
                StatusMessage = "Imagem exportada com sucesso (visual exato + dados radiométricos).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro ao exportar imagem: {ex.Message}";
            }
            finally
            {
                // Limpar o arquivo temporário gerado pelo snapshot
                if (!string.IsNullOrWhiteSpace(capturedCurrentViewPath) && File.Exists(capturedCurrentViewPath))
                {
                    try { File.Delete(capturedCurrentViewPath); } catch { }
                }
            }
        }
    }

    private async Task GenerateReportAsync()
    {
        if (SelectedThermogram is null || _loadedImage is null)
        {
            StatusMessage = "Selecione um termograma antes de gerar relatório.";
            return;
        }

        SyncEditableFieldsToSelectedThermogram();
        PersistCurrentStateToSelectedThermogram();
        await _dataService.UpdateThermogramAsync(SelectedThermogram);

        string? capturedCurrentViewPath = null;
        if (ReportSnapshotRequested is not null)
        {
            foreach (var callback in ReportSnapshotRequested.GetInvocationList().OfType<Func<Task<string?>>>())
            {
                try
                {
                    capturedCurrentViewPath = await callback();
                    if (!string.IsNullOrWhiteSpace(capturedCurrentViewPath)) break;
                }
                catch { }
            }
        }

        var editor = _serviceProvider.GetRequiredService<ThermixStudio.App.ReportEditorWindow>();
        await editor.ViewModel.LoadAsync(
            SelectedInspection ?? new Inspection
            {
                OsNumber = "-",
                TechnicianName = "N/A",
                StartAtUtc = DateTime.UtcNow
            },
            Thermograms,
            SelectedThermogram,
            capturedCurrentViewPath);

        editor.Owner = Application.Current?.MainWindow;
        editor.Show();
        editor.Activate();
        StatusMessage = "Editor de relatório aberto.";
    }

    private async Task SaveThermogramPropertiesAsync()
    {
        if (SelectedThermogram is null) return;
        SyncEditableFieldsToSelectedThermogram();
        PersistCurrentStateToSelectedThermogram();
        await _dataService.UpdateThermogramAsync(SelectedThermogram);
        StatusMessage = "Propriedades do termograma salvas.";
    }
}
