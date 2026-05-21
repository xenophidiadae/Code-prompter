using System;
using System.Windows;

namespace CodePrompter;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionWindow();
    }

    public void SetWord(string word)
    {
        OverlayTextBlock.Text = string.IsNullOrWhiteSpace(word) ? "..." : word;
    }

    public void SetFontSize(double fontSize)
    {
        OverlayTextBlock.FontSize = fontSize;
    }

    public void PositionWindow()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;
    }
}
