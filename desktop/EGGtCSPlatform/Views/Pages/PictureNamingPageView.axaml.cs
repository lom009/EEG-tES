using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Views.Pages;

public partial class PictureNamingPageView : UserControl
{
    private PictureNamingPageViewModel? _page;

    public PictureNamingPageView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_page is not null) _page.WaitForPicturePresentationAsync = null;
        _page = DataContext as PictureNamingPageViewModel;
        if (_page is not null) _page.WaitForPicturePresentationAsync = WaitForPicturePresentationAsync;
        base.OnDataContextChanged(e);
    }

    private async Task<long?> WaitForPicturePresentationAsync(CancellationToken token)
    {
        UpdateLayout();
        if (!PictureStimulus.IsEffectivelyVisible || PictureStimulus.Bounds is not { Width: > 0, Height: > 0 }
            || TopLevel.GetTopLevel(this) is not Window { IsActive: true, WindowState: not WindowState.Minimized }
            || ElementComposition.GetElementVisual(PictureStimulus) is not { } visual)
            return null;
        var batch = visual.Compositor.RequestCompositionBatchCommitAsync();
        await batch.Rendered.WaitAsync(token).ConfigureAwait(false);
        return Stopwatch.GetTimestamp();
    }

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
