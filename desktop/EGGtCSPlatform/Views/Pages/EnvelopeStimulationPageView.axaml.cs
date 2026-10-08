using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Views.Pages;

public partial class EnvelopeStimulationPageView : UserControl
{
    public EnvelopeStimulationPageView() => InitializeComponent();

    // Presentation-only adaptation: preserve four Figma cards at design width,
    // and use two rows when a small window cannot fit their complete labels.
    private void ResultSummarySizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid grid || args.NewSize.Width <= 0) return;
        var columns = args.NewSize.Width >= 740 ? 4 : 2;
        if (grid.ColumnDefinitions.Count == columns) return;
        grid.ColumnDefinitions = new ColumnDefinitions(columns == 4 ? "*,*,*,*" : "*,*");
        grid.RowDefinitions = new RowDefinitions(columns == 4 ? "Auto" : "Auto,Auto");
        for (var i = 0; i < grid.Children.Count; i++)
        { Grid.SetColumn(grid.Children[i], i % columns); Grid.SetRow(grid.Children[i], i / columns); }
    }

    private async void ImportCorpusClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not EnvelopeStimulationPageViewModel page || !page.CanEditCorpus) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "导入语料清单", AllowMultiple = false,
                FileTypeFilter = [new("语料清单 JSON") { Patterns = ["*.json"] }]
            });
            if (files.Count > 0) await page.ImportCorpusAsync(files[0].Path.LocalPath);
        }
        catch (Exception exception) { page.SetStatus($"语料导入失败：{exception.Message}", true); }
    }

    // OS file/clipboard interactions remain in the view; trial state lives exclusively in the VM.
    private async void ExportResultsClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not EnvelopeStimulationPageViewModel page || !page.IsResults) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "导出包络言语训练结果", SuggestedFileName = "envelope-training-result.json",
                FileTypeChoices = [new("完整结果 JSON") { Patterns = ["*.json"] }, new("逐字评分 CSV") { Patterns = ["*.csv"] }],
                ShowOverwritePrompt = true
            });
            if (file is null) return;
            var content = page.CreateSnapshotJson();
            if (file.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
                var rows = page.SavedTrials.SelectMany(trial => trial.Characters.Select((character, index) =>
                    $"{trial.SentenceNumber},{Quote(trial.Sentence)},{index + 1},{Quote(character.Character)},{(character.Correct ? 1 : 0)},{trial.CharactersPerSecond},{Quote(trial.AudioSha256)}"));
                content = "句次,题目,字序,字,正确,语速字每秒,音频SHA256\n" + string.Join("\n", rows);
            }
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteAsync(content);
            page.SetStatus("实验结果已导出。");
        }
        catch (Exception exception) { page.SetStatus($"导出失败：{exception.Message}", true); }
    }

    private async void CopyConfigurationClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not EnvelopeStimulationPageViewModel page || !page.IsResults) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { page.SetStatus("当前窗口的剪贴板不可用。", true); return; }
            await clipboard.SetTextAsync(page.CreateSnapshotJson());
            page.SetStatus("配置与逐字结果快照已复制。");
        }
        catch (Exception exception) { page.SetStatus($"复制失败：{exception.Message}", true); }
    }
}
