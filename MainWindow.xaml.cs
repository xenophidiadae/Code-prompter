using System;
using System.ComponentModel;
using System.Windows;

namespace CodePrompter;

public partial class MainWindow : Window
{
    private readonly CodePrompterService _prompterService = new();
    private readonly OverlayWindow _overlayWindow = new();

    public MainWindow()
    {
        InitializeComponent();
        _overlayWindow.SetFontSize(OverlayFontSizeSlider.Value);
        _prompterService.StateChanged += OnStateChanged;
        _prompterService.RefreshScripts();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _overlayWindow.Close();
        _prompterService.Dispose();
        base.OnClosing(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        _overlayWindow.PositionWindow();
        base.OnLocationChanged(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        _overlayWindow.PositionWindow();
        base.OnRenderSizeChanged(sizeInfo);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _prompterService.RefreshScripts();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _prompterService.Start();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _prompterService.Stop();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _prompterService.Reset();
    }

    private void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        _prompterService.SyncFromClipboard();
    }

    private void OverlayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _prompterService.SetOverlayEnabled(OverlayCheckBox.IsChecked == true);
    }

    private void OverlayFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _overlayWindow.SetFontSize(e.NewValue);
        _overlayWindow.PositionWindow();
    }

    private void OnStateChanged(PrompterState state)
    {
        StatusTextBlock.Text = state.Status;
        ActiveScriptTextBlock.Text = state.ActiveScriptName;
        IndexTextBlock.Text = $"{state.CurrentIndex} / {state.CodeLength}";
        ModeTextBlock.Text = state.IsWildcardMode ? "Свое название" : state.IsRunning ? "Прослушивание" : "Ожидание";
        CurrentLineTextBlock.Text = string.IsNullOrWhiteSpace(state.CurrentLine) ? "-" : state.CurrentLine;
        PreviewTextBlock.Text = string.IsNullOrWhiteSpace(state.Preview) ? "-" : state.Preview;
        ScriptsTextBlock.Text = state.AvailableScripts;

        if (CodeTextBox.Text != state.ActiveCode)
        {
            CodeTextBox.Text = state.ActiveCode;
            CodeTextBox.ScrollToHome();
        }

        StartButton.IsEnabled = !state.IsRunning;
        StopButton.IsEnabled = state.IsRunning;
        SyncButton.IsEnabled = state.CodeLength > 0;
        ResetButton.IsEnabled = state.CodeLength > 0;
        if (OverlayCheckBox.IsChecked != state.OverlayEnabled)
        {
            OverlayCheckBox.IsChecked = state.OverlayEnabled;
        }

        if (state.UseVisualOverlay)
        {
            ShowOverlay();
            _overlayWindow.SetWord(state.OverlayWord);
        }
        else
        {
            HideOverlay();
        }
    }

    private void ShowOverlay()
    {
        _overlayWindow.PositionWindow();
        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
        }
    }

    private void HideOverlay()
    {
        if (_overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
        }
    }
}
