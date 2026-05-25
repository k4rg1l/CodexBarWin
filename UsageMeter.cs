using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;

namespace CodexBarWin;

public sealed class UsageMeter : Grid
{
    public static readonly DependencyProperty UsedPercentProperty = DependencyProperty.Register(
        nameof(UsedPercent),
        typeof(double),
        typeof(UsageMeter),
        new PropertyMetadata(0d, OnMeterPropertyChanged));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(Brush),
        typeof(UsageMeter),
        new PropertyMetadata(null, OnMeterPropertyChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(UsageMeter),
        new PropertyMetadata(null, OnMeterPropertyChanged));

    private readonly Rectangle _track;
    private readonly Rectangle _fill;

    public UsageMeter()
    {
        MinHeight = 6;
        Height = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _track = new Rectangle
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _fill = new Rectangle
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Children.Add(_track);
        Children.Add(_fill);
        SizeChanged += (_, _) => UpdateMeter();
        Loaded += (_, _) => UpdateMeter();
    }

    public double UsedPercent
    {
        get => (double)GetValue(UsedPercentProperty);
        set => SetValue(UsedPercentProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private static void OnMeterPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is UsageMeter meter)
        {
            meter.UpdateMeter();
        }
    }

    private void UpdateMeter()
    {
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var radius = Math.Max(0, height / 2);
        var clamped = double.IsFinite(UsedPercent) ? Math.Clamp(UsedPercent, 0, 100) : 0;

        _track.Fill = TrackBrush;
        _track.RadiusX = radius;
        _track.RadiusY = radius;

        _fill.Fill = FillBrush;
        _fill.RadiusX = radius;
        _fill.RadiusY = radius;
        _fill.Width = Math.Max(0, width * clamped / 100);
    }
}
