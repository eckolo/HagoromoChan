using System.IO;
using System.Windows;

namespace HagoromoChan.Desktop;

public partial class App : Application
{
    private FileStream? _instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var configuredDirectory = Environment.GetEnvironmentVariable("HAGOROMO_DATA_DIR");
            var dataDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HagoromoChan")
                : Path.GetFullPath(configuredDirectory);

            Directory.CreateDirectory(dataDirectory);
            _instanceLock = new FileStream(Path.Combine(dataDirectory, "desktop.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            MainWindow = new MainWindow(dataDirectory);
            MainWindow.Show();
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        {
            MessageBox.Show("羽衣ちゃんはすでに起動しています。", "羽衣ちゃん",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"保存先を開けません。\n{error.Message}", "羽衣ちゃん",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
