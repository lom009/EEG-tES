using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Services;

/// <summary>Versioned session snapshots; atomic replacement prevents partially saved trial records.</summary>
public sealed class EnvelopeTrainingResultStore(string? directory = null) : IEnvelopeTrainingResultStore
{
    private readonly string _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EGGtCSPlatform", "EnvelopeTraining");
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    public async Task SaveAsync(EnvelopeTrainingSession session, CancellationToken token = default)
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, $"{session.SessionId:N}.json");
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(session, JsonOptions), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
