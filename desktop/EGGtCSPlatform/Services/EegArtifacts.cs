using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Services;

public sealed record EegArtifactResult(string EdfPath, string CsvPath, long SampleCount);

public interface IEegArtifactFinalizer
{
    Task<EegArtifactResult> FinalizeAsync(
        EegRecordingSummary summary,
        Func<CancellationToken, IAsyncEnumerable<EegRecordedSampleBatch>> sampleSource,
        CancellationToken cancellationToken = default,
        string? outputStem = null
    );
}

public interface IEegFileRegistry
{
    Task RegisterRawAsync(Guid runId, string path, CancellationToken cancellationToken = default);
    Task RegisterTemporaryAsync(
        Guid runId,
        EegArtifactResult result,
        CancellationToken cancellationToken = default
    );
    Task RegisterFailureAsync(
        Guid runId,
        string message,
        CancellationToken cancellationToken = default
    );
}

public sealed class EegFileRegistry(IDbContextFactory<AppDbContext> contextFactory)
    : IEegFileRegistry
{
    public async Task RegisterRawAsync(
        Guid runId,
        string path,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.ExperimentRuns.AnyAsync(x => x.Id == runId, cancellationToken))
            return;
        var fullPath = Path.GetFullPath(path);
        var existing = await db.EegFiles.SingleOrDefaultAsync(
            x =>
                x.ExperimentRunId == runId
                && x.Format == EegFileFormat.Staging
                && x.Location == EegFileLocation.Managed,
            cancellationToken
        );
        if (existing is null)
        {
            db.EegFiles.Add(
                new EegFileEntity
                {
                    ExperimentRunId = runId,
                    Format = EegFileFormat.Staging,
                    Location = EegFileLocation.Managed,
                    State = EegFileState.Available,
                    Path = fullPath,
                    SizeBytes = new FileInfo(fullPath).Length,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                }
            );
        }
        else
        {
            existing.Path = fullPath;
            existing.State = EegFileState.Available;
            existing.SizeBytes = new FileInfo(fullPath).Length;
            existing.CleanedAtUtc = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RegisterTemporaryAsync(
        Guid runId,
        EegArtifactResult result,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.ExperimentRuns.AnyAsync(x => x.Id == runId, cancellationToken))
            return;
        var now = DateTimeOffset.UtcNow;
        db.EegFiles.AddRange(
            Create(runId, EegFileFormat.EdfPlus, result.EdfPath, now),
            Create(runId, EegFileFormat.Csv, result.CsvPath, now)
        );
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RegisterFailureAsync(
        Guid runId,
        string message,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.ExperimentRuns.AnyAsync(x => x.Id == runId, cancellationToken))
            return;
        db.ExperimentIncidents.Add(
            new ExperimentIncidentEntity
            {
                ExperimentRunId = runId,
                Kind = ExperimentIncidentKind.FileFailure,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Source = "eeg-artifact-finalizer",
                Message = message,
            }
        );
        await db.SaveChangesAsync(cancellationToken);
    }

    private static EegFileEntity Create(
        Guid runId,
        EegFileFormat format,
        string path,
        DateTimeOffset now
    ) =>
        new()
        {
            ExperimentRunId = runId,
            Format = format,
            Location = EegFileLocation.Temporary,
            State = EegFileState.Available,
            Path = path,
            SizeBytes = new FileInfo(path).Length,
            CreatedAtUtc = now,
        };
}

public sealed class EegArtifactFinalizer : IEegArtifactFinalizer
{
    private static readonly UTF8Encoding CsvEncoding = new(encoderShouldEmitUTF8Identifier: true);

    public async Task<EegArtifactResult> FinalizeAsync(
        EegRecordingSummary summary,
        Func<CancellationToken, IAsyncEnumerable<EegRecordedSampleBatch>> sampleSource,
        CancellationToken cancellationToken = default,
        string? outputStem = null
    )
    {
        var stem =
            outputStem
            ?? Path.Combine(
                Path.GetDirectoryName(summary.FilePath)!,
                summary.RecordingId.ToString("N")
            );
        var csvPath = stem + ".csv";
        var edfPath = stem + ".edf";
        var csvPart = csvPath + ".part";
        var edfPart = edfPath + ".part";
        var channels = ResolveChannels(summary.Metadata);
        var minima = Enumerable.Repeat(double.PositiveInfinity, channels.Count).ToArray();
        var maxima = Enumerable.Repeat(double.NegativeInfinity, channels.Count).ToArray();
        long sampleCount = 0;

        try
        {
            await using (
                var stream = new FileStream(
                    csvPart,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough
                )
            )
            await using (var writer = new StreamWriter(stream, CsvEncoding))
            {
                await writer.WriteLineAsync(
                    "TimestampUtc,ElapsedSeconds,Cycle,Stage,"
                        + string.Join(',', channels.Select(x => EscapeCsv(x.Name)))
                );
                await foreach (var frame in ReadFrames(sampleSource, channels, cancellationToken))
                {
                    var values = new string[channels.Count];
                    for (var index = 0; index < channels.Count; index++)
                    {
                        var value = frame.Values[index];
                        if (value.HasValue)
                        {
                            minima[index] = Math.Min(minima[index], value.Value);
                            maxima[index] = Math.Max(maxima[index], value.Value);
                            values[index] = value.Value.ToString("R", CultureInfo.InvariantCulture);
                        }
                        else
                            values[index] = string.Empty;
                    }
                    var timestamp = summary.Metadata.StartedAtUtc.AddSeconds(frame.ElapsedSeconds);
                    await writer.WriteLineAsync(
                        $"{timestamp:O},{frame.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture)},"
                            + $"{frame.Cycle},{EscapeCsv(frame.Stage)},{string.Join(',', values)}"
                    );
                    sampleCount++;
                }
                await writer.FlushAsync(cancellationToken);
            }

            NormalizePhysicalRanges(minima, maxima);
            await WriteEdfAsync(
                edfPart,
                summary,
                channels,
                minima,
                maxima,
                sampleCount,
                sampleSource,
                cancellationToken
            );
            File.Move(csvPart, csvPath);
            File.Move(edfPart, edfPath);
            return new EegArtifactResult(edfPath, csvPath, sampleCount);
        }
        catch
        {
            TryDelete(csvPart);
            TryDelete(edfPart);
            TryDelete(csvPath);
            TryDelete(edfPath);
            throw;
        }
    }

    private static async Task WriteEdfAsync(
        string path,
        EegRecordingSummary summary,
        IReadOnlyList<ChannelDescriptor> channels,
        double[] minima,
        double[] maxima,
        long sampleCount,
        Func<CancellationToken, IAsyncEnumerable<EegRecordedSampleBatch>> sampleSource,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var signalCount = channels.Count + 1;
        WriteField(writer, "0", 8);
        WriteField(writer, summary.Metadata.SubjectId, 80);
        WriteField(writer, summary.Metadata.ExperimentId, 80);
        WriteField(
            writer,
            summary
                .Metadata.StartedAtUtc.ToLocalTime()
                .ToString("dd.MM.yy", CultureInfo.InvariantCulture),
            8
        );
        WriteField(
            writer,
            summary
                .Metadata.StartedAtUtc.ToLocalTime()
                .ToString("HH.mm.ss", CultureInfo.InvariantCulture),
            8
        );
        WriteField(writer, (256 + signalCount * 256).ToString(CultureInfo.InvariantCulture), 8);
        WriteField(writer, "EDF+D", 44);
        WriteField(writer, sampleCount.ToString(CultureInfo.InvariantCulture), 8);
        WriteField(
            writer,
            (1d / Math.Max(1, summary.Metadata.SampleRateHz)).ToString(
                "0.########",
                CultureInfo.InvariantCulture
            ),
            8
        );
        WriteField(writer, signalCount.ToString(CultureInfo.InvariantCulture), 4);

        WriteSignalFields(writer, channels.Select(x => x.Name).Append("EDF Annotations"), 16);
        WriteSignalFields(writer, Enumerable.Repeat(string.Empty, signalCount), 80);
        WriteSignalFields(writer, Enumerable.Repeat("uV", channels.Count).Append(string.Empty), 8);
        WriteSignalFields(writer, minima.Select(FormatNumber).Append("-1"), 8);
        WriteSignalFields(writer, maxima.Select(FormatNumber).Append("1"), 8);
        WriteSignalFields(writer, Enumerable.Repeat("-32768", signalCount), 8);
        WriteSignalFields(writer, Enumerable.Repeat("32767", signalCount), 8);
        WriteSignalFields(
            writer,
            Enumerable.Repeat("None", channels.Count).Append(string.Empty),
            80
        );
        WriteSignalFields(writer, Enumerable.Repeat("1", channels.Count).Append("32"), 8);
        WriteSignalFields(writer, Enumerable.Repeat(string.Empty, signalCount), 32);

        string? previousStage = null;
        var previousCycle = -1;
        var previousMissingChannels = string.Empty;
        await foreach (var frame in ReadFrames(sampleSource, channels, cancellationToken))
        {
            for (var index = 0; index < channels.Count; index++)
                writer.Write(Quantize(frame.Values[index] ?? 0d, minima[index], maxima[index]));
            var annotation = new byte[64];
            var onset =
                $"+{frame.ElapsedSeconds.ToString("0.######", CultureInfo.InvariantCulture)}";
            var changed =
                previousCycle != frame.Cycle
                || !string.Equals(previousStage, frame.Stage, StringComparison.Ordinal);
            var annotationText = changed ? $"{frame.Stage} cycle {frame.Cycle}" : string.Empty;
            var missingChannels = string.Join('|', frame.MissingChannels);
            if (!string.Equals(previousMissingChannels, missingChannels, StringComparison.Ordinal))
                annotationText +=
                    missingChannels.Length > 0 ? $" GAP:{missingChannels}" : " GAP END";
            WriteAnnotation(annotation, onset, annotationText.Trim());
            writer.Write(annotation);
            previousStage = frame.Stage;
            previousCycle = frame.Cycle;
            previousMissingChannels = missingChannels;
        }
        writer.Flush();
        await stream.FlushAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<DecodedFrame> ReadFrames(
        Func<CancellationToken, IAsyncEnumerable<EegRecordedSampleBatch>> sampleSource,
        IReadOnlyList<ChannelDescriptor> channels,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (
            var batch in sampleSource(cancellationToken).WithCancellation(cancellationToken)
        )
        {
            var recordedChannels = batch.Channels.ToDictionary(channel => channel.PhysicalChannel);
            for (var sample = 0; sample < batch.SampleCount; sample++)
            {
                var values = new double?[channels.Count];
                var missingChannels = new List<string>();
                for (var channelIndex = 0; channelIndex < channels.Count; channelIndex++)
                {
                    var physical = channels[channelIndex].PhysicalChannel;
                    if (
                        recordedChannels.TryGetValue(physical, out var recorded)
                        && sample < recorded.Samples.Count
                    )
                        values[channelIndex] = recorded.Samples[sample];
                    else
                        missingChannels.Add(channels[channelIndex].Name);
                }
                yield return new DecodedFrame(
                    batch.TimelineStartSeconds + sample * batch.SampleIntervalSeconds,
                    batch.CurrentCycle,
                    batch.AcquisitionStage,
                    values,
                    missingChannels
                );
            }
        }
    }

    private static IReadOnlyList<ChannelDescriptor> ResolveChannels(EegRecordingMetadata metadata)
    {
        if (metadata.PhysicalChannelNames is { Count: > 0 })
            return metadata
                .PhysicalChannelNames.OrderBy(x => x.Key)
                .Where(x => x.Key is >= 1 and <= 32 && metadata.ChannelIds.Contains(x.Value))
                .Select(x => new ChannelDescriptor(x.Key, x.Value))
                .ToArray();
        return metadata
            .ChannelIds.Take(32)
            .Select((name, index) => new ChannelDescriptor(index + 1, name))
            .ToArray();
    }

    private static void NormalizePhysicalRanges(double[] minima, double[] maxima)
    {
        for (var index = 0; index < minima.Length; index++)
        {
            if (!double.IsFinite(minima[index]))
            {
                minima[index] = -1;
                maxima[index] = 1;
            }
            else if (Math.Abs(maxima[index] - minima[index]) < 1e-12)
            {
                minima[index] -= 1;
                maxima[index] += 1;
            }
        }
    }

    private static short Quantize(double value, double minimum, double maximum)
    {
        var normalized = (value - minimum) / (maximum - minimum);
        return (short)Math.Clamp((int)Math.Round(-32768 + normalized * 65535), -32768, 32767);
    }

    private static void WriteAnnotation(byte[] destination, string onset, string text)
    {
        var prefix = $"{onset}\u0014";
        const string suffix = "\u0014\0";
        var availableTextLength = Math.Max(0, destination.Length - prefix.Length - suffix.Length);
        if (text.Length > availableTextLength)
            text = text[..availableTextLength];
        var encoded = Encoding.ASCII.GetBytes(prefix + text + suffix);
        encoded.AsSpan(0, Math.Min(encoded.Length, destination.Length)).CopyTo(destination);
    }

    private static void WriteSignalFields(
        BinaryWriter writer,
        IEnumerable<string> values,
        int width
    )
    {
        foreach (var value in values)
            WriteField(writer, value, width);
    }

    private static void WriteField(BinaryWriter writer, string value, int width)
    {
        var sanitized = new string(
            (value ?? string.Empty).Select(c => c is >= ' ' and <= '~' ? c : '_').ToArray()
        );
        var bytes = Encoding.ASCII.GetBytes(sanitized);
        writer.Write(bytes, 0, Math.Min(bytes.Length, width));
        if (bytes.Length < width)
            writer.Write(Enumerable.Repeat((byte)' ', width - bytes.Length).ToArray());
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch { }
    }

    private sealed record ChannelDescriptor(int PhysicalChannel, string Name);

    private sealed record DecodedFrame(
        double ElapsedSeconds,
        int Cycle,
        string Stage,
        double?[] Values,
        IReadOnlyList<string> MissingChannels
    );
}
