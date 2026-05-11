using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using ThermixStudio.App.ViewModels;

namespace ThermixStudio.App;

public partial class ReportEditorWindow : Window
{
    public ReportEditorViewModel ViewModel { get; }
    private bool _isPreviewReady;

    public ReportEditorWindow(ReportEditorViewModel viewModel)
    {
        InitializeComponent();
        WindowIconHelper.Apply(this);
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
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

        await Dispatcher.InvokeAsync(() => PreviewBrowser.NavigateToString(ViewModel.PreviewHtml));
    }

    private async Task<bool> EnsurePreviewReadyAsync()
    {
        if (_isPreviewReady)
        {
            return true;
        }

        try
        {
            await PreviewBrowser.EnsureCoreWebView2Async();
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
        Dispatcher.Invoke(() =>
        {
            DialogResult = generated;
            Close();
        });
    }

    private async void DownloadPdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sections.Count == 0)
        {
            MessageBox.Show(this, "Adicione ao menos um termograma ao relatório antes de baixar o PDF.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var tempOutputDirectory = Path.Combine(Path.GetTempPath(), "ThermixStudio", "ReportDownloads");
            Directory.CreateDirectory(tempOutputDirectory);

            var result = await ViewModel.GenerateReportToDirectoryAsync(tempOutputDirectory);
            if (result is null || !File.Exists(result.PdfPath))
            {
                MessageBox.Show(this, "Não foi possível gerar o PDF.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            File.Copy(result.PdfPath, saveDialog.FileName, overwrite: true);
            MessageBox.Show(this, "PDF salvo com sucesso.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReportEditorWindow] Failed to save PDF: {ex.Message}");
            MessageBox.Show(this, "Falha ao salvar o PDF no local selecionado.", "Thermix Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        ViewModel.CloseRequested -= OnCloseRequested;
        ViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        PreviewBrowser.Dispose();
        base.OnClosed(e);
    }
}