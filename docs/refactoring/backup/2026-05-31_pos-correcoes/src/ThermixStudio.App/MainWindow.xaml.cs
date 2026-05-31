using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ThermixStudio.App.Services;
using ThermixStudio.App.ViewModels;
using ThermixStudio.Core;

namespace ThermixStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isPanning;
    private bool _isLeftPanning;
    private bool _isDrawing;
    private Point _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private Point _drawStart;
    private Shape? _previewShape;
    private readonly Dictionary<Guid, List<UIElement>> _markerElements = [];
    private readonly Dictionary<Guid, List<UIElement>> _illustrationElements = [];
    private Guid? _selectedIllustrationId;
    private bool _isDraggingIllustration;
    private Point _illustrationDragStart;
    private ThermalIllustration? _dragStartIllustration;
    private Guid? _lastFittedThermogramId;
    private (int Width, int Height) _lastDisplayImageSize;
    private TextBox? _inlineTextEditor;
    private Guid? _inlineTextIllustrationId;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        WindowIconHelper.Apply(this);
        
        Loaded += async (_, _) =>
        {
            await viewModel.LoadDataCommand.ExecuteAsync(null);
            FitImageToViewport();
        };

        SizeChanged += (_, _) => FitImageToViewport();
        InteractionCanvas.SizeChanged += (_, _) =>
        {
            if (_viewModel.DisplayImage is null)
            {
                return;
            }

            ClearOverlay();
            RedrawPersistentOverlays();
            RedrawAllMeasurementMarkers();
            RedrawAllIllustrations();
        };
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _viewModel.MeasurementRemoved += RemoveMarkerByMeasurement;
        _viewModel.ReportSnapshotRequested += CaptureCurrentViewForReportAsync;
        Closed += (_, _) => _viewModel.ReportSnapshotRequested -= CaptureCurrentViewForReportAsync;
        UpdateInteractionCursor();
    }

    private void MenuItem_Exit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void MenuItem_About_Click(object sender, RoutedEventArgs e)
    {
        var aboutWindow = new AboutWindow
        {
            Owner = this
        };

        aboutWindow.ShowDialog();
    }

    private void MenuItem_Instrucoes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var manualPath = global::System.IO.Path.Combine(AppContext.BaseDirectory, "docs", "manual-instrucoes.html");
            if (!File.Exists(manualPath))
            {
                manualPath = EnsureEmbeddedManualInTemp();
                if (string.IsNullOrWhiteSpace(manualPath) || !File.Exists(manualPath))
                {
                    MessageBox.Show(this,
                        "Manual de instruções não encontrado.",
                        "Thermix Studio",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = manualPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] Failed to open instructions manual: {ex.Message}");
            MessageBox.Show(this,
                "Não foi possível abrir o manual de instruções.",
                "Thermix Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string? EnsureEmbeddedManualInTemp()
    {
        try
        {
            var outputPath = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "thermix_manual_instrucoes.html");
            if (global::System.IO.File.Exists(outputPath) && new global::System.IO.FileInfo(outputPath).Length > 0)
            {
                return outputPath;
            }

            const string resourceName = "ThermixStudio.App.Embedded.manual-instrucoes.html";
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var fs = global::System.IO.File.Open(
                outputPath,
                global::System.IO.FileMode.Create,
                global::System.IO.FileAccess.Write,
                global::System.IO.FileShare.Read);
            stream.CopyTo(fs);
            return outputPath;
        }
        catch
        {
            return null;
        }
    }

    private void MenuItem_HandTool_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ActiveTool = AnalysisTool.Hand;
    }

    private void MenuItem_IllustrationArrow_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ActiveTool = AnalysisTool.IllustrationArrow;
    }

    private void MenuItem_IllustrationRectangle_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ActiveTool = AnalysisTool.IllustrationRectangle;
    }

    private void MenuItem_IllustrationEllipse_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ActiveTool = AnalysisTool.IllustrationEllipse;
    }

    private void MenuItem_IllustrationText_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ActiveTool = AnalysisTool.IllustrationText;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            if (_selectedIllustrationId.HasValue)
            {
                await _viewModel.RemoveIllustrationByIdAsync(_selectedIllustrationId.Value);
                _selectedIllustrationId = null;
                RedrawAllIllustrations();
                e.Handled = true;
                return;
            }

            await _viewModel.DeleteSelectionCommand.ExecuteAsync(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && ThermogramListBox.IsKeyboardFocusWithin)
        {
            InteractionCanvas.Focus();
            e.Handled = true;
        }
    }

    private void ThermogramListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dependencyObject = e.OriginalSource as DependencyObject;
        while (dependencyObject is not null && dependencyObject is not ListBoxItem)
        {
            dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
        }

        if (dependencyObject is ListBoxItem item && item.DataContext is Thermogram thermogram)
        {
            _viewModel.SelectedThermogram = thermogram;
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IMPORTANT: Never set ThermalImageControl.Source directly here!
        // Doing so breaks the WPF binding Source="{Binding DisplayImage}" permanently.
        // The binding handles updating Source automatically via PropertyChanged.
        
        if (e.PropertyName is nameof(MainViewModel.DisplayImage) or nameof(MainViewModel.SelectedThermogram))
        {
            Dispatcher.Invoke(() =>
            {
                var currentThermogramId = _viewModel.SelectedThermogram?.Id;
                var isNewThermogram = currentThermogramId != _lastFittedThermogramId;

                if (isNewThermogram)
                {
                    // Switched to a different thermogram: clear, fit, redraw everything
                    ClearOverlay();
                    _isDrawing = false;
                    _previewShape = null;
                    _lastFittedThermogramId = currentThermogramId;

                    Dispatcher.BeginInvoke(() =>
                    {
                        FitImageToViewport();
                        RedrawPersistentOverlays();
                        RedrawAllMeasurementMarkers();
                        RedrawAllIllustrations();
                    }, System.Windows.Threading.DispatcherPriority.Render);
                }
                else
                {
                    // Same thermogram (palette/mode change): preserve zoom, adjust if dimensions changed
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_viewModel.DisplayImage is BitmapSource newBitmap)
                        {
                            var newW = newBitmap.PixelWidth;
                            var newH = newBitmap.PixelHeight;
                            if (_lastDisplayImageSize.Width > 0 && newW != _lastDisplayImageSize.Width)
                            {
                                // Compensate scale proportionally so visual size stays consistent
                                var ratio = (double)_lastDisplayImageSize.Width / newW;
                                var adjusted = Math.Clamp(ImageScaleTransform.ScaleX * ratio, 0.05, 20.0);
                                ImageScaleTransform.ScaleX = adjusted;
                                ImageScaleTransform.ScaleY = adjusted;
                            }
                            _lastDisplayImageSize = (newW, newH);
                        }
                        RefreshOverlayStateFromPersistedData();
                    }, System.Windows.Threading.DispatcherPriority.Render);
                }
            });
        }

        if (e.PropertyName == nameof(MainViewModel.ActiveTool))
        {
            Dispatcher.Invoke(UpdateInteractionCursor);
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel.DisplayImage is null)
        {
            return;
        }

        e.Handled = true;

        var oldScale = ImageScaleTransform.ScaleX;
        var zoomFactor = e.Delta > 0 ? 1.15 : 0.87;
        var newScale = Math.Clamp(oldScale * zoomFactor, 0.05, 20.0);

        if (Math.Abs(newScale - oldScale) < 0.0001)
        {
            return;
        }

        var mouse = e.GetPosition(ImageScrollViewer);
        var contentX = (mouse.X + ImageScrollViewer.HorizontalOffset) / oldScale;
        var contentY = (mouse.Y + ImageScrollViewer.VerticalOffset) / oldScale;

        ImageScaleTransform.ScaleX = newScale;
        ImageScaleTransform.ScaleY = newScale;

        Dispatcher.BeginInvoke(() =>
        {
            ImageScrollViewer.ScrollToHorizontalOffset((contentX * newScale) - mouse.X);
            ImageScrollViewer.ScrollToVerticalOffset((contentY * newScale) - mouse.Y);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void InteractionCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(ImageScrollViewer);
        _panStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ImageScrollViewer.VerticalOffset;
        Mouse.Capture(InteractionCanvas);
    }

    private async void InteractionCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        if (Mouse.Captured == InteractionCanvas)
            Mouse.Capture(null);

        var releasePos = e.GetPosition(ImageScrollViewer);
        var drag = releasePos - _panStart;
        if (Math.Abs(drag.X) < 6 && Math.Abs(drag.Y) < 6)
        {
            var canvasPos = e.GetPosition(InteractionCanvas);
            var nearbyId = FindNearestSpotMeasurementId(canvasPos, threshold: 14.0);
            if (nearbyId.HasValue)
            {
                await ShowSpotContextMenuAsync(nearbyId.Value, canvasPos);
                e.Handled = true;
                return;
            }

            var illustrationId = FindNearestIllustrationId(canvasPos, threshold: 14.0);
            if (illustrationId.HasValue)
            {
                _selectedIllustrationId = illustrationId;
                await ShowIllustrationContextMenuAsync(illustrationId.Value, canvasPos);
                e.Handled = true;
            }
        }
    }

    private void InteractionCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        // Em qualquer modo, permitir destacar/mover ilustrações existentes.
        if (!_isDraggingIllustration && !_isPanning && !_isLeftPanning && !_isDrawing)
        {
            var canvasPos = e.GetPosition(InteractionCanvas);
            var nearbyId = FindNearestIllustrationId(canvasPos, threshold: 14.0);
            if (nearbyId.HasValue)
            {
                var illustration = _viewModel.Illustrations.FirstOrDefault(i => i.Id == nearbyId.Value);
                InteractionCanvas.Cursor = Cursors.SizeAll;  // Indicar que pode arrastar
                if (illustration?.Type == IllustrationType.Arrow)
                {
                    _viewModel.StatusMessage = "Arraste para mover. Pressione Shift + Arraste para rotacionar a seta.";
                }
                else
                {
                    _viewModel.StatusMessage = "Arraste a ilustração para mover.";
                }
            }
            else
            {
                UpdateInteractionCursor();
            }
        }

        if (_isDraggingIllustration && _selectedIllustrationId.HasValue && _dragStartIllustration is not null)
        {
            var dragCurrent = e.GetPosition(InteractionCanvas);
            
            // Se Shift está pressionado, rotacionar a seta em vez de mover
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                // Rotação: manter ponto inicial fixo, rotacionar ponto final
                var cW = Math.Max(1.0, InteractionCanvas.ActualWidth);
                var cH = Math.Max(1.0, InteractionCanvas.ActualHeight);
                
                // Converter posições normalizadas para pixels
                var startPixelX = _dragStartIllustration.X1 * cW;
                var startPixelY = _dragStartIllustration.Y1 * cH;
                
                // Calcular vetor do início da seta até o mouse
                var dx = dragCurrent.X - startPixelX;
                var dy = dragCurrent.Y - startPixelY;
                
                // Calcular comprimento original da seta
                var originalLength = Math.Sqrt(
                    Math.Pow((_dragStartIllustration.X2 - _dragStartIllustration.X1) * cW, 2) +
                    Math.Pow((_dragStartIllustration.Y2 - _dragStartIllustration.Y1) * cH, 2)
                );
                
                // Calcular novo ângulo a partir da posição do mouse
                var newAngle = Math.Atan2(dy, dx);
                var minLength = 20.0;  // Comprimento mínimo para evitar seta muito pequena
                var currentLength = Math.Sqrt(dx * dx + dy * dy);
                var finalLength = Math.Max(minLength, currentLength);
                
                // Recalcular ponto final mantendo o comprimento
                var newX2Pixel = startPixelX + (finalLength * Math.Cos(newAngle));
                var newY2Pixel = startPixelY + (finalLength * Math.Sin(newAngle));
                
                // Converter de volta para coordenadas normalizadas
                var rotated = new ThermalIllustration
                {
                    Id = _dragStartIllustration.Id,
                    Type = _dragStartIllustration.Type,
                    X1 = _dragStartIllustration.X1,
                    Y1 = _dragStartIllustration.Y1,
                    X2 = Math.Clamp(newX2Pixel / cW, 0.0, 1.0),
                    Y2 = Math.Clamp(newY2Pixel / cH, 0.0, 1.0),
                    Text = _dragStartIllustration.Text
                };
                
                UpsertIllustrationLocal(rotated);
                _viewModel.StatusMessage = "Rotacionando seta com Shift. Solte para confirmar.";
                RedrawAllIllustrations();
                return;
            }
            
            // Modo normal: mover
            var dx_move = dragCurrent.X - _illustrationDragStart.X;
            var dy_move = dragCurrent.Y - _illustrationDragStart.Y;
            var cW_move = Math.Max(1.0, InteractionCanvas.ActualWidth);
            var cH_move = Math.Max(1.0, InteractionCanvas.ActualHeight);
            var nx = dx_move / cW_move;
            var ny = dy_move / cH_move;

            var moved = CloneWithOffset(_dragStartIllustration, nx, ny);
            UpsertIllustrationLocal(moved);
            _viewModel.StatusMessage = "Arrastando ilustração. Pressione Shift para rotacionar.";
            RedrawAllIllustrations();
            return;
        }

        if (_isPanning || _isLeftPanning)
        {
            var currentPan = e.GetPosition(ImageScrollViewer);
            var deltaX = currentPan.X - _panStart.X;
            var deltaY = currentPan.Y - _panStart.Y;

            ImageScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - deltaX);
            ImageScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - deltaY);
            return;
        }

        if (!_isDrawing || _previewShape is null)
        {
            return;
        }

        var current = e.GetPosition(InteractionCanvas);
        if (_viewModel.ActiveTool == AnalysisTool.Area)
        {
            var left = Math.Min(_drawStart.X, current.X);
            var top = Math.Min(_drawStart.Y, current.Y);
            var width = Math.Abs(current.X - _drawStart.X);
            var height = Math.Abs(current.Y - _drawStart.Y);

            if (_previewShape is Rectangle rect)
            {
                rect.Width = Math.Max(1, width);
                rect.Height = Math.Max(1, height);
                Canvas.SetLeft(rect, left);
                Canvas.SetTop(rect, top);
            }
        }
        else if (_viewModel.ActiveTool == AnalysisTool.Circle)
        {
            var left = Math.Min(_drawStart.X, current.X);
            var top = Math.Min(_drawStart.Y, current.Y);
            var width = Math.Abs(current.X - _drawStart.X);
            var height = Math.Abs(current.Y - _drawStart.Y);

            if (_previewShape is Ellipse ellipse)
            {
                ellipse.Width = Math.Max(1, width);
                ellipse.Height = Math.Max(1, height);
                Canvas.SetLeft(ellipse, left);
                Canvas.SetTop(ellipse, top);
            }
        }
        else if (_viewModel.ActiveTool == AnalysisTool.AutoAdjustRegion)
        {
            var left = Math.Min(_drawStart.X, current.X);
            var top = Math.Min(_drawStart.Y, current.Y);
            var width = Math.Abs(current.X - _drawStart.X);
            var height = Math.Abs(current.Y - _drawStart.Y);

            if (_previewShape is Rectangle rect)
            {
                rect.Width = Math.Max(1, width);
                rect.Height = Math.Max(1, height);
                Canvas.SetLeft(rect, left);
                Canvas.SetTop(rect, top);
            }
        }
        else if (_viewModel.ActiveTool == AnalysisTool.Line && _previewShape is Line line)
        {
            line.X1 = _drawStart.X;
            line.Y1 = _drawStart.Y;
            line.X2 = current.X;
            line.Y2 = current.Y;
        }
        else if ((_viewModel.ActiveTool == AnalysisTool.IllustrationArrow) && _previewShape is Line arrow)
        {
            arrow.X1 = _drawStart.X;
            arrow.Y1 = _drawStart.Y;
            arrow.X2 = current.X;
            arrow.Y2 = current.Y;

            // Feedback: mostrar comprimento aproximado
            var dx = current.X - _drawStart.X;
            var dy = current.Y - _drawStart.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            _viewModel.StatusMessage = $"Seta: solte para confirmar. Comprimento: {length:F0}px. Depois use Shift+Arraste para rotacionar.";
        }
        else if ((_viewModel.ActiveTool is AnalysisTool.IllustrationRectangle or AnalysisTool.IllustrationEllipse) && _previewShape is Shape shape)
        {
            var left = Math.Min(_drawStart.X, current.X);
            var top = Math.Min(_drawStart.Y, current.Y);
            var width = Math.Abs(current.X - _drawStart.X);
            var height = Math.Abs(current.Y - _drawStart.Y);

            shape.Width = Math.Max(1, width);
            shape.Height = Math.Max(1, height);
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);

            // Feedback: mostrar dimensões
            var shapeType = _viewModel.ActiveTool == AnalysisTool.IllustrationRectangle ? "Retângulo" : "Elipse";
            _viewModel.StatusMessage = $"{shapeType}: solte para confirmar. Tamanho: {width:F0}x{height:F0}px. Clique e arraste para ajustar depois.";
        }
    }

    private async void InteractionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.DisplayImage is null || _viewModel.ImagePixelWidth <= 0 || _viewModel.ImagePixelHeight <= 0)
        {
            return;
        }

        var point = e.GetPosition(InteractionCanvas);
        var width = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var height = Math.Max(1.0, InteractionCanvas.ActualHeight);

        var nx = Math.Clamp(point.X / width, 0.0, 1.0);
        var ny = Math.Clamp(point.Y / height, 0.0, 1.0);

        if (_inlineTextEditor is not null)
        {
            await CommitInlineTextEditorAsync();
        }

        var hitIllustrationId = FindNearestIllustrationId(point, threshold: 14.0);
        if (hitIllustrationId.HasValue)
        {
            var illustration = _viewModel.Illustrations.FirstOrDefault(i => i.Id == hitIllustrationId.Value) as ThermalIllustration;
            if (illustration is not null)
            {
                _selectedIllustrationId = hitIllustrationId.Value;
                _isDraggingIllustration = true;
                _illustrationDragStart = point;
                _dragStartIllustration = CloneIllustration(illustration);
                Mouse.Capture(InteractionCanvas);
                e.Handled = true;
                return;
            }
        }

        if (_viewModel.ActiveTool == AnalysisTool.Hand)
        {
            _isLeftPanning = true;
            _panStart = e.GetPosition(ImageScrollViewer);
            _panStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
            _panStartVerticalOffset = ImageScrollViewer.VerticalOffset;
            Mouse.Capture(InteractionCanvas);
            return;
        }

        switch (_viewModel.ActiveTool)
        {
            case AnalysisTool.Spot:
            {
                var measurement = await _viewModel.AddSpotAtNormalizedAsync(nx, ny);
                if (measurement is not null)
                {
                    DrawSpotMarker(measurement.Id, point, $"{measurement.Tmax:F1} C");
                }
                break;
            }
            case AnalysisTool.Area:
            {
                _isDrawing = true;
                _drawStart = point;
                _previewShape = new Rectangle
                {
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 2 }
                };
                OverlayCanvas.Children.Add(_previewShape);
                Mouse.Capture(InteractionCanvas);
                break;
            }
            case AnalysisTool.Circle:
            {
                _isDrawing = true;
                _drawStart = point;
                _previewShape = new Ellipse
                {
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 2 }
                };
                OverlayCanvas.Children.Add(_previewShape);
                Mouse.Capture(InteractionCanvas);
                break;
            }
            case AnalysisTool.Line:
            {
                _isDrawing = true;
                _drawStart = point;
                _previewShape = new Line
                {
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 2 }
                };
                OverlayCanvas.Children.Add(_previewShape);
                Mouse.Capture(InteractionCanvas);
                break;
            }
            case AnalysisTool.AutoAdjustRegion:
            {
                _isDrawing = true;
                _drawStart = point;
                _previewShape = new Rectangle
                {
                    Stroke = Brushes.Orange,
                    StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Color.FromArgb(20, 255, 165, 0)),
                    StrokeDashArray = new DoubleCollection { 3, 2 }
                };
                OverlayCanvas.Children.Add(_previewShape);
                Mouse.Capture(InteractionCanvas);
                break;
            }
            case AnalysisTool.IllustrationArrow:
            {
                _isDrawing = true;
                _drawStart = point;
                _previewShape = new Line
                {
                    Stroke = Brushes.Gold,
                    StrokeThickness = 2.0,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                OverlayCanvas.Children.Add(_previewShape);
                Mouse.Capture(InteractionCanvas);
                break;
            }
            case AnalysisTool.IllustrationRectangle:
            {
                _isDrawing = true;
                _drawStart = point;
                _previewShape = new Rectangle
                {
                    Stroke = Brushes.Gold,
                    StrokeThickness = 2.0,
                    Fill = new SolidColorBrush(Color.FromArgb(35, 255, 200, 0)),
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                OverlayCanvas.Children.Add(_previewShape);
                Mouse.Capture(InteractionCanvas);
                break;
            }
            case AnalysisTool.IllustrationEllipse:
            {
                _isDrawing = true;
                _drawStart = point;
                _previewShape = new Ellipse
                {
                    Stroke = Brushes.Gold,
                    StrokeThickness = 2.0,
                    Fill = new SolidColorBrush(Color.FromArgb(35, 255, 200, 0)),
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                OverlayCanvas.Children.Add(_previewShape);
                Mouse.Capture(InteractionCanvas);
                break;
            }
            case AnalysisTool.IllustrationText:
            {
                StartInlineTextEditor(point, null);
                e.Handled = true;
                break;
            }
        }
    }

    private async void InteractionCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingIllustration && _selectedIllustrationId.HasValue)
        {
            _isDraggingIllustration = false;
            if (Mouse.Captured == InteractionCanvas)
            {
                Mouse.Capture(null);
            }

            var moved = _viewModel.Illustrations.FirstOrDefault(i => i.Id == _selectedIllustrationId.Value) as ThermalIllustration;
            if (moved is not null)
            {
                await _viewModel.UpdateIllustrationAsync(moved.Id, moved);
            }

            _dragStartIllustration = null;
            return;
        }

        if (_isLeftPanning)
        {
            _isLeftPanning = false;
            if (Mouse.Captured == InteractionCanvas)
            {
                Mouse.Capture(null);
            }
            return;
        }

        if (!_isDrawing || _viewModel.DisplayImage is null)
        {
            return;
        }

        _isDrawing = false;
        if (Mouse.Captured == InteractionCanvas)
        {
            Mouse.Capture(null);
        }

        var end = e.GetPosition(InteractionCanvas);
        var width = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var height = Math.Max(1.0, InteractionCanvas.ActualHeight);

        var sx = Math.Clamp(_drawStart.X / width, 0.0, 1.0);
        var sy = Math.Clamp(_drawStart.Y / height, 0.0, 1.0);
        var ex = Math.Clamp(end.X / width, 0.0, 1.0);
        var ey = Math.Clamp(end.Y / height, 0.0, 1.0);

        if (_previewShape is not null)
        {
            OverlayCanvas.Children.Remove(_previewShape);
            _previewShape = null;
        }

        if (_viewModel.ActiveTool == AnalysisTool.Area)
        {
            var item = new ThermalIllustration
            {
                Type = IllustrationType.Rectangle,
                X1 = sx,
                Y1 = sy,
                X2 = ex,
                Y2 = ey
            };
            await _viewModel.AddIllustrationAsync(item);
            RedrawAllIllustrations();
        }
        else if (_viewModel.ActiveTool == AnalysisTool.Circle)
        {
            var item = new ThermalIllustration
            {
                Type = IllustrationType.Ellipse,
                X1 = sx,
                Y1 = sy,
                X2 = ex,
                Y2 = ey
            };
            await _viewModel.AddIllustrationAsync(item);
            RedrawAllIllustrations();
        }
        else if (_viewModel.ActiveTool == AnalysisTool.Line)
        {
            var item = new ThermalIllustration
            {
                Type = IllustrationType.Arrow,
                X1 = sx,
                Y1 = sy,
                X2 = ex,
                Y2 = ey
            };
            await _viewModel.AddIllustrationAsync(item);
            RedrawAllIllustrations();
        }
        else if (_viewModel.ActiveTool == AnalysisTool.AutoAdjustRegion)
        {
            await _viewModel.SetAutoAdjustRegionNormalizedAsync(sx, sy, ex, ey);
        }
        else if (_viewModel.ActiveTool == AnalysisTool.IllustrationArrow)
        {
            var item = new ThermalIllustration
            {
                Type = IllustrationType.Arrow,
                X1 = sx,
                Y1 = sy,
                X2 = ex,
                Y2 = ey
            };
            await _viewModel.AddIllustrationAsync(item);
            RedrawAllIllustrations();
        }
        else if (_viewModel.ActiveTool == AnalysisTool.IllustrationRectangle)
        {
            var item = new ThermalIllustration
            {
                Type = IllustrationType.Rectangle,
                X1 = sx,
                Y1 = sy,
                X2 = ex,
                Y2 = ey
            };
            await _viewModel.AddIllustrationAsync(item);
            RedrawAllIllustrations();
        }
        else if (_viewModel.ActiveTool == AnalysisTool.IllustrationEllipse)
        {
            var item = new ThermalIllustration
            {
                Type = IllustrationType.Ellipse,
                X1 = sx,
                Y1 = sy,
                X2 = ex,
                Y2 = ey
            };
            await _viewModel.AddIllustrationAsync(item);
            RedrawAllIllustrations();
        }
    }

    private void FitImageToViewport()
    {
        if (_viewModel.DisplayImage is null)
        {
            Debug.WriteLine("[FitImageToViewport] DisplayImage is null, returning");
            return;
        }

        // Always fit using the currently displayed bitmap dimensions, so all modes
        // are presented at a consistent maximum viewport size.
        var imageWidth = _viewModel.ImagePixelWidth;
        var imageHeight = _viewModel.ImagePixelHeight;

        if (_viewModel.DisplayImage is BitmapSource bitmap)
        {
            imageWidth = bitmap.PixelWidth;
            imageHeight = bitmap.PixelHeight;
        }

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            Debug.WriteLine($"[FitImageToViewport] Invalid image dimensions: {imageWidth}x{imageHeight}");
            return;
        }

        var viewportWidth = Math.Max(1.0, ImageScrollViewer.ViewportWidth);
        var viewportHeight = Math.Max(1.0, ImageScrollViewer.ViewportHeight);

        var fitScale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        fitScale = double.IsFinite(fitScale) ? Math.Clamp(fitScale, 0.05, 20.0) : 1.0;

        // Display at 2x magnification for better spot marking and detail visibility
        // Users can zoom out with mouse wheel if needed (0.05x to 20.0x range)
        var initialScale = Math.Clamp(2.0, 0.05, 20.0);

        Debug.WriteLine($"[FitImageToViewport] Image: {imageWidth}x{imageHeight}, Viewport: {viewportWidth}x{viewportHeight}, Scale: {initialScale} (2x magnification)");

        ImageScaleTransform.ScaleX = initialScale;
        ImageScaleTransform.ScaleY = initialScale;
        _lastDisplayImageSize = (imageWidth, imageHeight);

        ImageScrollViewer.ScrollToHorizontalOffset(0);
        ImageScrollViewer.ScrollToVerticalOffset(0);
    }

    private void ClearOverlay()
    {
        OverlayCanvas.Children.Clear();
        _markerElements.Clear();
        _illustrationElements.Clear();
    }

    private void RefreshOverlayStateFromPersistedData()
    {
        _isDrawing = false;
        _isDraggingIllustration = false;
        _isLeftPanning = false;
        _previewShape = null;
        _dragStartIllustration = null;

        if (Mouse.Captured == InteractionCanvas)
        {
            Mouse.Capture(null);
        }

        ClearOverlay();
        RedrawPersistentOverlays();
        RedrawAllMeasurementMarkers();
        RedrawAllIllustrations();
    }

    private void RedrawPersistentOverlays()
    {
        if (!_viewModel.HasAutoAdjustRegion || _viewModel.AutoAdjustRegionNormalized is not { } region)
        {
            return;
        }

        var width = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var height = Math.Max(1.0, InteractionCanvas.ActualHeight);

        var left = Math.Min(region.startX, region.endX) * width;
        var top = Math.Min(region.startY, region.endY) * height;
        var rectWidth = Math.Max(2.0, Math.Abs(region.endX - region.startX) * width);
        var rectHeight = Math.Max(2.0, Math.Abs(region.endY - region.startY) * height);

        var rect = new Rectangle
        {
            Width = rectWidth,
            Height = rectHeight,
            Stroke = Brushes.Orange,
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(24, 255, 165, 0)),
            StrokeDashArray = new DoubleCollection { 3, 2 },
            IsHitTestVisible = false
        };

        var label = BuildMarkerLabel("Auto-ajuste");
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        Canvas.SetLeft(label, left + 4);
        Canvas.SetTop(label, top + 4);

        OverlayCanvas.Children.Add(rect);
        OverlayCanvas.Children.Add(label);
    }

    private void RedrawAllMeasurementMarkers()
    {
        var cW = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var cH = Math.Max(1.0, InteractionCanvas.ActualHeight);
        var iW = Math.Max(1.0, _viewModel.ImagePixelWidth);
        var iH = Math.Max(1.0, _viewModel.ImagePixelHeight);

        foreach (var m in _viewModel.Measurements)
        {
            if (string.IsNullOrWhiteSpace(m.CoordinatesJson))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(m.CoordinatesJson);
                var root = doc.RootElement;
                var label = $"{m.Tmax:F1} C";

                switch (m.Type)
                {
                    case MeasurementType.Spot:
                        var sx = root.GetProperty("x").GetDouble() / iW * cW;
                        var sy = root.GetProperty("y").GetDouble() / iH * cH;
                        DrawSpotMarker(m.Id, new Point(sx, sy), label);
                        break;

                    case MeasurementType.Area:
                        var ax  = root.GetProperty("x").GetDouble()  / iW * cW;
                        var ay  = root.GetProperty("y").GetDouble()  / iH * cH;
                        var arw = root.GetProperty("rw").GetDouble() / iW * cW;
                        var arh = root.GetProperty("rh").GetDouble() / iH * cH;
                        DrawAreaMarker(m.Id, new Point(ax, ay), new Point(ax + arw, ay + arh), $"Tmax {label}");
                        break;

                    case MeasurementType.Line:
                        var ly = root.GetProperty("y").GetDouble() / iH * cH;
                        DrawLineMarker(m.Id, new Point(0, ly), new Point(cW, ly), $"Tmax {label}");
                        break;

                    case MeasurementType.Circle:
                        var ccx = root.GetProperty("cx").GetDouble()     / iW * cW;
                        var ccy = root.GetProperty("cy").GetDouble()     / iH * cH;
                        var crx = root.GetProperty("radius").GetDouble() / iW * cW;
                        var cry = root.GetProperty("radius").GetDouble() / iH * cH;
                        DrawCircleMarker(m.Id, new Point(ccx - crx, ccy - cry), new Point(ccx + crx, ccy + cry), $"Tmax {label}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RedrawAllMeasurementMarkers] Skipping measurement {m.Id}: {ex.Message}");
            }
        }
    }

    private void RedrawAllIllustrations()
    {
        foreach (var elements in _illustrationElements.Values)
        {
            foreach (var element in elements)
            {
                OverlayCanvas.Children.Remove(element);
            }
        }
        _illustrationElements.Clear();

        var cW = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var cH = Math.Max(1.0, InteractionCanvas.ActualHeight);

        foreach (var item in _viewModel.Illustrations)
        {
            var x1 = item.X1 * cW;
            var y1 = item.Y1 * cH;
            var x2 = item.X2 * cW;
            var y2 = item.Y2 * cH;

            switch (item.Type)
            {
                case IllustrationType.Arrow:
                    DrawIllustrationArrow(item.Id, new Point(x1, y1), new Point(x2, y2));
                    break;
                case IllustrationType.Rectangle:
                    DrawIllustrationRectangle(item.Id, new Point(x1, y1), new Point(x2, y2));
                    break;
                case IllustrationType.Ellipse:
                    DrawIllustrationEllipse(item.Id, new Point(x1, y1), new Point(x2, y2));
                    break;
                case IllustrationType.Text:
                    DrawIllustrationText(item.Id, new Point(x1, y1), item.Text);
                    break;
            }
        }
    }

    private static ThermalIllustration CloneIllustration(ThermalIllustration source)
        => new()
        {
            Id = source.Id,
            Type = source.Type,
            X1 = source.X1,
            Y1 = source.Y1,
            X2 = source.X2,
            Y2 = source.Y2,
            Text = source.Text
        };

    private static ThermalIllustration CloneWithOffset(ThermalIllustration source, double dx, double dy)
        => new()
        {
            Id = source.Id,
            Type = source.Type,
            X1 = Math.Clamp(source.X1 + dx, 0.0, 1.0),
            Y1 = Math.Clamp(source.Y1 + dy, 0.0, 1.0),
            X2 = Math.Clamp(source.X2 + dx, 0.0, 1.0),
            Y2 = Math.Clamp(source.Y2 + dy, 0.0, 1.0),
            Text = source.Text
        };

    private void UpsertIllustrationLocal(ThermalIllustration item)
    {
        var existing = _viewModel.Illustrations.FirstOrDefault(i => i.Id == item.Id);
        if (existing is null)
        {
            return;
        }

        existing.X1 = item.X1;
        existing.Y1 = item.Y1;
        existing.X2 = item.X2;
        existing.Y2 = item.Y2;
        existing.Text = item.Text;
    }

    private Guid? FindNearestIllustrationId(Point canvasPos, double threshold)
    {
        Guid? best = null;
        var bestDist = double.MaxValue;
        var cW = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var cH = Math.Max(1.0, InteractionCanvas.ActualHeight);

        foreach (var item in _viewModel.Illustrations)
        {
            var dist = GetDistanceToIllustrationPixels(item, canvasPos, cW, cH);
            if (dist < threshold && dist < bestDist)
            {
                bestDist = dist;
                best = item.Id;
            }
        }

        return best;
    }

    private static double GetDistanceToIllustrationPixels(IIllustration item, Point canvasPos, double canvasWidth, double canvasHeight)
    {
        var x1 = item.X1 * canvasWidth;
        var y1 = item.Y1 * canvasHeight;
        var x2 = item.X2 * canvasWidth;
        var y2 = item.Y2 * canvasHeight;

        switch (item.Type)
        {
            case IllustrationType.Text:
            {
                var dx = canvasPos.X - x1;
                var dy = canvasPos.Y - y1;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }
            case IllustrationType.Arrow:
                return DistancePointToSegment(canvasPos, new Point(x1, y1), new Point(x2, y2));

            case IllustrationType.Rectangle:
            {
                var left = Math.Min(x1, x2);
                var top = Math.Min(y1, y2);
                var right = Math.Max(x1, x2);
                var bottom = Math.Max(y1, y2);

                // Para UX: arrastar no contorno, deixando interior livre para spots.
                if (canvasPos.X >= left && canvasPos.X <= right && canvasPos.Y >= top && canvasPos.Y <= bottom)
                {
                    var toLeft = canvasPos.X - left;
                    var toRight = right - canvasPos.X;
                    var toTop = canvasPos.Y - top;
                    var toBottom = bottom - canvasPos.Y;
                    return Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
                }

                var dx = Math.Max(Math.Max(left - canvasPos.X, 0.0), canvasPos.X - right);
                var dy = Math.Max(Math.Max(top - canvasPos.Y, 0.0), canvasPos.Y - bottom);
                return Math.Sqrt((dx * dx) + (dy * dy));
            }

            case IllustrationType.Ellipse:
            {
                var cx = (x1 + x2) / 2.0;
                var cy = (y1 + y2) / 2.0;
                var rx = Math.Max(1.0, Math.Abs(x2 - x1) / 2.0);
                var ry = Math.Max(1.0, Math.Abs(y2 - y1) / 2.0);
                var nx = (canvasPos.X - cx) / rx;
                var ny = (canvasPos.Y - cy) / ry;
                var d = (nx * nx) + (ny * ny);
                var radialDistance = Math.Abs(Math.Sqrt(d) - 1.0) * Math.Min(rx, ry);

                return radialDistance;
            }

            default:
            {
                var mx = (x1 + x2) / 2.0;
                var my = (y1 + y2) / 2.0;
                var dx = canvasPos.X - mx;
                var dy = canvasPos.Y - my;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }
        }
    }

    private static double DistancePointToSegment(Point p, Point a, Point b)
    {
        var vx = b.X - a.X;
        var vy = b.Y - a.Y;
        var len2 = (vx * vx) + (vy * vy);
        if (len2 < 1e-6)
        {
            var dx0 = p.X - a.X;
            var dy0 = p.Y - a.Y;
            return Math.Sqrt((dx0 * dx0) + (dy0 * dy0));
        }

        var t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
        t = Math.Clamp(t, 0.0, 1.0);

        var projX = a.X + (t * vx);
        var projY = a.Y + (t * vy);
        var dx = p.X - projX;
        var dy = p.Y - projY;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private void DrawIllustrationArrow(Guid id, Point start, Point end)
    {
        var body = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            IsHitTestVisible = false
        };

        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var len = 11.0;
        var arrowHeadLeft = new Line
        {
            X1 = end.X,
            Y1 = end.Y,
            X2 = end.X - (len * Math.Cos(angle + Math.PI / 7)),
            Y2 = end.Y - (len * Math.Sin(angle + Math.PI / 7)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            IsHitTestVisible = false
        };

        var arrowHeadRight = new Line
        {
            X1 = end.X,
            Y1 = end.Y,
            X2 = end.X - (len * Math.Cos(angle - Math.PI / 7)),
            Y2 = end.Y - (len * Math.Sin(angle - Math.PI / 7)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            IsHitTestVisible = false
        };

        OverlayCanvas.Children.Add(body);
        OverlayCanvas.Children.Add(arrowHeadLeft);
        OverlayCanvas.Children.Add(arrowHeadRight);
        _illustrationElements[id] = [body, arrowHeadLeft, arrowHeadRight];
    }

    private void DrawIllustrationRectangle(Guid id, Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Max(2, Math.Abs(end.X - start.X));
        var height = Math.Max(2, Math.Abs(end.Y - start.Y));
        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            StrokeDashArray = new DoubleCollection { 3, 2 },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        OverlayCanvas.Children.Add(rect);
        _illustrationElements[id] = [rect];
    }

    private void DrawIllustrationEllipse(Guid id, Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Max(2, Math.Abs(end.X - start.X));
        var height = Math.Max(2, Math.Abs(end.Y - start.Y));
        var ellipse = new Ellipse
        {
            Width = width,
            Height = height,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            StrokeDashArray = new DoubleCollection { 3, 2 },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(ellipse, left);
        Canvas.SetTop(ellipse, top);
        OverlayCanvas.Children.Add(ellipse);
        _illustrationElements[id] = [ellipse];
    }

    private void DrawIllustrationText(Guid id, Point at, string text)
    {
        var label = BuildMarkerLabel(string.IsNullOrWhiteSpace(text) ? "Texto" : text);
        Canvas.SetLeft(label, at.X + 2);
        Canvas.SetTop(label, at.Y + 2);
        OverlayCanvas.Children.Add(label);
        _illustrationElements[id] = [label];
    }

    private void DrawSpotMarker(Guid measurementId, Point p, string label)
    {
        var halo = new Ellipse
        {
            Width = 8,
            Height = 8,
            Stroke = Brushes.White,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.5
        };

        var core = new Ellipse
        {
            Width = 4,
            Height = 4,
            Stroke = Brushes.Red,
            Fill = Brushes.Red,
            StrokeThickness = 1
        };

        var text = BuildMarkerLabel(label);
        Canvas.SetLeft(halo, p.X - 4);
        Canvas.SetTop(halo, p.Y - 4);
        Canvas.SetLeft(core, p.X - 2);
        Canvas.SetTop(core, p.Y - 2);
        Canvas.SetLeft(text, p.X + 6);
        Canvas.SetTop(text, p.Y - 12);

        OverlayCanvas.Children.Add(halo);
        OverlayCanvas.Children.Add(core);
        OverlayCanvas.Children.Add(text);

        _markerElements[measurementId] = [halo, core, text];
    }

    private void DrawAreaMarker(Guid measurementId, Point start, Point end, string label)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Max(2, Math.Abs(end.X - start.X));
        var height = Math.Max(2, Math.Abs(end.Y - start.Y));

        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0))
        };

        var outline = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
            StrokeDashArray = new DoubleCollection { 2, 2 }
        };

        var text = BuildMarkerLabel(label);
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        Canvas.SetLeft(outline, left);
        Canvas.SetTop(outline, top);
        Canvas.SetLeft(text, left + width + 6);
        Canvas.SetTop(text, top - 2);

        OverlayCanvas.Children.Add(rect);
        OverlayCanvas.Children.Add(outline);
        OverlayCanvas.Children.Add(text);

        _markerElements[measurementId] = [rect, outline, text];
    }

    private void DrawLineMarker(Guid measurementId, Point start, Point end, string label)
    {
        var baseLine = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = Brushes.White,
            StrokeThickness = 4
        };

        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = Brushes.Red,
            StrokeThickness = 2
        };

        var text = BuildMarkerLabel(label);
        Canvas.SetLeft(text, end.X + 6);
        Canvas.SetTop(text, end.Y - 12);

        OverlayCanvas.Children.Add(baseLine);
        OverlayCanvas.Children.Add(line);
        OverlayCanvas.Children.Add(text);

        _markerElements[measurementId] = [baseLine, line, text];
    }

    private void DrawCircleMarker(Guid measurementId, Point start, Point end, string label)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Max(2, Math.Abs(end.X - start.X));
        var height = Math.Max(2, Math.Abs(end.Y - start.Y));

        var ellipse = new Ellipse
        {
            Width = width,
            Height = height,
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0))
        };

        var outline = new Ellipse
        {
            Width = width,
            Height = height,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
            StrokeDashArray = new DoubleCollection { 2, 2 }
        };

        var text = BuildMarkerLabel(label);
        Canvas.SetLeft(ellipse, left);
        Canvas.SetTop(ellipse, top);
        Canvas.SetLeft(outline, left);
        Canvas.SetTop(outline, top);
        Canvas.SetLeft(text, left + width + 6);
        Canvas.SetTop(text, top - 2);

        OverlayCanvas.Children.Add(ellipse);
        OverlayCanvas.Children.Add(outline);
        OverlayCanvas.Children.Add(text);

        _markerElements[measurementId] = [ellipse, outline, text];
    }

    private void RemoveMarkerByMeasurement(Guid measurementId)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_markerElements.TryGetValue(measurementId, out var elements))
            {
                return;
            }

            foreach (var element in elements)
            {
                OverlayCanvas.Children.Remove(element);
            }

            _markerElements.Remove(measurementId);
        });
    }

    private void UpdateInteractionCursor()
    {
        InteractionCanvas.Cursor = _viewModel.ActiveTool switch
        {
            AnalysisTool.Hand => Cursors.Hand,
            AnalysisTool.IllustrationArrow => Cursors.Pen,
            AnalysisTool.IllustrationRectangle => Cursors.Pen,
            AnalysisTool.IllustrationEllipse => Cursors.Pen,
            AnalysisTool.IllustrationText => Cursors.IBeam,
            _ => Cursors.Cross
        };

        // Feedback de ferramenta ativa no statusbar
        var toolMessage = _viewModel.ActiveTool switch
        {
            AnalysisTool.Hand => "Mão: clique e arraste para mover a imagem. Clique em uma ilustração para arrastá-la.",
            AnalysisTool.Spot => "Spot: clique para criar um ponto de medição.",
            AnalysisTool.Area => "Retângulo (ilustração): clique e arraste para desenhar sobre o termograma.",
            AnalysisTool.Circle => "Círculo (ilustração): clique e arraste para destacar elementos.",
            AnalysisTool.Line => "Seta (ilustração): clique e arraste para apontar elementos.",
            AnalysisTool.IllustrationArrow => "Seta: clique e arraste para desenhar uma seta anotativa.",
            AnalysisTool.IllustrationRectangle => "Retângulo: clique e arraste para desenhar um retângulo anotativo.",
            AnalysisTool.IllustrationEllipse => "Elipse: clique e arraste para desenhar uma elipse anotativa.",
            AnalysisTool.IllustrationText => "Texto: clique na imagem e digite direto no termograma.",
            _ => "Pronto."
        };

        _viewModel.StatusMessage = toolMessage;
    }

    private static Border BuildMarkerLabel(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 10, 10, 18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 102, 0)),
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2.5, 1, 2.5, 1),
            Child = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(225, 245, 245, 250)),
                FontSize = 8,
                Text = text,
                FontFamily = new FontFamily("Segoe UI")
            }
        };
    }

    private Guid? FindNearestSpotMeasurementId(Point canvasPos, double threshold)
    {
        Guid? bestId = null;
        var bestDist = double.MaxValue;

        foreach (var (id, elements) in _markerElements)
        {
            // The spot halo is the first element; its Canvas.Left/Top is center-4
            var halo = elements.FirstOrDefault();
            if (halo is null) continue;

            var left = Canvas.GetLeft(halo);
            var top = Canvas.GetTop(halo);
            if (double.IsNaN(left) || double.IsNaN(top)) continue;

            var cx = left + 4;
            var cy = top + 4;
            var dist = Math.Sqrt((canvasPos.X - cx) * (canvasPos.X - cx) + (canvasPos.Y - cy) * (canvasPos.Y - cy));
            if (dist < threshold && dist < bestDist)
            {
                bestDist = dist;
                bestId = id;
            }
        }

        return bestId;
    }

    private async Task ShowSpotContextMenuAsync(Guid measurementId, Point canvasPos)
    {
        var measurement = _viewModel.Measurements.FirstOrDefault(m => m.Id == measurementId);
        if (measurement is null) return;

        var menu = new ContextMenu { IsOpen = false };

        var defineItem = new MenuItem
        {
            Header = measurement.MaxAdmissibleC.HasValue
                ? $"Editar Tmax admissível ({measurement.MaxAdmissibleC.Value:F1} °C)..."
                : "Definir Tmax admissível..."
        };
        defineItem.Click += async (_, _) =>
        {
            var value = PromptForTmax(measurement.MaxAdmissibleC);
            if (value.HasValue)
                await _viewModel.SetMeasurementMaxAdmissibleAsync(measurementId, value.Value);
        };

        var clearItem = new MenuItem { Header = "Limpar Tmax admissível" };
        clearItem.IsEnabled = measurement.MaxAdmissibleC.HasValue;
        clearItem.Click += async (_, _) =>
            await _viewModel.SetMeasurementMaxAdmissibleAsync(measurementId, double.NaN);

        var separator = new Separator();

        var removeItem = new MenuItem { Header = "Remover spot" };
        removeItem.Click += async (_, _) =>
        {
            // TODO: Fix after illustration engine refactor
            // await _viewModel.RemoveMeasurementByIdAsync(measurementId);
        };

        menu.Items.Add(defineItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(separator);
        menu.Items.Add(removeItem);

        menu.PlacementTarget = InteractionCanvas;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;

        await Task.CompletedTask;
    }

    private async Task ShowIllustrationContextMenuAsync(Guid illustrationId, Point canvasPos)
    {
        var illustration = _viewModel.Illustrations.FirstOrDefault(i => i.Id == illustrationId);
        if (illustration is null)
        {
            return;
        }

        var menu = new ContextMenu { IsOpen = false };

        if (illustration.Type == IllustrationType.Text)
        {
            var editText = new MenuItem { Header = "Editar texto na imagem" };
            editText.Click += (_, _) =>
            {
                var cW = Math.Max(1.0, InteractionCanvas.ActualWidth);
                var cH = Math.Max(1.0, InteractionCanvas.ActualHeight);
                var at = new Point(illustration.X1 * cW, illustration.Y1 * cH);
                StartInlineTextEditor(at, illustration as ThermalIllustration);
            };
            menu.Items.Add(editText);
            menu.Items.Add(new Separator());
        }

        var remove = new MenuItem { Header = "Remover ilustração" };
        remove.Click += async (_, _) =>
        {
            await _viewModel.RemoveIllustrationByIdAsync(illustrationId);
            _selectedIllustrationId = null;
            RedrawAllIllustrations();
        };
        menu.Items.Add(remove);

        menu.PlacementTarget = InteractionCanvas;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        await Task.CompletedTask;
    }

    private void StartInlineTextEditor(Point canvasPoint, ThermalIllustration? existing)
    {
        if (_inlineTextEditor is not null)
        {
            _ = CommitInlineTextEditorAsync();
        }

        _inlineTextIllustrationId = existing?.Id;

        var editor = new TextBox
        {
            Text = existing?.Text ?? string.Empty,
            Width = 220,
            MinWidth = 140,
            MaxWidth = 340,
            Background = new SolidColorBrush(Color.FromArgb(235, 20, 20, 34)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            MaxLength = 200,
            AcceptsReturn = false
        };

        var cW = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var cH = Math.Max(1.0, InteractionCanvas.ActualHeight);
        var left = Math.Clamp(canvasPoint.X, 0, Math.Max(0, cW - editor.Width));
        var top = Math.Clamp(canvasPoint.Y, 0, Math.Max(0, cH - 28));

        Canvas.SetLeft(editor, left);
        Canvas.SetTop(editor, top);
        Panel.SetZIndex(editor, 5000);
        editor.IsTabStop = true;
        editor.Visibility = Visibility.Visible;

        editor.KeyDown += InlineTextEditor_KeyDown;
        editor.LostKeyboardFocus += InlineTextEditor_LostKeyboardFocus;

        InteractionCanvas.Children.Add(editor);
        _inlineTextEditor = editor;

        editor.Loaded += (_, _) =>
        {
            editor.BringIntoView();
            editor.Focus();
            editor.SelectAll();
            Keyboard.Focus(editor);
        };

        Dispatcher.BeginInvoke(() =>
        {
            editor.BringIntoView();
            editor.Focus();
            editor.SelectAll();
            Keyboard.Focus(editor);
        }, System.Windows.Threading.DispatcherPriority.Input);

        _viewModel.StatusMessage = "Digite o texto e pressione Enter para salvar (Esc para cancelar).";
    }

    private async void InlineTextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_inlineTextEditor is null)
        {
            return;
        }

        await CommitInlineTextEditorAsync();
    }

    private async void InlineTextEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitInlineTextEditorAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineTextEditor();
        }
    }

    private async Task CommitInlineTextEditorAsync()
    {
        if (_inlineTextEditor is null)
        {
            return;
        }

        var editor = _inlineTextEditor;
        var text = editor.Text.Trim();
        var existingId = _inlineTextIllustrationId;
        var left = Canvas.GetLeft(editor);
        var top = Canvas.GetTop(editor);

        CancelInlineTextEditor(clearStatus: false);

        if (string.IsNullOrWhiteSpace(text))
        {
            _viewModel.StatusMessage = "Texto vazio descartado.";
            return;
        }

        var cW = Math.Max(1.0, InteractionCanvas.ActualWidth);
        var cH = Math.Max(1.0, InteractionCanvas.ActualHeight);
        var nx = Math.Clamp(left / cW, 0.0, 1.0);
        var ny = Math.Clamp(top / cH, 0.0, 1.0);

        if (existingId.HasValue)
        {
            var existing = _viewModel.Illustrations.FirstOrDefault(i => i.Id == existingId.Value) as ThermalIllustration;
            if (existing is not null)
            {
                existing.Text = text;
                existing.X1 = nx;
                existing.Y1 = ny;
                existing.X2 = nx;
                existing.Y2 = ny;
                await _viewModel.UpdateIllustrationAsync(existing.Id, existing);
            }
        }
        else
        {
            var item = new ThermalIllustration
            {
                Type = IllustrationType.Text,
                X1 = nx,
                Y1 = ny,
                X2 = nx,
                Y2 = ny,
                Text = text
            };
            await _viewModel.AddIllustrationAsync(item);
        }

        RedrawAllIllustrations();
        _viewModel.StatusMessage = "Texto salvo na imagem.";
    }

    private void CancelInlineTextEditor(bool clearStatus = true)
    {
        if (_inlineTextEditor is not null)
        {
            _inlineTextEditor.KeyDown -= InlineTextEditor_KeyDown;
            _inlineTextEditor.LostKeyboardFocus -= InlineTextEditor_LostKeyboardFocus;
            InteractionCanvas.Children.Remove(_inlineTextEditor);
            _inlineTextEditor = null;
        }

        _inlineTextIllustrationId = null;

        if (clearStatus)
        {
            UpdateInteractionCursor();
        }
    }

    private async Task<string?> CaptureCurrentViewForReportAsync()
    {
        if (_viewModel.DisplayImage is not BitmapSource displayBitmap)
        {
            return null;
        }

        if (_inlineTextEditor is not null)
        {
            await CommitInlineTextEditorAsync();
        }

        try
        {
            var tempDir = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "ThermixStudio", "RenderedReportImages");
            Directory.CreateDirectory(tempDir);

            var basePath = global::System.IO.Path.Combine(tempDir, $"current_{_viewModel.SelectedThermogram?.Id ?? Guid.NewGuid():N}_{Guid.NewGuid():N}.png");
            await using (var stream = File.Create(basePath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(displayBitmap));
                encoder.Save(stream);
            }

            var illustrations = _viewModel.Illustrations
                .OfType<ThermalIllustration>()
                .Select(CloneIllustration)
                .ToList();

            var annotated = ThermalImageAnnotator.AnnotateWithSpots(basePath, _viewModel.Measurements.ToList(), illustrations);
            return string.IsNullOrWhiteSpace(annotated) ? basePath : annotated;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] Falha ao capturar snapshot para relatório: {ex.Message}");
            return null;
        }
    }

    private double? PromptForTmax(double? currentValue)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Tmax Admissível",
            Width = 320,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(26, 26, 40))
        };

        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        panel.Children.Add(new TextBlock
        {
            Text = "Temperatura máxima admissível (°C):",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12
        });

        var input = new TextBox
        {
            Text = currentValue.HasValue ? currentValue.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 54)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 82)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 12),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12
        };
        panel.Children.Add(input);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button
        {
            Content = "OK",
            Width = 70,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6)
        };
        var cancelBtn = new Button
        {
            Content = "Cancelar",
            Width = 80,
            Background = new SolidColorBrush(Color.FromRgb(42, 42, 62)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 82)),
            Padding = new Thickness(0, 6, 0, 6)
        };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        double? result = null;
        okBtn.Click += (_, _) =>
        {
            var text = input.Text.Trim().Replace(',', '.');
            if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var val) && val > 0)
            {
                result = val;
                dialog.DialogResult = true;
            }
            else
            {
                MessageBox.Show(dialog, "Informe um valor numérico positivo.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (e.Key == Key.Escape) cancelBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };

        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
        return result;
    }

    private string? PromptForIllustrationText(string? current = null)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Texto da Ilustracao",
            Width = 360,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(26, 26, 40))
        };

        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = "Conteudo da anotacao:",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12
        });

        var input = new TextBox
        {
            Text = current ?? string.Empty,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 54)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 82)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 12),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            MaxLength = 140
        };
        panel.Children.Add(input);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button
        {
            Content = "OK",
            Width = 70,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6)
        };
        var cancelBtn = new Button
        {
            Content = "Cancelar",
            Width = 80,
            Background = new SolidColorBrush(Color.FromRgb(42, 42, 62)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 82)),
            Padding = new Thickness(0, 6, 0, 6)
        };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        string? result = null;
        okBtn.Click += (_, _) =>
        {
            result = input.Text?.Trim();
            dialog.DialogResult = true;
        };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (e.Key == Key.Escape) cancelBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };

        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
        return result;
    }
}