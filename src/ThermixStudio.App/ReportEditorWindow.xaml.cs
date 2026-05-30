using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using ThermixStudio.App.ViewModels;

namespace ThermixStudio.App;

public partial class ReportEditorWindow : Window
{
    public ReportEditorViewModel ViewModel { get; }
    private bool _isPreviewReady;
    private bool _initialHtmlLoaded;

    public ReportEditorWindow(ReportEditorViewModel viewModel)
    {
        InitializeComponent();
        WindowIconHelper.Apply(this);
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PdfRenderRequested += OnPdfRenderRequestedAsync;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsurePreviewReadyAsync();
        RefreshPreview();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportEditorViewModel.PreviewHtml))
        {
            RefreshPreview();
        }
    }

    private async void RefreshPreview()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.PreviewHtml))
        {
            return;
        }

        if (!await EnsurePreviewReadyAsync())
        {
            return;
        }

        var html = ViewModel.PreviewHtml;

        await Dispatcher.InvokeAsync(async () =>
        {
            if (!_initialHtmlLoaded)
            {
                PreviewBrowser.NavigateToString(html);
                _initialHtmlLoaded = true;
            }
            else
            {
                // Atualiza sem causar piscar: reescreve o documento via JS
                var escaped = html
                    .Replace("\\", "\\\\")
                    .Replace("`", "\\`")
                    .Replace("${", "\\${");
                await PreviewBrowser.CoreWebView2.ExecuteScriptAsync(
                    $"document.open(); document.write(`{escaped}`); document.close();");
            }
        });
    }

    private async Task<bool> EnsurePreviewReadyAsync()
    {
        if (_isPreviewReady)
        {
            return true;
        }

        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThermixStudio", "WebView2Cache");
            Directory.CreateDirectory(cacheDir);
            var env = await CoreWebView2Environment.CreateAsync(null, cacheDir);
            await PreviewBrowser.EnsureCoreWebView2Async(env);
            if (PreviewBrowser.CoreWebView2 is not null)
            {
                PreviewBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                PreviewBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                PreviewBrowser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            }

            _isPreviewReady = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReportEditorWindow] WebView2 initialization failed: {ex.Message}");
            return false;
        }
    }

    private void OnCloseRequested(bool generated)
    {
        Dispatcher.Invoke(Close);
    }

    private async Task<bool> OnPdfRenderRequestedAsync(string pdfPath)
    {
        if (!await EnsurePreviewReadyAsync())
        {
            MessageBox.Show(this, "Não foi possível inicializar o WebView2 para geração de PDF.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            var settings = PreviewBrowser.CoreWebView2.Environment.CreatePrintSettings();
            settings.PageWidth = 8.267;   // A4 em polegadas
            settings.PageHeight = 11.693;
            settings.MarginTop = 0.47;
            settings.MarginBottom = 0.47;
            settings.MarginLeft = 0.47;
            settings.MarginRight = 0.47;
            settings.ShouldPrintBackgrounds = true;
            settings.HeaderTitle = string.Empty;
            settings.FooterUri = string.Empty;

            await PreviewBrowser.CoreWebView2.PrintToPdfAsync(pdfPath, settings);

            MessageBox.Show(this,
                $"Relatório gerado com sucesso!\n\n{pdfPath}",
                "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReportEditorWindow] PDF generation failed: {ex.Message}");
            MessageBox.Show(this, $"Falha ao gerar o relatório PDF.\n{ex.Message}", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async void DownloadPdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sections.Count == 0)
        {
            MessageBox.Show(this, "Adicione ao menos um termograma ao relatório antes de baixar o PDF.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!await EnsurePreviewReadyAsync())
        {
            return;
        }

        var suggestedOs = string.IsNullOrWhiteSpace(ViewModel.OsNumber) ? "SEM-OS" : ViewModel.OsNumber.Trim();
        var suggestedFileName = $"{suggestedOs}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";

        var saveDialog = new SaveFileDialog
        {
            Title = "Salvar relatório em PDF",
            Filter = "Arquivo PDF (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedFileName
        };

        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var settings = PreviewBrowser.CoreWebView2.Environment.CreatePrintSettings();
            settings.PageWidth = 8.267;
            settings.PageHeight = 11.693;
            settings.MarginTop = 0.47;
            settings.MarginBottom = 0.47;
            settings.MarginLeft = 0.47;
            settings.MarginRight = 0.47;
            settings.ShouldPrintBackgrounds = true;
            settings.HeaderTitle = string.Empty;
            settings.FooterUri = string.Empty;

            await PreviewBrowser.CoreWebView2.PrintToPdfAsync(saveDialog.FileName, settings);
            MessageBox.Show(this, "PDF salvo com sucesso.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReportEditorWindow] Failed to save PDF: {ex.Message}");
            MessageBox.Show(this, "Falha ao salvar o PDF no local selecionado.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GenerateReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sections.Count == 0)
        {
            MessageBox.Show(this, "Adicione ao menos um termograma ao relatório.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ViewModel.GenerateReportCommand.ExecuteAsync(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        ViewModel.CloseRequested -= OnCloseRequested;
        ViewModel.PdfRenderRequested -= OnPdfRenderRequestedAsync;
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        PreviewBrowser.Dispose();
        base.OnClosed(e);
    }
}