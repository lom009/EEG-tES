using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace EGGtCSPlatform.VisualTests;

/// <summary>Samples the visible blue marker from the desktop, including compositor transforms.</summary>
internal sealed class PlayheadScreenProbe : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    public ConcurrentQueue<(long Time, double Progress)> Frames { get; } = new();
    public byte[]? LastPixels => Volatile.Read(ref _lastPixels);
    private byte[]? _lastPixels;

    public PlayheadScreenProbe(Control marker, bool curves = false, bool capturePixels = false)
    {
        var start = marker.PointToScreen(new Point(0, curves ? 0 : 5));
        var end = marker.PointToScreen(new Point(marker.Bounds.Width, curves ? marker.Bounds.Height : 5));
        var width = end.X - start.X;
        var height = curves ? Math.Max(1, end.Y - start.Y) : 1;
        _worker = Task.Factory.StartNew(() =>
        {
            var screen = GetDC(0);
            var memory = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            var bitmap = CreateDIBSection(screen, ref info, 0, out var pixels, 0, 0);
            var old = SelectObject(memory, bitmap);
            var row = new byte[width * height * 4];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (!BitBlt(memory, 0, 0, width, height, screen, start.X, start.Y, 0x00CC0020))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Cannot sample the playhead: width={width}, screen={screen}, memory={memory}, bitmap={bitmap}");
                    Marshal.Copy(pixels, row, 0, row.Length);
                    if (capturePixels)
                        Volatile.Write(ref _lastPixels, (byte[])row.Clone());
                    var best = -1;
                    var score = 0;
                    for (var pixel = 0; pixel < width * height; pixel++)
                    {
                        var blue = row[pixel * 4];
                        var green = row[pixel * 4 + 1];
                        var red = row[pixel * 4 + 2];
                        var x = pixel % width;
                        if (curves)
                        {
                            // Exact interior colors of voice, envelope and stimulus curves.
                            if ((Math.Abs(red - 120) < 12 && Math.Abs(green - 197) < 12 && Math.Abs(blue - 251) < 12)
                                || (Math.Abs(red - 255) < 12 && Math.Abs(green - 112) < 12 && Math.Abs(blue - 77) < 12)
                                || (Math.Abs(red - 166) < 12 && Math.Abs(green - 160) < 12 && Math.Abs(blue - 255) < 12)
                                || (Math.Abs(red - 149) < 12 && Math.Abs(green - 54) < 12 && Math.Abs(blue - 243) < 12))
                                best = Math.Max(best, x);
                            continue;
                        }
                        var candidate = blue - red;
                        if (blue > 180 && green < 190 && candidate > 70 && candidate > score)
                        {
                            score = candidate;
                            best = x;
                        }
                    }
                    if (best >= 0)
                        Frames.Enqueue((Stopwatch.GetTimestamp(), best / (double)(width - 1)));
                    _stop.Token.WaitHandle.WaitOne(8);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(0, screen);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.GetAwaiter().GetResult();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint Used, Important;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);
}
