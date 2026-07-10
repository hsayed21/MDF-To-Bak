using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace MdfToBakConverter;

public sealed record ConversionRequest(int EngineYear, string MdfPath, string? LdfPath, string BakPath);
public sealed record InstallProgress(string Stage, long BytesReceived = 0, long? TotalBytes = null);

public sealed class LocalDbConverter
{
    private static readonly IReadOnlyDictionary<int, string> LocalDbVersions = new Dictionary<int, string>
    {
        [2012] = "11.0", [2014] = "12.0", [2016] = "13.0", [2019] = "15.0", [2022] = "16.0"
    };
    // Standalone x64 LocalDB packages hosted on Microsoft's download CDN.
    private static readonly IReadOnlyDictionary<int, InstallerDownload> InstallerDownloads = new Dictionary<int, InstallerDownload>
    {
        [2012] = new(
            new Uri("https://download.microsoft.com/download/F/6/7/F673709C-D371-4A64-8BF9-C1DD73F60990/ENU/x64/SqlLocalDB.msi"),
            new Uri("https://download.microsoft.com/download/F/6/7/F673709C-D371-4A64-8BF9-C1DD73F60990/ENU/x86/SqlLocalDB.msi")),
        [2014] = new(
            new Uri("https://download.microsoft.com/download/2/A/5/2A5260C3-4143-47D8-9823-E91BB0121F94/ENU/x64/SqlLocalDB.msi"),
            new Uri("https://download.microsoft.com/download/2/A/5/2A5260C3-4143-47D8-9823-E91BB0121F94/ENU/x86/SqlLocalDB.msi")),
        [2016] = new(new Uri("https://download.microsoft.com/download/E/1/2/E12B3655-D817-49BA-B934-CEB9DAC0BAF3/SqlLocalDB.msi"), null),
        [2019] = new(new Uri("https://download.microsoft.com/download/7/c/1/7c14e92e-bdcb-4f89-b7cf-93543e7112d1/SqlLocalDB.msi"), null),
        [2022] = new(new Uri("https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SqlLocalDB.msi"), null)
    };
    private readonly Action<string> _log;

    public LocalDbConverter(Action<string> log) => _log = log;

    public string OperatingSystemArchitecture => Environment.Is64BitOperatingSystem ? "x64" : "x86";

    public bool CanInstallOnCurrentArchitecture(int year) =>
        Environment.Is64BitOperatingSystem || InstallerDownloads[year].X86 is not null;

    public string GetArchitectureRequirement(int year) =>
        $"SQL Server {year} LocalDB is not installed. Microsoft provides the configured package only for x64 Windows; select SQL Server 2012 or 2014 for this x86 computer.";

    public async Task<bool> IsVersionInstalledAsync(int year, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("SqlLocalDB.exe", new[] { "versions" }, cancellationToken, throwOnError: false, logOutput: false);
        if (result.ExitCode != 0)
        {
            _log("SqlLocalDB.exe was not found or returned an error.");
            return false;
        }
        var installed = ParseInstalledLocalDbVersions(result.Output).ToArray();
        var selectedVersion = LocalDbVersions[year];
        var isInstalled = installed.Any(x => x.Version.StartsWith(selectedVersion + ".", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(x.Version, selectedVersion, StringComparison.OrdinalIgnoreCase));
        var found = installed.Length == 0
            ? "none"
            : string.Join(", ", installed.Select(x => $"SQL Server {x.Year?.ToString() ?? "unknown"} ({x.Version})"));
        _log($"LocalDB check — selected SQL Server {year} ({selectedVersion}): {(isInstalled ? "installed" : "not installed")}. Detected: {found}.");
        return isInstalled;
    }

    private static IEnumerable<InstalledLocalDbVersion> ParseInstalledLocalDbVersions(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"\((?<version>\d+\.\d+(?:\.\d+){0,2})\)|^\s*(?<bare>\d+\.\d+)\s*$");
            if (!match.Success) continue;
            var version = match.Groups["version"].Success ? match.Groups["version"].Value : match.Groups["bare"].Value;
            var majorVersion = string.Join('.', version.Split('.').Take(2));
            var year = majorVersion switch
            {
                "11.0" => 2012, "12.0" => 2014, "13.0" => 2016, "15.0" => 2019,
                "16.0" => 2022, "17.0" => 2025, _ => (int?)null
            };
            yield return new InstalledLocalDbVersion(version, year);
        }
    }

    public async Task ConvertAsync(ConversionRequest request, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "MdfToBakConverter", Guid.NewGuid().ToString("N"));
        var instanceName = "MdfToBak_" + Guid.NewGuid().ToString("N")[..12];
        var databaseName = "MdfToBak_" + Guid.NewGuid().ToString("N")[..12];
        var instanceCreated = false;
        var attached = false;
        string? connectionString = null;

        try
        {
            Directory.CreateDirectory(tempRoot);
            _log($"Working directory: {tempRoot}");
            if (!await IsVersionInstalledAsync(request.EngineYear, cancellationToken))
                throw new InvalidOperationException($"SQL Server {request.EngineYear} LocalDB is not installed. Click Install, wait for it to finish, then start conversion.");

            cancellationToken.ThrowIfCancellationRequested();
            _log($"Creating temporary LocalDB instance {instanceName}.");
            await RunProcessAsync("SqlLocalDB.exe", new[] { "create", instanceName, LocalDbVersions[request.EngineYear] }, cancellationToken);
            instanceCreated = true;
            await RunProcessAsync("SqlLocalDB.exe", new[] { "start", instanceName }, cancellationToken);
            connectionString = $"Server=(localdb)\\{instanceName};Database=master;Integrated Security=true;Encrypt=False;TrustServerCertificate=True;Connect Timeout=30";

            var copiedMdf = Path.Combine(tempRoot, "database.mdf");
            var copiedLdf = request.LdfPath is null ? null : Path.Combine(tempRoot, "database.ldf");
            _log("Copying database files to the private working directory.");
            File.Copy(request.MdfPath, copiedMdf, overwrite: true);
            if (request.LdfPath is not null) File.Copy(request.LdfPath, copiedLdf!, overwrite: true);

            await using (var connection = new SqlConnection(connectionString))
            {
                _log("Attaching copied database files.");
                await connection.OpenAsync(cancellationToken);
                var attach = copiedLdf is null
                    ? $"CREATE DATABASE {QuoteIdentifier(databaseName)} ON (FILENAME = N'{EscapeSql(copiedMdf)}') FOR ATTACH_REBUILD_LOG;"
                    : $"CREATE DATABASE {QuoteIdentifier(databaseName)} ON (FILENAME = N'{EscapeSql(copiedMdf)}'), (FILENAME = N'{EscapeSql(copiedLdf)}') FOR ATTACH;";
                await ExecuteAsync(connection, attach, cancellationToken);
                attached = true;

                _log("Creating SQL Server backup with checksum.");
                await ExecuteAsync(connection, $"BACKUP DATABASE {QuoteIdentifier(databaseName)} TO DISK = N'{EscapeSql(request.BakPath)}' WITH INIT, CHECKSUM, STATS = 10;", cancellationToken, timeoutSeconds: 0);
                if (!File.Exists(request.BakPath) || new FileInfo(request.BakPath).Length == 0)
                    throw new IOException("SQL Server reported success, but no usable BAK file was created.");

                _log("Verifying backup integrity.");
                await ExecuteAsync(connection, $"RESTORE VERIFYONLY FROM DISK = N'{EscapeSql(request.BakPath)}' WITH CHECKSUM;", cancellationToken, timeoutSeconds: 0);
            }
        }
        finally
        {
            if (attached && connectionString is not null)
            {
                try
                {
                    _log("Detaching temporary database.");
                    await using var cleanupConnection = new SqlConnection(connectionString);
                    await cleanupConnection.OpenAsync(CancellationToken.None);
                    await ExecuteAsync(cleanupConnection, $"ALTER DATABASE {QuoteIdentifier(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC master.dbo.sp_detach_db @dbname = N'{EscapeSql(databaseName)}';", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log($"Cleanup warning: could not detach {databaseName}: {ex.Message}");
                    _log($"Manual cleanup: SqlLocalDB stop {instanceName}; SqlLocalDB delete {instanceName}");
                }
            }
            if (instanceCreated)
            {
                try { await RunProcessAsync("SqlLocalDB.exe", new[] { "stop", instanceName }, CancellationToken.None, throwOnError: false); } catch { }
                try { await RunProcessAsync("SqlLocalDB.exe", new[] { "delete", instanceName }, CancellationToken.None, throwOnError: false); } catch { }
            }
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch (Exception ex) { _log($"Cleanup warning: could not remove {tempRoot}: {ex.Message}"); }
        }
    }

    public async Task InstallLocalDbAsync(int year, CancellationToken cancellationToken, IProgress<InstallProgress>? progress = null)
    {
        if (!CanInstallOnCurrentArchitecture(year))
            throw new PlatformNotSupportedException(GetArchitectureRequirement(year));
        var architecture = OperatingSystemArchitecture;
        var installerDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdfToBakConverter", "Installers");
        Directory.CreateDirectory(installerDirectory);
        var msi = Path.Combine(installerDirectory, $"SqlLocalDB-{year}-{architecture}.msi");
        if (!File.Exists(msi)) await DownloadInstallerAsync(year, architecture, msi, cancellationToken, progress);
        VerifyMicrosoftSignature(msi);
        var installLog = Path.Combine(installerDirectory, $"SqlLocalDB-{year}-install.log");
        _log($"Installing SQL Server {year} LocalDB silently.");
        progress?.Report(new InstallProgress($"Installing SQL Server {year} LocalDB"));
        var result = await RunProcessAsync("msiexec.exe", new[] { "/i", msi, "/qn", "/norestart", "IACCEPTSQLLOCALDBLICENSETERMS=YES", "/L*v", installLog }, cancellationToken, throwOnError: false);
        _log($"LocalDB installer exit code: {result.ExitCode}. Installer log: {installLog}");
        if (result.ExitCode is not (0 or 3010)) throw new InvalidOperationException($"LocalDB installer failed with exit code {result.ExitCode}. See {installLog}.");
    }

    private async Task DownloadInstallerAsync(int year, string architecture, string destination, CancellationToken cancellationToken, IProgress<InstallProgress>? progress)
    {
        var download = InstallerDownloads[year];
        var url = architecture == "x64" ? download.X64 : download.X86!;
        // A unique temporary name prevents collisions with another running app instance.
        var partialPath = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        _log($"Downloading SQL Server {year} LocalDB ({architecture}) from {url}.");
        progress?.Report(new InstallProgress($"Downloading SQL Server {year} LocalDB ({architecture})"));
        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(partialPath);
                var buffer = new byte[1024 * 128];
                long received = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    progress?.Report(new InstallProgress($"Downloading SQL Server {year} LocalDB ({architecture})", received, total));
                }
                await target.FlushAsync(cancellationToken);
                _log($"Download stream completed: {received:N0} bytes.");
            }

            // The target stream above has been disposed before renaming. A cached file from a
            // concurrent app instance is safe to reuse after signature validation.
            try
            {
                File.Move(partialPath, destination, overwrite: false);
                _log($"Downloaded installer saved to {destination}.");
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(partialPath);
                _log($"Another process already cached the installer at {destination}; reusing it.");
            }
        }
        catch
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            throw;
        }
    }

    private static void VerifyMicrosoftSignature(string path)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromSignedFile(path);
            if (!certificate.Subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The downloaded installer is not signed by Microsoft. Signer: {certificate.Subject}");
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The downloaded installer does not have a verifiable Authenticode signature.", ex);
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken, int timeoutSeconds = 60)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        using var registration = cancellationToken.Register(command.Cancel);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken, bool throwOnError = true, bool logOutput = true)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        _log($"> {fileName} {string.Join(" ", arguments.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            if (throwOnError) throw new InvalidOperationException("SqlLocalDB.exe could not be started. Install the selected LocalDB version or add SqlLocalDB.exe to PATH.", ex);
            return new ProcessResult(-1, ex.Message, "");
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var result = new ProcessResult(process.ExitCode, await stdout, await stderr);
        if (logOutput && !string.IsNullOrWhiteSpace(result.Output)) _log(result.Output.Trim());
        if (logOutput && !string.IsNullOrWhiteSpace(result.Error)) _log(result.Error.Trim());
        if (throwOnError && result.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed with exit code {result.ExitCode}: {result.Error.Trim()}");
        return result;
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");
    private static string QuoteIdentifier(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private sealed record InstalledLocalDbVersion(string Version, int? Year);
    private sealed record InstallerDownload(Uri X64, Uri? X86);
}

public sealed record MdfVersion(int? Year, int? InternalVersion, int? CreateVersion)
{
    public static MdfVersion Unknown { get; } = new(null, null, null);
}

public static class MdfVersionDetector
{
    private const int PageSize = 8192;
    private const int BootPageNumber = 9;
    private const int PageHeaderSize = 96;
    private const int DbiVersionOffset = 4;
    private const int DbiCreateVersionOffset = 6;
    private const long DbiVersionFileOffset = (long)BootPageNumber * PageSize + PageHeaderSize + DbiVersionOffset;
    private const long DbiCreateVersionFileOffset = (long)BootPageNumber * PageSize + PageHeaderSize + DbiCreateVersionOffset;
    private static readonly IReadOnlyDictionary<int, int> VersionYears = new Dictionary<int, int>
    {
        [539] = 2000, [611] = 2005, [612] = 2005, [655] = 2008, [661] = 2008, [706] = 2012,
        [782] = 2014, [852] = 2016, [869] = 2017, [904] = 2019, [957] = 2022
    };

    public static MdfVersion Detect(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < DbiCreateVersionFileOffset + sizeof(ushort)) return MdfVersion.Unknown;

            // The boot page is page 9. Its fixed dbi record starts after SQL Server's 96-byte
            // page header; dbi_version and dbi_createVersion are little-endian UINT16 fields.
            stream.Position = DbiVersionFileOffset;
            Span<byte> bytes = stackalloc byte[sizeof(ushort) * 2];
            stream.ReadExactly(bytes);
            var version = BitConverter.ToUInt16(bytes[..sizeof(ushort)]);
            var createVersion = BitConverter.ToUInt16(bytes[sizeof(ushort)..]);
            return VersionYears.TryGetValue(version, out var year)
                ? new MdfVersion(year, version, createVersion)
                : new MdfVersion(null, version, createVersion);
        }
        catch { return MdfVersion.Unknown; }
    }
}
