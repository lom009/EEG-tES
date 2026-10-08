using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EGGtCSPlatform.Services;

internal sealed class ApplicationSingleInstanceLease : IDisposable
{
    private Mutex? _mutex;
    private FileStream? _lockFile;
    private bool _ownsMutex;

    private ApplicationSingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    private ApplicationSingleInstanceLease(FileStream lockFile) => _lockFile = lockFile;

    internal static string CurrentUserMutexName
    {
        get
        {
            var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return $"EGGtCSPlatform.SingleInstance.{hash}";
        }
    }

    internal static string CurrentUserLockFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EGGtCSPlatform",
            "application.instance.lock"
        );

    internal static bool TryAcquireCurrentUser(out ApplicationSingleInstanceLease? lease) =>
        OperatingSystem.IsWindows()
            ? TryAcquireMutex(CurrentUserMutexName, out lease)
            : TryAcquireFile(CurrentUserLockFilePath, out lease);

    internal static bool TryAcquireMutex(
        string mutexName,
        out ApplicationSingleInstanceLease? lease
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                lease = null;
                return false;
            }

            lease = new ApplicationSingleInstanceLease(mutex);
            return true;
        }
        finally
        {
            if (!acquired)
                mutex.Dispose();
        }
    }

    internal static bool TryAcquireFile(
        string lockFilePath,
        out ApplicationSingleInstanceLease? lease
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        var fullPath = Path.GetFullPath(lockFilePath);
        var directory =
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定单例锁文件所在目录。");
        Directory.CreateDirectory(directory);

        try
        {
            var lockFile = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
            lease = new ApplicationSingleInstanceLease(lockFile);
            return true;
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            lease = null;
            return false;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lockFile, null)?.Dispose();

        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
            return;

        if (_ownsMutex)
        {
            _ownsMutex = false;
            mutex.ReleaseMutex();
        }
        mutex.Dispose();
    }
}
