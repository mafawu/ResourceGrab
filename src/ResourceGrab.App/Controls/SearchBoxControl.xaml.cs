using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ResourceGrab.App.Controls;

public partial class SearchBoxControl : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBoxControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty WatermarkProperty = DependencyProperty.Register(
        nameof(Watermark), typeof(string), typeof(SearchBoxControl), new PropertyMetadata("搜索"));

    public static readonly DependencyProperty ShowClearButtonProperty = DependencyProperty.Register(
        nameof(ShowClearButton), typeof(bool), typeof(SearchBoxControl), new PropertyMetadata(true));

    public event EventHandler<string>? SearchChanged;
    public event EventHandler<string>? SearchSubmitted;

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Watermark { get => (string)GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }
    public bool ShowClearButton { get => (bool)GetValue(ShowClearButtonProperty); set => SetValue(ShowClearButtonProperty, value); }

    public SearchBoxControl()
    {
        InitializeComponent();
    }

    public void FocusInput()
    {
        InputTextBox.Focus();
        InputTextBox.CaretIndex = InputTextBox.Text.Length;
    }

    public void Clear() => InputTextBox.Clear();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchBoxControl control)
            control.SearchChanged?.Invoke(control, e.NewValue as string ?? string.Empty);
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetCurrentValue(TextProperty, InputTextBox.Text);
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyboardDevice.Modifiers != ModifierKeys.None)
            return;

        SearchSubmitted?.Invoke(this, Text);
        e.Handled = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        InputTextBox.Clear();
        FocusInput();
    }
}
