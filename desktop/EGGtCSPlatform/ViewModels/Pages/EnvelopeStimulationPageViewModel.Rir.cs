using System;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed partial class EnvelopeStimulationPageViewModel
{
    private TrainingRoomSettings Rir => _session?.RirSettings ?? TrainingRoomSettings.Default;
    public bool IsRirMode => _session?.Mode == SpeechTrainingMode.FixedRir;
    public RirResponse? RirPreview => _session?.RirPreview;
    public bool IsGeneratingRir => _session?.IsGeneratingRir == true;
    public bool CanCancelRir => !_disposed && _session?.CanCancelRir == true;
    public string RirStatus =>
        IsGeneratingRir ? "正在生成 RIR…"
        : RirPreview is { } r
            ? $"RIR {r.Effective.Length / 16000d:0.###} s；直达声 {r.DirectFrame / 16d:0.###} ms；早晚分界 {r.DivisionFrame / 16d:0.###} ms"
        : "参数改变后需重新生成；开始播放时自动生成。";
    public string RirConditionText =>
        $"T60 {Rir.Acoustics.T60:0.###} s · 早期{(Rir.Acoustics.Early ? "开" : "关")} · 晚期{(Rir.Acoustics.Late ? "开" : "关")}";
    public IAsyncRelayCommand GenerateRirCommand { get; private set; } = null!;
    public IRelayCommand CancelRirCommand { get; private set; } = null!;

    private void InitializeRirCommands()
    {
        GenerateRirCommand = new AsyncRelayCommand(
            async () =>
            {
                if (_session is not null)
                    await _session.GenerateRirAsync();
                Refresh();
            },
            () => !_disposed && IsRirMode && CanConfigure && !IsGeneratingRir
        );
        CancelRirCommand = new RelayCommand(() => _session?.CancelRir(), () => CanCancelRir);
    }

    private static decimal ToRirDecimal(double value) =>
        value >= (double)decimal.MaxValue ? decimal.MaxValue
        : value <= (double)decimal.MinValue ? decimal.MinValue
        : (decimal)value;

    private void UpdateRir(TrainingRoomSettings settings)
    {
        if (!CanConfigure)
            return;
        _session?.SetRir(settings);
        OnPropertyChanged(nameof(RirAddNoise));
        OnPropertyChanged(nameof(RirNoise));
        OnPropertyChanged(nameof(RirConditionText));
    }

    public string RirNoise
    {
        get => NoiseOptions[(int)Rir.NoiseKind];
        set =>
            UpdateRir(
                Rir with
                {
                    NoiseKind =
                        value == NoiseOptions[1] ? SpeechNoiseKind.Babble : SpeechNoiseKind.Ssn,
                }
            );
    }
    public decimal RirRoomX
    {
        get => ToRirDecimal(Rir.Acoustics.Room.X);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Room = Rir.Acoustics.Room with { X = (double)value },
                    },
                }
            );
    }
    public decimal RirRoomY
    {
        get => ToRirDecimal(Rir.Acoustics.Room.Y);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Room = Rir.Acoustics.Room with { Y = (double)value },
                    },
                }
            );
    }
    public decimal RirRoomZ
    {
        get => ToRirDecimal(Rir.Acoustics.Room.Z);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Room = Rir.Acoustics.Room with { Z = (double)value },
                    },
                }
            );
    }
    public decimal RirSourceX
    {
        get => ToRirDecimal(Rir.Acoustics.Source.X);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Source = Rir.Acoustics.Source with { X = (double)value },
                    },
                }
            );
    }
    public decimal RirSourceY
    {
        get => ToRirDecimal(Rir.Acoustics.Source.Y);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Source = Rir.Acoustics.Source with { Y = (double)value },
                    },
                }
            );
    }
    public decimal RirSourceZ
    {
        get => ToRirDecimal(Rir.Acoustics.Source.Z);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Source = Rir.Acoustics.Source with { Z = (double)value },
                    },
                }
            );
    }
    public decimal RirListenerX
    {
        get => ToRirDecimal(Rir.Acoustics.Listener.X);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Listener = Rir.Acoustics.Listener with { X = (double)value },
                    },
                }
            );
    }
    public decimal RirListenerY
    {
        get => ToRirDecimal(Rir.Acoustics.Listener.Y);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Listener = Rir.Acoustics.Listener with { Y = (double)value },
                    },
                }
            );
    }
    public decimal RirListenerZ
    {
        get => ToRirDecimal(Rir.Acoustics.Listener.Z);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with
                    {
                        Listener = Rir.Acoustics.Listener with { Z = (double)value },
                    },
                }
            );
    }
    public decimal RirT60
    {
        get => ToRirDecimal(Rir.Acoustics.T60);
        set => UpdateRir(Rir with { Acoustics = Rir.Acoustics with { T60 = (double)value } });
    }
    public decimal RirLengthSeconds
    {
        get => ToRirDecimal(Rir.Acoustics.LengthSeconds);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with { LengthSeconds = (double)value },
                }
            );
    }
    public decimal RirEarlyLateMilliseconds
    {
        get => ToRirDecimal(Rir.Acoustics.EarlyLateMilliseconds);
        set =>
            UpdateRir(
                Rir with
                {
                    Acoustics = Rir.Acoustics with { EarlyLateMilliseconds = (double)value },
                }
            );
    }
    public decimal RirSnrDb
    {
        get => ToRirDecimal(Rir.SnrDb);
        set => UpdateRir(Rir with { SnrDb = (double)value });
    }
    public bool RirEarly
    {
        get => Rir.Acoustics.Early;
        set => UpdateRir(Rir with { Acoustics = Rir.Acoustics with { Early = value } });
    }
    public bool RirLate
    {
        get => Rir.Acoustics.Late;
        set => UpdateRir(Rir with { Acoustics = Rir.Acoustics with { Late = value } });
    }
    public bool RirAddNoise
    {
        get => Rir.AddNoise;
        set => UpdateRir(Rir with { AddNoise = value });
    }
}
