using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EGGtCSPlatform.Services;

// Background consumers only. The mutex also protects early startup of a second process.
internal sealed class DatedLogWriter(string directory, TimeProvider timeProvider)
{
    private static readonly HashSet<string> Streams = new(StringComparer.Ordinal)
    {
        "all",
        "trace",
        "debug",
        "info",
        "warning",
        "error",
        "fatal",
        "bytes",
    };
    private readonly HashSet<string> _failedPaths = new(StringComparer.Ordinal);
    private readonly string _mutexName =
        "EGGtCSPlatform.Logs."
        + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant()))
        );
    private DateOnly? _lastCleanup;
    private int _lastRetention;
    private SerialLogOptions? _retentionOptions;

    public void ConfigureRetention(SerialLogOptions options) =>
        Volatile.Write(ref _retentionOptions, options);

    public void Maintain() => Guard(Cleanup);

    public void Append(
        string stream,
        DateTimeOffset timestamp,
        string text,
        SerialLogOptions options
    )
    {
        var day = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(timestamp, timeProvider.LocalTimeZone).DateTime
        );
        var path = Path.Combine(
            directory,
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        );
        var failureKey = Path.Combine(path, stream);
        Guard(() =>
        {
            Cleanup();
            if (_failedPaths.Contains(failureKey))
                return;
            try
            {
                Directory.CreateDirectory(path);
                var index = Directory
                    .EnumerateFiles(path, stream + ".*.log")
                    .Select(file => ParseIndex(Path.GetFileName(file), stream))
                    .DefaultIfEmpty(0)
                    .Max();
                if (index == 0)
                    index = 1;
                var filePath = Path.Combine(path, $"{stream}.{index}.log");
                var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
                if (
                    File.Exists(filePath)
                    && new FileInfo(filePath).Length is var length
                    && length > 0
                    && (
                        length >= options.MaxFileSizeBytes
                        || bytes.LongLength > options.MaxFileSizeBytes - length
                    )
                )
                    filePath = Path.Combine(path, $"{stream}.{checked(index + 1)}.log");
                using var file = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read
                );
                file.Write(bytes);
                file.Flush();
            }
            catch (Exception exception)
            {
                _failedPaths.Add(failureKey);
                Debug.WriteLine($"Log output disabled for {failureKey}: {exception}");
            }
        });
    }

    private static int ParseIndex(string fileName, string stream)
    {
        var prefix = stream + ".";
        return
            fileName.StartsWith(prefix, StringComparison.Ordinal)
            && fileName.EndsWith(".log", StringComparison.Ordinal)
            && fileName.Length > prefix.Length + 4
            && int.TryParse(
                fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - 4),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index
            )
            && index > 0
            ? index
            : 0;
    }

    private void Cleanup()
    {
        var options = Volatile.Read(ref _retentionOptions);
        if (options is null || !options.Enabled)
            return;
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        if (_lastCleanup == today && _lastRetention == options.RetentionDays)
            return;
        _lastCleanup = today;
        _lastRetention = options.RetentionDays;
        _failedPaths.Clear();
        try
        {
            if (!Directory.Exists(directory))
                return;
            var oldest = today.AddDays(-options.RetentionDays + 1);
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0
                    || !DateOnly.TryParseExact(
                        Path.GetFileName(child),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var day
                    )
                    || day >= oldest
                )
                    continue;
                foreach (var file in Directory.EnumerateFiles(child, "*.log"))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (Streams.Any(stream => ParseIndex(Path.GetFileName(file), stream) > 0))
                        File.Delete(file);
                }
                if (!Directory.EnumerateFileSystemEntries(child).Any())
                    Directory.Delete(child);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Log retention cleanup failed: {exception}");
        }
    }

    private void Guard(Action action)
    {
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (acquired)
                    action();
                else
                    Debug.WriteLine("Log write skipped: log lock timeout.");
            }
            finally
            {
                if (acquired)
                    mutex.ReleaseMutex();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Log writer failed: {exception}");
        }
    }
}
