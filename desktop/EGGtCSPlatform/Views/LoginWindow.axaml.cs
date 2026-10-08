using System;
using Avalonia;
using Avalonia.Controls;

namespace EGGtCSPlatform.Views;

public partial class LoginWindow : Window
{
    private const double AspectRatio = 1440d / 900d;

    private Size _previousClientSize = new(1440, 900);
    private bool _isAdjustingSize;

    public LoginWindow()
    {
        InitializeComponent();
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        if (_isAdjustingSize || WindowState != WindowState.Normal)
        {
            return;
        }

        var size = e.ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var widthChange =
            Math.Abs(size.Width - _previousClientSize.Width)
            / Math.Max(_previousClientSize.Width, 1);
        var heightChange =
            Math.Abs(size.Height - _previousClientSize.Height)
            / Math.Max(_previousClientSize.Height, 1);

        var targetSize =
            widthChange >= heightChange
                ? new Size(size.Width, size.Width / AspectRatio)
                : new Size(size.Height * AspectRatio, size.Height);

        if (
            Math.Abs(size.Width - targetSize.Width) < 0.5
            && Math.Abs(size.Height - targetSize.Height) < 0.5
        )
        {
            _previousClientSize = size;
            return;
        }

        try
        {
            _isAdjustingSize = true;
            ClientSize = targetSize;
            _previousClientSize = targetSize;
        }
        finally
        {
            _isAdjustingSize = false;
        }
    }
}
