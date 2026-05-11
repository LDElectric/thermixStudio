using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        
        // Load application icon with DPI awareness
        try
        {
            var iconPath = global::System.IO.Path.Combine(AppContext.BaseDirectory, "thermix_studio.ico");
            if (File.Exists(iconPath))
            {
                using var stream = File.OpenRead(iconPath);
                var decoder = new IconBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                var bestFrame = decoder.Frames
                    .Where(f => f.PixelWidth > 0 && f.PixelHeight > 0 && f.PixelWidth == f.PixelHeight)
                    .OrderByDescending(f => f.PixelWidth)
                    .FirstOrDefault()
                    ?? decoder.Frames.FirstOrDefault();

                if (bestFrame is not null)
                {
                    bestFrame.Freeze();
                    Icon = bestFrame;
                }

                Debug.WriteLine("[MainWindow] Application icon loaded successfully");
            }
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"[MainWindow] Icon load failed: {ex.Message}");
        }
        
        Loaded += async (_, _) =>
        {
            await viewModel.LoadDataCommand.ExecuteAsync(null);
            FitImageToViewport();
        };

        SizeChanged += (_, _) => FitImageToViewport();
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _viewModel.MeasurementRemoved += RemoveMarkerByMeasurement;
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
                MessageBox.Show(this,
                    "Manual de instruções não encontrado. Reinstale o aplicativo para restaurar os arquivos de ajuda.",
                    "Thermix Studio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
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

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
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
                ClearOverlay();
                _isDrawing = false;
                _previewShape = null;

                // Wait for WPF to finish rendering the new image before fitting
                Dispatcher.BeginInvoke(() =>
                {
                    FitImageToViewport();
                    RedrawPersistentOverlays();
                }, System.Windows.Threading.DispatcherPriority.Render);
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

    private void InteractionCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        if (Mouse.Captured == InteractionCanvas)
        {
            Mouse.Capture(null);
        }
    }

    private void InteractionCanvas_MouseMove(object sender, MouseEventArgs e)
    {
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
        }
    }

    private async void InteractionCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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
            var measurement = await _viewModel.AddAreaAtNormalizedAsync(sx, sy, ex, ey);
            if (measurement is not null)
            {
                DrawAreaMarker(measurement.Id, _drawStart, end, $"Tmax {measurement.Tmax:F1} C");
            }
        }
        else if (_viewModel.ActiveTool == AnalysisTool.Circle)
        {
            var measurement = await _viewModel.AddCircleAtNormalizedAsync(sx, sy, ex, ey);
            if (measurement is not null)
            {
                DrawCircleMarker(measurement.Id, _drawStart, end, $"Tmax {measurement.Tmax:F1} C");
            }
        }
        else if (_viewModel.ActiveTool == AnalysisTool.Line)
        {
            var measurement = await _viewModel.AddLineAtNormalizedAsync(sy, ey);
            if (measurement is not null)
            {
                DrawLineMarker(measurement.Id, _drawStart, end, $"Tmax {measurement.Tmax:F1} C");
            }
        }
        else if (_viewModel.ActiveTool == AnalysisTool.AutoAdjustRegion)
        {
            await _viewModel.SetAutoAdjustRegionNormalizedAsync(sx, sy, ex, ey);
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

        Debug.WriteLine($"[FitImageToViewport] Image: {imageWidth}x{imageHeight}, Viewport: {viewportWidth}x{viewportHeight}, Scale: {fitScale}");

        ImageScaleTransform.ScaleX = fitScale;
        ImageScaleTransform.ScaleY = fitScale;

        ImageScrollViewer.ScrollToHorizontalOffset(0);
        ImageScrollViewer.ScrollToVerticalOffset(0);
    }

    private void ClearOverlay()
    {
        OverlayCanvas.Children.Clear();
        _markerElements.Clear();
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
        InteractionCanvas.Cursor = _viewModel.ActiveTool == AnalysisTool.Hand
            ? Cursors.Hand
            : Cursors.Cross;
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
}