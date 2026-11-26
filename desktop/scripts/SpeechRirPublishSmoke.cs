using EGGtCSPlatform.Audio.Core.Playback;
using EGGtCSPlatform.Audio.Sdl;
using EGGtCSPlatform.SpeechTraining;

// Runs against published assemblies/resources only, with SDL's silent dummy driver.
var root = Path.GetFullPath(args[0]);
Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
var corpora = new FileSpeechCorpusProvider(Path.Combine(root, "SpeechCorpora"));
var noises = new FileSpeechNoiseProvider(Path.Combine(root, "SpeechNoises"));
foreach (var corpus in corpora.Corpora)
foreach (var withNoise in new[] { false, true })
{
    using var player = new SdlAudioPlayer();
    using var session = new SpeechTrainingSession(corpora, player, 10, noiseProvider: noises);
    session.SelectCorpus(corpus.Id);
    session.SelectMode(SpeechTrainingMode.FixedRir);
    session.SetRir(
        TrainingRoomSettings.Default with
        {
            AddNoise = withNoise,
            NoiseKind = SpeechNoiseKind.Babble,
            SnrDb = -10,
        }
    );
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.Updated += (_, e) =>
    {
        if (e.State == AudioPlaybackState.Completed)
            done.TrySetResult();
        if (e.State == AudioPlaybackState.Failed)
            done.TrySetException(new IOException(e.Error));
    };
    await session.StartAsync();
    if (session.Error is not null || session.Waveform?.Preparation?.Rir is null)
        throw new IOException(session.Error ?? "Missing RIR metadata.");
    var prepared = session.Waveform.Preparation;
    prepared.ValidateRequest();
    var roomNode = prepared.Pipeline.Nodes.Single(n => n.Id == "room");
    if (
        roomNode.Inputs.ContainsKey("noise") != withNoise
        || prepared.Pipeline.Nodes.Count(n => n.Id == "noise-source") != (withNoise ? 1 : 0)
    )
        throw new IOException("Published RIR graph did not bind its optional noise source.");
    if (
        prepared.Envelope.Frames != prepared.EnvelopeSource.Audio.Frames
        || prepared.Envelope.Channels != 1
        || prepared.Graph.Records.Count(r => r.NodeId.StartsWith("envelope/")) != 5
    )
        throw new IOException(
            "Published graph did not execute the default envelope child processors."
        );
    await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    session.RateAll(true);
    session.End();
    Console.WriteLine(
        $"{corpus.Id}: RIR {(withNoise ? "with Babble -10 dB" : "without noise")} generated and SDL playback completed."
    );
}
