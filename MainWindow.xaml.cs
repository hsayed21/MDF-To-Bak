using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MdfToBakConverter;

public partial class MainWindow : Window
{
    private readonly LocalDbConverter _converter;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        _converter = new LocalDbConverter(Log);
        Loaded += async (_, _) => await RefreshInstallationAsync();
    }

    private int SelectedYear => int.Parse(((RadioButton)new[] { Sql2012, Sql2014, Sql2016, Sql2019, Sql2022 }
        .Single(x => x.IsChecked == true)).Tag.ToString()!);

    private async void EngineChanged(object sender, RoutedEventArgs e) => await RefreshInstallationAsync();

    private async Task RefreshInstallationAsync()
    {
        if (!IsLoaded || _isRunning) return;
        InstallationStatus.Text = "Checking LocalDB installation…";
        try
        {
            var installed = await _converter.IsVersionInstalledAsync(SelectedYear, CancellationToken.None);
            if (installed)
            {
                InstallationStatus.Text = $"SQL Server {SelectedYear} LocalDB is installed ({_converter.OperatingSystemArchitecture} Windows).";
                InstallButton.Visibility = Visibility.Collapsed;
            }
            else if (_converter.CanInstallOnCurrentArchitecture(SelectedYear))
            {
                InstallationStatus.Text = $"SQL Server {SelectedYear} LocalDB is not installed ({_converter.OperatingSystemArchitecture} Windows).";
                InstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                InstallationStatus.Text = _converter.GetArchitectureRequirement(SelectedYear);
                InstallButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            InstallationStatus.Text = "Unable to determine LocalDB installation status.";
            InstallButton.Visibility = Visibility.Collapsed;
            Log($"LocalDB detection failed: {ex.Message}");
        }
    }

    private void BrowseMdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SQL Server database (*.mdf)|*.mdf|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) MdfPath.Text = dialog.FileName;
    }

    private void BrowseLdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SQL Server log (*.ldf)|*.ldf|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) LdfPath.Text = dialog.FileName;
    }

    private void BrowseBak_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "SQL Server backup (*.bak)|*.bak", DefaultExt = ".bak", AddExtension = true, OverwritePrompt = false };
        if (dialog.ShowDialog(this) == true) BakPath.Text = dialog.FileName;
    }

    private void PathChanged(object sender, TextChangedEventArgs e)
    {
        ConvertButton.IsEnabled = !_isRunning && File.Exists(MdfPath.Text) && !string.IsNullOrWhiteSpace(BakPath.Text);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _isRunning = true;
        _cancellation = new CancellationTokenSource();
        ConvertButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.IsIndeterminate = true;
        try
        {
            var progress = new Progress<InstallProgress>(UpdateInstallProgress);
            OperationStatus.Text = $"Installing SQL Server {SelectedYear} LocalDB…";
            await _converter.InstallLocalDbAsync(SelectedYear, _cancellation.Token, progress);
            if (!await _converter.IsVersionInstalledAsync(SelectedYear, CancellationToken.None))
                throw new InvalidOperationException("The installer finished, but the selected LocalDB version was not detected.");
            InstallationStatus.Text = $"SQL Server {SelectedYear} LocalDB is installed ({_converter.OperatingSystemArchitecture} Windows).";
            InstallButton.Visibility = Visibility.Collapsed;
            OperationStatus.Text = "LocalDB installation completed.";
            MessageBox.Show(this, $"SQL Server {SelectedYear} LocalDB is ready.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Installation canceled.";
            Log("LocalDB installation canceled by user.");
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "LocalDB installation failed. See technical log.";
            Log($"INSTALL ERROR: {ex}");
            MessageBox.Show(this, ex.Message, "LocalDB installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _isRunning = false;
            CancelButton.IsEnabled = false;
            Progress.IsIndeterminate = false;
            PathChanged(this, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
            await RefreshInstallationAsync();
        }
    }

    private void UpdateInstallProgress(InstallProgress progress)
    {
        if (progress.TotalBytes is long total && total > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Minimum = 0;
            Progress.Maximum = total;
            Progress.Value = Math.Min(progress.BytesReceived, total);
            InstallationStatus.Text = $"{progress.Stage}: {progress.BytesReceived / 1024d / 1024d:F1} MB of {total / 1024d / 1024d:F1} MB";
        }
        else
        {
            Progress.IsIndeterminate = true;
            InstallationStatus.Text = progress.Stage;
        }
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(MdfPath.Text)) return;
        if (!string.IsNullOrWhiteSpace(LdfPath.Text) && !File.Exists(LdfPath.Text))
        {
            MessageBox.Show(this, "The selected LDF file does not exist.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(Path.GetExtension(BakPath.Text), ".bak", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The output file must have a .bak extension.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (File.Exists(BakPath.Text) && MessageBox.Show(this, "The output BAK already exists. Replace it?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var mdfVersion = MdfVersionDetector.Detect(MdfPath.Text);
        Log(mdfVersion.Year is int detectedYear
            ? $"MDF boot-page version detected: {mdfVersion.InternalVersion} (SQL Server {detectedYear}); created-version: {mdfVersion.CreateVersion?.ToString() ?? "unknown"}."
            : $"MDF boot-page version is not mapped: {mdfVersion.InternalVersion?.ToString() ?? "unreadable"}; created-version: {mdfVersion.CreateVersion?.ToString() ?? "unreadable"}.");
        if (mdfVersion.Year is int sourceYear && sourceYear > SelectedYear)
        {
            MessageBox.Show(this, $"This MDF was created by SQL Server {sourceYear}. SQL Server {SelectedYear} LocalDB cannot open it. Select a newer version.", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (mdfVersion.Year is int olderYear && olderYear != SelectedYear &&
            MessageBox.Show(this, $"This MDF appears to be from SQL Server {olderYear}. You selected SQL Server {SelectedYear}. The database may be upgraded when attached. Continue?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (mdfVersion.Year is null &&
            MessageBox.Show(this, "The MDF version could not be identified. The program will attempt a safe attach using the selected engine. Continue?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        _isRunning = true;
        _cancellation = new CancellationTokenSource();
        ConvertButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.IsIndeterminate = true;
        try
        {
            OperationStatus.Text = "Preparing LocalDB…";
            await _converter.ConvertAsync(new ConversionRequest(SelectedYear, MdfPath.Text, string.IsNullOrWhiteSpace(LdfPath.Text) ? null : LdfPath.Text, BakPath.Text), _cancellation.Token);
            OperationStatus.Text = "Conversion completed successfully.";
            MessageBox.Show(this, $"Backup verified and saved to:\n{BakPath.Text}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Canceled. Cleanup was attempted; see log for details.";
            Log("Operation canceled by user.");
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Conversion failed. See technical log.";
            Log($"ERROR: {ex}");
            MessageBox.Show(this, ex.Message, "Conversion failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _isRunning = false;
            CancelButton.IsEnabled = false;
            Progress.IsIndeterminate = false;
            PathChanged(this, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
            await RefreshInstallationAsync();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        OperationStatus.Text = "Canceling safely…";
        _cancellation?.Cancel();
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }
}
