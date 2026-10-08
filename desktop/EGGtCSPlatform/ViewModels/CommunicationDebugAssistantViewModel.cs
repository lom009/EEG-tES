using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels;

public partial class CommunicationDebugAssistantViewModel : ViewModelBase, IDisposable
{
    private readonly UdpTrafficCaptureSession _captureSession;
    private readonly DispatcherTimer _timer;
    private readonly List<UdpTrafficDisplayEntry> _allEntries = [];
    private long _generation;
    private bool _disposed;

    [ObservableProperty]
    private bool _showTransmit = true;

    [ObservableProperty]
    private bool _showReceive = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureButtonText))]
    [NotifyPropertyChangedFor(nameof(CaptureStatusText))]
    private bool _isCapturePaused;

    public CommunicationDebugAssistantViewModel(UdpTrafficCaptureSession captureSession)
        : this(captureSession, startTimer: true) { }

    internal CommunicationDebugAssistantViewModel(
        UdpTrafficCaptureSession captureSession,
        bool startTimer
    )
    {
        _captureSession = captureSession;
        VisibleEntries = [];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
        if (startTimer)
            _timer.Start();
    }

    public ObservableCollection<UdpTrafficDisplayEntry> VisibleEntries { get; }

    public int PacketCount => _allEntries.Count;

    public string PacketCountText =>
        $"保留 {PacketCount}/{UdpTrafficCaptureSession.MaximumEntries} 包";

    public bool HasVisibleEntries => VisibleEntries.Count > 0;

    public string CaptureButtonText => IsCapturePaused ? "继续记录" : "暂停记录";

    public string CaptureStatusText => IsCapturePaused ? "已暂停" : "记录中";

    partial void OnShowTransmitChanged(bool value) => RebuildVisibleEntries();

    partial void OnShowReceiveChanged(bool value) => RebuildVisibleEntries();

    [RelayCommand]
    private void Clear()
    {
        _generation = _captureSession.Clear();
        _allEntries.Clear();
        VisibleEntries.Clear();
        NotifyCountChanged();
        OnPropertyChanged(nameof(HasVisibleEntries));
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        IsCapturePaused = !IsCapturePaused;
        _captureSession.SetPaused(IsCapturePaused);
    }

    internal void FlushPending()
    {
        if (_disposed)
            return;
        var batch = _captureSession.Drain();
        if (batch.Generation != _generation)
        {
            _generation = batch.Generation;
            _allEntries.Clear();
            VisibleEntries.Clear();
        }
        if (batch.Entries.Count == 0)
            return;

        foreach (var entry in batch.Entries)
        {
            _allEntries.Add(entry);
            if (IsVisible(entry))
                VisibleEntries.Add(entry);
        }
        while (_allEntries.Count > UdpTrafficCaptureSession.MaximumEntries)
        {
            var removed = _allEntries[0];
            _allEntries.RemoveAt(0);
            VisibleEntries.Remove(removed);
        }
        NotifyCountChanged();
        OnPropertyChanged(nameof(HasVisibleEntries));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _captureSession.Dispose();
    }

    private void OnTimerTick(object? sender, EventArgs e) => FlushPending();

    private void RebuildVisibleEntries()
    {
        VisibleEntries.Clear();
        foreach (var entry in _allEntries)
        {
            if (IsVisible(entry))
                VisibleEntries.Add(entry);
        }
        OnPropertyChanged(nameof(HasVisibleEntries));
    }

    private bool IsVisible(UdpTrafficDisplayEntry entry) =>
        entry.Direction == ByteTrafficDirection.Transmit ? ShowTransmit : ShowReceive;

    private void NotifyCountChanged()
    {
        OnPropertyChanged(nameof(PacketCount));
        OnPropertyChanged(nameof(PacketCountText));
    }
}
