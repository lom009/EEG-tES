using System.Diagnostics;
using System.Reflection;

namespace EGGtCSPlatform.WindowsSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallLocationForm());
    }
}

internal sealed class InstallLocationForm : Form
{
    private const string EmbeddedSetupResourceName = "EGGtCSPlatform.EmbeddedVelopackSetup.exe";
    private readonly TextBox _installPathTextBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _installButton = new();
    private readonly Button _cancelButton = new();
    private readonly Label _statusLabel = new();

    public InstallLocationForm()
    {
        var displayVersion = GetDisplayVersion();
        Text = $"EGGtCSPlatform v{displayVersion} 安装";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 245);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            Location = new Point(28, 24),
            Text = $"安装 EGGtCSPlatform v{displayVersion}",
        };
        var descriptionLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Location = new Point(31, 65),
            Text = "请选择程序安装目录。用户配置和数据仍保存在 AppData，不会放入安装目录。",
        };
        var locationLabel = new Label
        {
            AutoSize = true,
            Location = new Point(31, 101),
            Text = "安装位置",
        };

        _installPathTextBox.Location = new Point(34, 126);
        _installPathTextBox.Size = new Size(465, 27);
        _installPathTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EGGtCSPlatform.Desktop"
        );

        _browseButton.Location = new Point(508, 124);
        _browseButton.Size = new Size(82, 31);
        _browseButton.Text = "浏览...";
        _browseButton.Click += BrowseButton_Click;

        _statusLabel.AutoEllipsis = true;
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Location = new Point(34, 164);
        _statusLabel.Size = new Size(350, 40);

        _installButton.Location = new Point(408, 184);
        _installButton.Size = new Size(88, 34);
        _installButton.Text = "立即安装";
        _installButton.Click += InstallButton_Click;

        _cancelButton.Location = new Point(502, 184);
        _cancelButton.Size = new Size(88, 34);
        _cancelButton.Text = "取消";
        _cancelButton.Click += (_, _) => Close();

        AcceptButton = _installButton;
        CancelButton = _cancelButton;
        Controls.AddRange([
            titleLabel,
            descriptionLabel,
            locationLabel,
            _installPathTextBox,
            _browseButton,
            _statusLabel,
            _installButton,
            _cancelButton,
        ]);
    }

    private static string GetDisplayVersion()
    {
        var informationalVersion = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            // Source revision metadata is useful for diagnostics but should not be
            // presented as part of the release version in the installer UI.
            return informationalVersion.Split('+', 2)[0];
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知版本";
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 EGGtCSPlatform 的安装目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = GetExistingParentDirectory(_installPathTextBox.Text),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void InstallButton_Click(object? sender, EventArgs e)
    {
        if (!TryNormalizeInstallPath(_installPathTextBox.Text, out var installPath, out var error))
        {
            MessageBox.Show(
                this,
                error,
                "安装位置无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            return;
        }

        SetBusy(true, "正在启动安装程序…");
        string? temporaryDirectory = null;
        try
        {
            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "EGGtCSPlatform",
                "Setup",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(temporaryDirectory);
            var setupPath = Path.Combine(
                temporaryDirectory,
                "EGGtCSPlatform.Desktop-win-Setup.exe"
            );
            ExtractEmbeddedSetup(setupPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true,
                WorkingDirectory = temporaryDirectory,
            };
            startInfo.ArgumentList.Add("--installto");
            startInfo.ArgumentList.Add(installPath);
            if (RequiresElevation(installPath))
            {
                startInfo.Verb = "runas";
            }

            using var process =
                Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows 未能启动 Velopack 安装程序。");
            SetBusy(true, "正在安装，请稍候…");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Velopack 安装程序返回错误代码 {process.ExitCode}。"
                );
            }

            Close();
        }
        catch (Exception exception)
        {
            SetBusy(false, "安装未完成，请检查目录权限后重试。");
            MessageBox.Show(
                this,
                $"无法完成安装：{exception.Message}",
                "安装失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }

    private static void ExtractEmbeddedSetup(string destinationPath)
    {
        using var source =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedSetupResourceName)
            ?? throw new InvalidOperationException(
                "安装包不完整，缺少内置 Velopack 安装程序。请重新下载安装包。"
            );
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.WriteThrough
        );
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private void SetBusy(bool isBusy, string status)
    {
        _installPathTextBox.Enabled = !isBusy;
        _browseButton.Enabled = !isBusy;
        _installButton.Enabled = !isBusy;
        _cancelButton.Enabled = !isBusy;
        ControlBox = !isBusy;
        _statusLabel.Text = status;
    }

    private static bool TryNormalizeInstallPath(
        string value,
        out string normalizedPath,
        out string error
    )
    {
        normalizedPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请选择安装目录。";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
            if (!Path.IsPathFullyQualified(normalizedPath))
            {
                error = "安装目录必须是完整路径。";
                return false;
            }

            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"安装目录无效：{exception.Message}";
            return false;
        }
    }

    private static string GetExistingParentDirectory(string value)
    {
        try
        {
            var candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
            while (!Directory.Exists(candidate))
            {
                candidate = Path.GetDirectoryName(candidate) ?? string.Empty;
                if (candidate.Length == 0)
                {
                    break;
                }
            }

            return candidate;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    private static bool RequiresElevation(string path)
    {
        return IsPathWithin(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsPathWithin(
                path,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            )
            || IsPathWithin(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    private static bool IsPathWithin(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        var normalizedParent =
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath =
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The operating system will eventually clean its temporary directory.
        }
    }
}
