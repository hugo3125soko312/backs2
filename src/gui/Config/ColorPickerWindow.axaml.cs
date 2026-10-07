using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace backs2.Views;

public partial class ColorPickerWindow : Window
{
    private bool _syncing;

    public Color? Result { get; private set; }

    public ColorPickerWindow()
    {
        InitializeComponent();

        OkButton.Click += OnOk;
        CancelButton.Click += OnCancel;

        HexBox.KeyDown += OnHexKeyDown;
        HexBox.LostFocus += OnHexLostFocus;

        Spectrum.PropertyChanged += OnSpectrumPropertyChanged;
        ValueSlider.PropertyChanged += OnValueSliderPropertyChanged;
    }

    /// <summary>
    /// Seed the picker with the currently-selected theme color. The
    /// ColorSpectrum works in HSV internally; we convert once on entry
    /// and back to RGB only when the dialog closes.
    /// </summary>
    public void Initialize(Color initial)
    {
        Spectrum.HsvColor = initial.ToHsv();
        SyncFromSpectrum();
    }

    private void OnSpectrumPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ColorSpectrum.HsvColorProperty)
            SyncFromSpectrum();
    }

    private void OnValueSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty) return;
        if (_syncing) return;

        // Slider is 0-100; HSV V is 0-1. At 0 the result is pure black
        // regardless of the hue/saturation ring position.
        _syncing = true;
        var hsv = Spectrum.HsvColor;
        Spectrum.HsvColor = new HsvColor(hsv.A, hsv.H, hsv.S, ValueSlider.Value / 100.0);
        _syncing = false;
    }

    /// <summary>
    /// Reflect the current spectrum state into the slider, hex box, and
    /// RGB readout. Guarded by _syncing so slider-driven changes do not
    /// loop back into the slider handler.
    /// </summary>
    private void SyncFromSpectrum()
    {
        if (_syncing) return;
        _syncing = true;

        var hsv = Spectrum.HsvColor;
        var rgb = hsv.ToRgb();

        HexBox.Text = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
        RgbLabel.Text = $"RGB({rgb.R}, {rgb.G}, {rgb.B})";
        ValueSlider.Value = hsv.V * 100.0;

        _syncing = false;
    }

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHexInput();
            e.Handled = true;
        }
    }

    private void OnHexLostFocus(object? sender, RoutedEventArgs e)
    {
        ApplyHexInput();
    }

    private void ApplyHexInput()
    {
        if (_syncing) return;
        if (string.IsNullOrWhiteSpace(HexBox.Text)) return;

        if (Color.TryParse(HexBox.Text, out var color))
            Spectrum.HsvColor = color.ToHsv();
        else
            SyncFromSpectrum(); // revert bad input to the current color
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Result = Spectrum.HsvColor.ToRgb();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}