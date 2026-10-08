using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Views.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed class DialogAdornerVisualApp : App
{
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
        Directory.CreateDirectory(output);
        var error = Path.Combine(output, "DialogAdorner.error.txt");
        var result = Path.Combine(output, "DialogAdorner.passed.txt");
        File.Delete(error);
        File.Delete(result);
        try
        {
            await Run(desktop, output).WaitAsync(VisualTestOptions.Current.Timeout);
            File.WriteAllText(result, "PASS: AdornerLayer ancestry, binding, stacking, hit testing, Escape, resize, reattachment and cleanup.");
            desktop.Shutdown(0);
        }
        catch (Exception exception)
        {
            File.WriteAllText(error, exception.ToString());
            desktop.Shutdown(1);
        }
    }

    private static MainViewModel CreateModel() => new(
        new PageFactory((_, _) => throw new NotSupportedException()),
        new DeviceSelectionContext()
    );

    private static async Task Run(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        var model = CreateModel();
        var main = new MainView { DataContext = model };
        var root = main.FindControl<Grid>("DialogAdornedRoot")!;
        var host = (DialogAdornerHost)AdornerLayer.GetAdorner(root)!;
        // A local template provides a focused editor and records dismissal attempts.
        // Production dialogs are separately checked through the global ViewLocator below.
        host.DataTemplates.Add(new FuncDataTemplate<ProbeDialog>((_, _) => new TextBox
        {
            Width = 320,
            Height = 100,
            Text = "Adorner keyboard regression",
        }));
        var window = new Window { Width = 1000, Height = 700, Content = main };
        desktop.MainWindow = window;
        window.Show();
        await Settle();
        var layer = AdornerLayer.GetAdornerLayer(root)
            ?? throw new InvalidOperationException("Missing AdornerLayer");
        Check(host.GetVisualParent() == layer, "Host must belong to AdornerLayer");
        Check(!root.Children.Contains(host) && root.Children.Count == 1, "Page layout must not contain dialogs");
        Check(!host.IsVisible && !host.IsHitTestVisible, "Empty host must not intercept input");
        var service = new DialogService(() => window);

        var windowEscape = new ProbeDialog();
        var windowEscapeTask = service.ShowDialogAsync(model, windowEscape);
        await Settle();
        Escape(window);
        Check(windowEscape.Dismissals == 1, "Escape at window root must close an allowed dialog");
        await windowEscapeTask;

        var unfocused = new ProbeDialog();
        var unfocusedTask = service.ShowDialogAsync(model, unfocused);
        await Settle();
        Escape(window, focusSource: false);
        Check(unfocused.Dismissals == 1, "Escape without a focused control must close an allowed dialog");
        await unfocusedTask;
        var attachedBindings = window.KeyBindings.Count;

        var lower = new ProbeDialog();
        var lowerTask = service.ShowDialogAsync(model, lower);
        await Settle();
        Check(host.IsVisible && host.IsHitTestVisible, "Nonempty host must intercept input");
        Check(host.Bounds.Size == root.Bounds.Size, "Host must cover MainView");
        var overlay = Overlays(host).Single();
        Check(overlay.Bounds.Size == root.Bounds.Size, "Overlay must cover MainView");
        await Until(() => Math.Abs(overlay.Opacity - 0.6) < 0.01,
            () => $"Overlay animation must reach 0.6 (actual {overlay.Opacity}, open {lower.IsDialogOpen})");
        var content = host.GetVisualDescendants().OfType<ContentControl>().Single(x => x.Name == "DialogContent");
        var position = content.TranslatePoint(default, host)!.Value;
        Check(Math.Abs(position.X + content.Bounds.Width / 2 - host.Bounds.Width / 2) < 1
            && Math.Abs(position.Y + content.Bounds.Height / 2 - host.Bounds.Height / 2) < 1,
            "Dialog must be centered");
        Check(Math.Abs(content.Opacity - 1) < 0.01, "Content animation must reach full opacity");
        Check(IsWithin(host.InputHitTest(new Point(4, 4)), overlay), "Background hit must reach overlay");

        var upper = new ProbeDialog { CanDismiss = false };
        var upperTask = service.ShowDialogAsync(model, upper);
        await Settle();
        var upperOverlay = Overlays(host).Last();
        Check(ReferenceEquals(upperOverlay.DataContext, upper), "Last dialog must be on top");
        Check(IsWithin(host.InputHitTest(new Point(4, 4)), upperOverlay), "Top overlay must intercept input");
        Click(upperOverlay);
        Check(upper.IsDialogOpen && lower.IsDialogOpen, "Dismissal guard must prevent overlay close");
        Escape(window);
        Check(upper.IsDialogOpen && lower.Dismissals == 0, "Window Escape must respect top dialog guard");
        var editor = host.GetVisualDescendants().OfType<TextBox>().Last();
        editor.Focus();
        Escape(editor);
        Check(upper.IsDialogOpen && lower.Dismissals == 0, "Escape must respect dismissal guard");
        upper.CanDismiss = true;
        Escape(editor);
        Check(upper.Dismissals == 1 && lower.Dismissals == 0,
            $"One Escape must dismiss exactly one dialog (upper={upper.Dismissals}, lower={lower.Dismissals}, focus={editor.IsFocused})");
        Check(!upperTask.IsCompleted, "Exit animation must retain dialog until delayed removal");
        await upperTask;
        await Settle();
        Check(model.DialogStack.Count == 1 && lower.IsDialogOpen, "Lower dialog must remain");

        window.Width = 820;
        window.Height = 580;
        await Settle();
        Check(host.Bounds.Size == root.Bounds.Size && Overlays(host).Single().Bounds.Size == root.Bounds.Size,
            "Resize must update host and overlay bounds");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
        {
            bitmap.Render(window);
            bitmap.Save(Path.Combine(output, "DialogAdorner.resized.png"), new PngBitmapEncoderOptions());
        }

        window.Content = null;
        await Settle();
        Check(!layer.Children.Contains(host) && lower.IsDialogOpen, "Detach must remove host without closing model");
        Check(window.KeyBindings.Count == attachedBindings - 1, "Detach must remove window Escape binding");
        Escape(window);
        Check(lower.IsDialogOpen, "Detached MainView must not handle window Escape");
        window.Content = main;
        await Settle();
        Check(layer.Children.OfType<DialogAdornerHost>().Count() == 1 && host.IsVisible,
            "Reattach must restore exactly one host");
        Check(window.KeyBindings.Count == attachedBindings, "Reattach must restore exactly one Escape binding");

        var replacement = CreateModel();
        main.DataContext = replacement;
        await Settle();
        Check(ReferenceEquals(host.DataContext, replacement) && !host.IsVisible, "Context replacement must update host");
        main.DataContext = model;
        await Settle();
        Check(host.IsVisible && Overlays(host).Count() == 1, "Original stack must reappear");
        Click(Overlays(host).Single());
        await lowerTask;
        await Settle();
        Check(model.DialogStack.Count == 0 && !host.IsVisible && !host.IsHitTestVisible,
            "Last close must clear stack and release input");
        Check(!IsWithin(main.InputHitTest(new Point(4, 4)), host), "Page must receive input after close");

        var pageEscape = new ProbeDialog();
        var pageEscapeTask = service.ShowDialogAsync(model, pageEscape);
        await Settle();
        Escape(root);
        await pageEscapeTask;
        Check(pageEscape.Dismissals == 1, "Page Escape binding must still work");

        var confirmation = new ConfirmDialogViewModel();
        var confirmTask = service.ShowDialogAsync(model, confirmation);
        await Settle();
        Check(host.GetVisualDescendants().Any(x => x is EGGtCSPlatform.Views.ConfirmDialogView),
            "Global ViewLocator must resolve production dialog");
        confirmation.Close();
        await confirmTask;
        window.Close();
        Check(!layer.Children.Contains(host), "Window close must remove host");
    }

    private static IEnumerable<Button> Overlays(Control host) => host.GetVisualDescendants()
        .OfType<Button>().Where(x => x.Name == "DialogOverlay");

    private static void Click(Button button) => typeof(Button).GetMethod("OnClick",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(button, null);

    private static bool IsWithin(object? hit, Visual control) => hit is Visual visual
        && (ReferenceEquals(visual, control) || visual.GetVisualAncestors().Contains(control));

    private static void Escape(Control source, bool focusSource = true)
    {
        // KeyBindings run in KeyboardDevice before routed KeyDown events.
        // Raising KeyDown directly bypasses the behavior this test must exercise.
        // Avalonia 12 hides these test-driver APIs from its reference assembly.
        var keyboardType = typeof(KeyboardDevice);
        var keyboard = Activator.CreateInstance(keyboardType)!;
        var focus = keyboardType.GetMethod("SetFocusedElement",
            [typeof(IInputElement), typeof(NavigationMethod), typeof(KeyModifiers)])!;
        focus.Invoke(keyboard, [focusSource ? source : null, NavigationMethod.Unspecified, KeyModifiers.None]);
        var inputRoot = typeof(TopLevel).GetProperty("InputRoot",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic)!.GetValue(TopLevel.GetTopLevel(source));
        var input = Activator.CreateInstance(typeof(RawKeyEventArgs),
            [keyboard, (ulong)0, inputRoot, RawKeyEventType.KeyDown,
                Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null, default(KeyDeviceType)])!;
        keyboardType.GetMethod("ProcessRawEvent")!.Invoke(keyboard, [input]);
        focus.Invoke(keyboard, [null, NavigationMethod.Unspecified, KeyModifiers.None]);
    }

    private static Task Settle() => Task.Delay(250);

    private static async Task Until(Func<bool> predicate, Func<string> error)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
                return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException(error());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ProbeDialog : DialogViewModel
    {
        public bool CanDismiss { get; set; } = true;
        public int Dismissals { get; private set; }
        public override bool AllowOverlayDismiss => CanDismiss;
        protected override void OnOverlayDismiss()
        {
            Dismissals++;
            base.OnOverlayDismiss();
        }
    }
}
