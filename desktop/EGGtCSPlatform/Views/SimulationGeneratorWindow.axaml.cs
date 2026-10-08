using Avalonia.Controls;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.Views;

public partial class SimulationGeneratorWindow : Window
{
    public SimulationGeneratorWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (Avalonia.Application.Current is App { IsExiting: true })
                return;
            if (DataContext is SimulationGeneratorViewModel { IsGenerating: true } vm)
            {
                vm.Cancel();
                e.Cancel = true;
                vm.Status = "正在取消并清理当前记录，请稍后关闭窗口。";
            }
        };
    }
}
