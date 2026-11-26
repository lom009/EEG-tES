using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.VisualTests;

public sealed class NumericCornersVisualApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var stack = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        var inputs = new List<NumericUpDown>();
        string[] states = ["Normal", "Focus", "Hover up", "Pressed down", "Disabled", "No spinner"];
        foreach (var duration in new[] { true, false })
        {
            stack.Children.Add(new TextBlock { Text = duration ? "Experiment duration" : "Stimulus parameter" });
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16 };
            for (var i = 0; i < states.Length; i++)
            {
                NumericUpDown input = duration
                    ? new ExperimentDurationInput { Unit = new DurationUnitOption(ExperimentDurationUnit.Seconds, "s") }
                    : new StimulusNumericInput();
                input.Width = 110;
                input.Height = 32;
                input.CornerRadius = new CornerRadius(5);
                input.BorderThickness = new Thickness(1);
                input.BorderBrush = Brush.Parse("#D7E0ED");
                input.Background = Brushes.White;
                input.Value = 5;
                input.Minimum = 0;
                input.Maximum = 100;
                input.Increment = 1;
                input.IsEnabled = i != 4;
                input.ShowButtonSpinner = i != 5;
                var cell = new StackPanel { Spacing = 8 };
                cell.Children.Add(new TextBlock { Text = states[i] });
                cell.Children.Add(input);
                row.Children.Add(cell);
                inputs.Add(input);
            }
            stack.Children.Add(row);
        }
        var surface = new Border { Background = Brushes.White, Child = stack };
        var window = new Window { Width = 820, Height = 270, Content = surface };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var output = Path.GetDirectoryName(VisualTestOptions.Current.ErrorPath)!;
            try
            {
                Directory.CreateDirectory(output);
                File.Delete(VisualTestOptions.Current.ErrorPath);
                await Task.Delay(250);
                for (var i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    var state = i % states.Length;
                    if (state == 1)
                        ((IPseudoClasses)input.Classes).Set(":focus-within", true);
                    if (state is 2 or 3)
                    {
                        var name = state == 2 ? "PART_IncreaseButton" : "PART_DecreaseButton";
                        var button = input.GetVisualDescendants().OfType<RepeatButton>().Single(x => x.Name == name);
                        ((IPseudoClasses)button.Classes).Set(state == 2 ? ":pointerover" : ":pressed", true);
                    }
                }
                await Task.Delay(100);
                foreach (var scale in new[] { 1d, 1.5d })
                {
                    using var bitmap = new RenderTargetBitmap(
                        new PixelSize((int)(surface.Bounds.Width * scale), (int)(surface.Bounds.Height * scale)),
                        new Vector(96 * scale, 96 * scale));
                    bitmap.Render(surface);
                    using var stream = File.Create(Path.Combine(output, $"numeric-corners-{scale * 100:0}.png"));
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                }
                foreach (var input in new[] { inputs[0], inputs[6] })
                {
                    var button = input.GetVisualDescendants().OfType<RepeatButton>()
                        .Single(x => x.Name == "PART_IncreaseButton");
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), surface)!.Value;
                    var hit = surface.InputHitTest(point) as Visual;
                    if (hit != button && !(hit?.GetVisualAncestors().Contains(button) ?? false))
                        throw new InvalidOperationException("Spinner button is covered by the border overlay.");
                    var before = input.Value;
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (input.Value <= before)
                        throw new InvalidOperationException("Spinner click did not increase the value.");
                    before = input.Value;
                    using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                    button.RaiseEvent(new PointerPressedEventArgs(button, pointer, surface, point, 0,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                        KeyModifiers.None, 1));
                    await Task.Delay(800);
                    button.RaiseEvent(new PointerReleasedEventArgs(button, pointer, surface, point, 800,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                        KeyModifiers.None, MouseButton.Left));
                    if (input.Value <= before + input.Increment)
                        throw new InvalidOperationException("Holding the spinner did not repeat.");
                    var textBox = input.GetVisualDescendants().OfType<TextBox>().Single();
                    textBox.Focus();
                    textBox.Text = "7";
                    textBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                    if (input.Value != 7)
                        throw new InvalidOperationException("Numeric text could not be committed.");
                }
                var numeric = (StimulusNumericInput)inputs[6];
                numeric.DecimalPlaces = 2;
                numeric.Minimum = 0.04m;
                numeric.Maximum = 2m;
                numeric.Increment = 0.04m;
                numeric.Value = 0.08m;
                var editor = numeric.GetVisualDescendants().OfType<TextBox>().Single();
                var separator = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                editor.Focus();
                editor.SelectAll();
                editor.SelectedText = "0" + separator + "13";
                await Task.Delay(50);
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                await Task.Delay(50);
                if (numeric.Value != 0.12m || editor.Text != "0" + separator + "12")
                    throw new InvalidOperationException("Enter did not align pasted current to its step.");
                editor.SelectAll();
                editor.SelectedText = "0" + separator + "14";
                await Task.Delay(50);
                inputs[0].Focus();
                await Task.Delay(50);
                if (numeric.Value != 0.16m || editor.Text != "0" + separator + "16")
                    throw new InvalidOperationException("Focus loss did not align current to its step.");
                numeric.AllowNegative = true;
                numeric.Minimum = -30;
                numeric.Maximum = 30;
                numeric.Increment = 1;
                editor.Focus();
                editor.SelectAll();
                editor.SelectedText = "-7" + separator + "25";
                await Task.Delay(50);
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                await Task.Delay(50);
                if (numeric.Value != -7)
                    throw new InvalidOperationException("Signed manual value did not align to its step.");

                var plain = new StepNumericUpDown { Minimum = 0, Maximum = 1, Increment = 0.05m, Value = 0.5m };
                stack.Children.Add(plain);
                await Task.Delay(50);
                var plainEditor = plain.GetVisualDescendants().OfType<TextBox>().Single();
                plainEditor.Focus();
                plainEditor.SelectAll();
                plainEditor.SelectedText = "0" + separator + "53";
                await Task.Delay(50);
                plainEditor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                await Task.Delay(50);
                if (plain.Value != 0.55m)
                    throw new InvalidOperationException("Simulation numeric input did not align to its step.");
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                File.WriteAllText(VisualTestOptions.Current.ErrorPath, exception.ToString());
                Environment.ExitCode = 1;
            }
            desktop.Shutdown(Environment.ExitCode);
        };
        window.Show();
    }
}
