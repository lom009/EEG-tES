namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Optional output owned by the host, with no device SDK dependency.</summary>
public interface ISpeechStimulationOutput
{
    event EventHandler? Changed;
    event EventHandler<string>? Faulted;
    string? Status { get; }
    Task PrepareAsync(EnvelopePlaybackPlan plan, CancellationToken token);
    Task StartAsync(CancellationToken token);
    Task WaitForCompletionAsync(CancellationToken token);

    // Cleanup must not reuse the cancelled playback token.
    Task StopAsync();
}
