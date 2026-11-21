using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ResMonNext.ViewModels;

namespace ResMonNext.Controls;

/// <summary>
/// Graphe temps réel dessiné directement (aucune bibliothèque de graphiques) :
/// une géométrie par série, redessinée seulement quand de nouvelles valeurs arrivent.
/// </summary>
public sealed class LiveGraph : Control
{
    public static readonly StyledProperty<GraphModel?> ModelProperty =
        AvaloniaProperty.Register<LiveGraph, GraphModel?>(nameof(Model));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<LiveGraph, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<LiveGraph, IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<bool> ShowScaleProperty =
        AvaloniaProperty.Register<LiveGraph, bool>(nameof(ShowScale), true);

    private static readonly Typeface LabelFace = new("Segoe UI");
    private bool _attached;

    static LiveGraph()
    {
        AffectsRender<LiveGraph>(ModelProperty, GridBrushProperty, LabelBrushProperty, ShowScaleProperty);
    }

    public GraphModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public bool ShowScale
    {
        get => GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty && _attached)
        {
            if (change.OldValue is GraphModel oldModel) oldModel.Changed -= OnModelChanged;
            if (change.NewValue is GraphModel newModel) newModel.Changed += OnModelChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (Model is { } model) model.Changed += OnModelChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (Model is { } model) model.Changed -= OnModelChanged;
    }

    private void OnModelChanged() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 4 || bounds.Height < 4) return;

        using var clip = context.PushClip(new RoundedRect(bounds, 6));
        var gridPen = new ImmutablePen((GridBrush ?? new ImmutableSolidColorBrush(Color.FromArgb(40, 128, 128, 128))).ToImmutable(), 1);
        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(bounds.Height * i / 4) + 0.5;
            context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
        }
        for (int i = 1; i < 6; i++)
        {
            double x = Math.Round(bounds.Width * i / 6) + 0.5;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        if (Model is not { } model) return;
        double scale = model.Scale();
        if (scale <= 0) scale = 1;

        foreach (var series in model.Series)
        {
            if (series.Count < 2) continue;
            double step = bounds.Width / (series.Capacity - 1);
            double x0 = bounds.Width - (series.Count - 1) * step;
            Point At(int i) => new(x0 + i * step, bounds.Height - Math.Clamp(series[i] / scale, 0, 1) * (bounds.Height - 1));

            if (series.Fill)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(x0, bounds.Height), true);
                    for (int i = 0; i < series.Count; i++) ctx.LineTo(At(i));
                    ctx.LineTo(new Point(bounds.Width, bounds.Height));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(series.FillBrush, null, area);
            }

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(At(0), false);
                for (int i = 1; i < series.Count; i++) ctx.LineTo(At(i));
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, series.Stroke, line);
        }

        if (ShowScale)
        {
            var text = new FormattedText(model.ScaleFormatter(scale), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelFace, 10, LabelBrush ?? Brushes.Gray);
            context.DrawText(text, new Point(6, 3));
        }
    }
}

public sealed record MemorySegment(ulong Value, Color Color);

/// <summary>Barre empilée de la mémoire physique (réservée, utilisée, modifiée, en attente, libre).</summary>
public sealed class MemoryBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<MemorySegment>?> SegmentsProperty =
        AvaloniaProperty.Register<MemoryBar, IReadOnlyList<MemorySegment>?>(nameof(Segments));

    static MemoryBar()
    {
        AffectsRender<MemoryBar>(SegmentsProperty);
    }

    public IReadOnlyList<MemorySegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Segments is not { Count: > 0 } segments || bounds.Width < 4) return;
        double total = segments.Sum(s => (double)s.Value);
        if (total <= 0) return;

        using var clip = context.PushClip(new RoundedRect(bounds, 6));
        double x = 0;
        foreach (var segment in segments)
        {
            double width = bounds.Width * segment.Value / total;
            if (width <= 0) continue;
            context.DrawRectangle(new ImmutableSolidColorBrush(segment.Color), null, new Rect(x, 0, width + 0.5, bounds.Height));
            x += width;
        }
    }
}
