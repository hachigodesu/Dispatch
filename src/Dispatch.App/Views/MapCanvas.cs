using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dispatch.App.ViewModels;
using Dispatch.Core.Maps;

namespace Dispatch.App.Views;

/// <summary>
/// Draws a <see cref="MapScene"/> with pan (drag) and zoom (wheel). The roads (hundreds of thousands for a big map
/// combo) are rendered to a bitmap on a background thread; while that runs, the last bitmap is moved and scaled to
/// follow the mouse, so panning and zooming stay smooth. City labels are drawn on top, so they stay sharp.
/// </summary>
public sealed class MapCanvas : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(MapScene), typeof(MapCanvas),
        new PropertyMetadata(null, (d, e) => ((MapCanvas)d).OnSceneChanged((MapScene?)e.OldValue, (MapScene?)e.NewValue)));

    private readonly DispatcherTimer _renderTimer;
    private readonly Dictionary<(string, bool, Color), FormattedText> _labels = [];
    private View _view;
    private bool _hasView;
    private BitmapSource? _bitmap;
    private View _bitmapView;
    private CancellationTokenSource? _renderCancel;
    private int _renderVersion;
    private Point? _dragFrom;
    private View _dragView;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) => StartRender(), Dispatcher);
        SizeChanged += (_, _) =>
        {
            if (!_hasView && Scene is not null)
            {
                FitTo(Scene.Focus ?? Scene.Extent);
            }

            ScheduleRender();
        };
    }

    public MapScene? Scene
    {
        get => (MapScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>True once a bitmap for the current scene has been drawn (screenshots wait for it).</summary>
    public bool HasRendered { get; private set; }

    /// <summary>The visible area: world centre and pixels (DIPs) per metre.</summary>
    private readonly record struct View(double CenterX, double CenterZ, double Scale, double Width, double Height)
    {
        public Point ToScreen(MapPoint p) => new(((p.X - CenterX) * Scale) + (Width / 2), ((p.Z - CenterZ) * Scale) + (Height / 2));

        public (double X, double Z) ToWorld(Point p) => (((p.X - (Width / 2)) / Scale) + CenterX, ((p.Y - (Height / 2)) / Scale) + CenterZ);
    }

    /// <summary>Shows this area (with a margin), or does nothing for null.</summary>
    public void FitTo(MapRect? area)
    {
        if (area is not { } r || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        var padded = r.Inflate(Math.Max(1500, Math.Max(r.Width, r.Height) * 0.08f));
        double scale = Math.Min(ActualWidth / padded.Width, ActualHeight / padded.Height);
        SetView(new View((padded.MinX + padded.MaxX) / 2.0, (padded.MinZ + padded.MaxZ) / 2.0, scale, ActualWidth, ActualHeight));
    }

    public void ZoomToChanges() => FitTo(Scene?.Focus ?? Scene?.Extent);

    public void ZoomToAll() => FitTo(Scene?.Extent);

    private void OnSceneChanged(MapScene? old, MapScene? scene)
    {
        HasRendered = false;
        _labels.Clear();
        if (scene is not null && old is not null && ReferenceEquals(scene.Source, old.Source) && _hasView)
        {
            ScheduleRender(); // same map, other layers shown: keep the view and the old bitmap until the new one is ready
            InvalidateVisual();
            return;
        }

        _bitmap = null;
        _hasView = false;
        if (scene is not null)
        {
            FitTo(scene.Focus ?? scene.Extent);
        }

        InvalidateVisual();
    }

    private void SetView(View view)
    {
        _view = view with { Scale = Math.Clamp(view.Scale, 1e-5, 2.0) };
        _hasView = true;
        InvalidateVisual();
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    // ---- Mouse -----------------------------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!_hasView)
        {
            return;
        }

        var at = e.GetPosition(this);
        var (wx, wz) = _view.ToWorld(at);
        double scale = Math.Clamp(_view.Scale * Math.Pow(1.25, e.Delta / 120.0), 1e-5, 2.0);
        // Keep the point under the mouse where it is.
        SetView(_view with { Scale = scale, CenterX = wx - ((at.X - (_view.Width / 2)) / scale), CenterZ = wz - ((at.Y - (_view.Height / 2)) / scale) });
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2 && _hasView)
        {
            var at = e.GetPosition(this);
            var (wx, wz) = _view.ToWorld(at);
            SetView(_view with { CenterX = wx, CenterZ = wz, Scale = _view.Scale * 2 });
        }
        else
        {
            _dragFrom = e.GetPosition(this);
            _dragView = _view;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is { } from && IsMouseCaptured)
        {
            var now = e.GetPosition(this);
            SetView(_dragView with { CenterX = _dragView.CenterX - ((now.X - from.X) / _dragView.Scale), CenterZ = _dragView.CenterZ - ((now.Y - from.Y) / _dragView.Scale) });
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
        Cursor = null;
    }

    // ---- Drawing ---------------------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRectangle(new SolidColorBrush(MapScene.Paper), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (!_hasView || Scene is null)
        {
            return;
        }

        var view = _view with { Width = ActualWidth, Height = ActualHeight };
        if (_bitmap is not null)
        {
            // Where the last bitmap's corners are now.
            double k = view.Scale / _bitmapView.Scale;
            double left = (-_bitmapView.Width / 2 * k) + ((_bitmapView.CenterX - view.CenterX) * view.Scale) + (view.Width / 2);
            double top = (-_bitmapView.Height / 2 * k) + ((_bitmapView.CenterZ - view.CenterZ) * view.Scale) + (view.Height / 2);
            dc.DrawImage(_bitmap, new Rect(left, top, _bitmapView.Width * k, _bitmapView.Height * k));
        }

        DrawLabels(dc, view);
    }

    /// <summary>City labels; strong ones first. A label that would overlap one already placed is left out.</summary>
    private void DrawLabels(DrawingContext dc, View view)
    {
        var placed = new List<Rect>();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var dot = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, MapScene.Paper.R, MapScene.Paper.G, MapScene.Paper.B)), 3) { LineJoin = PenLineJoin.Round };
        foreach (var label in Scene!.Labels.OrderByDescending(c => c.Strong))
        {
            var p = view.ToScreen(label.Position);
            if (p.X < -50 || p.Y < -20 || p.X > view.Width + 50 || p.Y > view.Height + 20)
            {
                continue;
            }

            var key = (label.Name, label.Strong, label.Color);
            if (!_labels.TryGetValue(key, out var text))
            {
                text = new FormattedText(label.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                         new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, label.Strong ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal),
                                         label.Strong ? 13 : 12, new SolidColorBrush(label.Color), dpi);
                _labels[key] = text;
            }

            var box = new Rect(p.X + 4, p.Y - (text.Height / 2), text.Width, text.Height);
            if (placed.Any(r => r.IntersectsWith(box)))
            {
                continue;
            }

            placed.Add(box);
            dc.DrawEllipse(dot, null, p, 2.2, 2.2);
            dc.DrawGeometry(null, halo, text.BuildGeometry(box.TopLeft)); // keeps labels readable on top of roads
            dc.DrawText(text, box.TopLeft);
        }
    }

    private void StartRender()
    {
        _renderTimer.Stop();
        if (!_hasView || Scene is not { } scene || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        _renderCancel?.Cancel();
        var cancel = _renderCancel = new CancellationTokenSource();
        var view = _view with { Width = ActualWidth, Height = ActualHeight };
        var dpi = VisualTreeHelper.GetDpi(this);
        int version = ++_renderVersion;
        var thread = new Thread(() =>
        {
            BitmapSource? bitmap = null;
            try
            {
                bitmap = MapRenderer.Render(scene, view.CenterX, view.CenterZ, view.Scale, view.Width, view.Height, dpi, cancel.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }

            if (bitmap is not null)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (version == _renderVersion && ReferenceEquals(scene, Scene))
                    {
                        _bitmap = bitmap;
                        _bitmapView = view;
                        HasRendered = true;
                        InvalidateVisual();
                    }
                });
            }
        })
        { IsBackground = true, Name = "Map render" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}

/// <summary>Renders a scene's roads for one view into a frozen bitmap. Runs on any STA thread.</summary>
internal static class MapRenderer
{
    public static BitmapSource Render(MapScene scene, double centerX, double centerZ, double scale, double width, double height,
                                      DpiScale dpi, CancellationToken cancel)
    {
        var world = new MapRect((float)(centerX - (width / 2 / scale)), (float)(centerZ - (height / 2 / scale)),
                                (float)(centerX + (width / 2 / scale)), (float)(centerZ + (height / 2 / scale)));
        Point S(MapPoint p) => new(((p.X - centerX) * scale) + (width / 2), ((p.Z - centerZ) * scale) + (height / 2));

        // Wider lines when zoomed in far enough to see single roads.
        double zoomed = Math.Clamp((scale - 0.02) / 0.2, 0, 1);
        var hiddenPen = Pen(MapScene.Hidden, 1.5, dashed: true);
        var hiddenFill = new SolidColorBrush(Color.FromArgb(0x26, MapScene.Hidden.R, MapScene.Hidden.G, MapScene.Hidden.B));
        hiddenFill.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var cell in scene.HiddenCells.Where(world.Intersects))
            {
                dc.DrawRectangle(hiddenFill, hiddenPen, new Rect(S(new MapPoint(cell.MinX, cell.MinZ)), S(new MapPoint(cell.MaxX, cell.MaxZ))));
            }

            foreach (var layer in scene.Layers)
            {
                var pen = Pen(layer.Color, layer.Width + (zoomed * layer.ZoomWidth), layer.Dashed);
                dc.DrawGeometry(null, pen, Geometry(layer.Roads, layer.Prefabs, world, scale, S, layer.AlwaysShow, cancel));
            }
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
                                            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// One geometry for many roads. Curves get more points the longer they are on screen; zoomed out, roads that
    /// share a pixel (or pixel pair) are drawn once, except changes, which must always show.
    /// </summary>
    private static StreamGeometry Geometry(IReadOnlyList<MapRoad> roads, IReadOnlyList<MapPrefab> prefabs, MapRect world, double scale,
                                           Func<MapPoint, Point> s, bool keepTiny, CancellationToken cancel)
    {
        var g = new StreamGeometry();
        var seen = new HashSet<(int, int, int, int)>();
        using (var ctx = g.Open())
        {
            for (int i = 0; i < roads.Count; i++)
            {
                if ((i & 4095) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                }

                var r = roads[i];
                if (!r.Bounds.Intersects(world))
                {
                    continue;
                }

                double px = r.Length * scale;
                var a = s(r.A);
                if (px < 14)
                {
                    var b = s(r.B);
                    var key = px < 0.7 ? ((int)a.X, (int)a.Y, int.MinValue, 0) : ((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);
                    if (px < 3 && !keepTiny && !seen.Add(key))
                    {
                        continue;
                    }

                    ctx.BeginFigure(a, false, false);
                    ctx.LineTo(px < 0.7 ? new Point(b.X + 0.7, b.Y) : b, true, false);
                    continue;
                }

                int steps = (int)Math.Clamp(px / 10, 2, 24);
                ctx.BeginFigure(a, false, false);
                for (int k = 1; k <= steps; k++)
                {
                    ctx.LineTo(s(r.At((float)k / steps)), true, true);
                }
            }

            // Prefabs (junctions) are drawn as spokes from their centre to their nodes: a stand-in for their real shape.
            var near = world.Inflate(200);
            foreach (var p in prefabs)
            {
                if (p.Nodes.Count < 2)
                {
                    continue;
                }

                var centre = p.Centre;
                if (!near.Intersects(new MapRect(centre.X, centre.Z, centre.X, centre.Z)))
                {
                    continue;
                }

                var sc = s(centre);
                foreach (var n in p.Nodes)
                {
                    var sn = s(n);
                    if (Math.Abs(sn.X - sc.X) + Math.Abs(sn.Y - sc.Y) < 1.5 && !keepTiny)
                    {
                        continue;
                    }

                    ctx.BeginFigure(sc, false, false);
                    ctx.LineTo(sn, true, false);
                }
            }
        }

        g.Freeze();
        return g;
    }

    private static Pen Pen(Color color, double thickness, bool dashed = false)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            DashStyle = dashed ? new DashStyle([2.5, 2], 0) : DashStyles.Solid,
        };
        pen.Freeze();
        return pen;
    }
}
