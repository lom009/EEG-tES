using System;
using System.IO;

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Views.Pages;

public partial class PictureNamingPageView : UserControl
{
    public PictureNamingPageView() => InitializeComponent();

    private async void ExportResults(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PictureNamingPageViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title="导出实验结果", SuggestedFileName="图片命名结果.json" });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await vm.WriteResultsAsync(stream);
            vm.ResultMessage = "结果已导出";
        }
        catch (Exception error) { vm.ResultMessage = error.Message; }
    }

    private async void CopySettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PictureNamingPageViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync($"{vm.CurrentText}　{vm.Electrodes}　{vm.SelectedRound?.Settings}"); vm.ResultMessage="配置已复制"; }
        catch (Exception error) { vm.ResultMessage = error.Message; }
    }
}
