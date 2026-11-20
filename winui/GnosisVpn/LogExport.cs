using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GnosisVpn;

/// <summary>Settings > Logs > Export: one zip in the user's Downloads folder with
///   app/ui.log                   the app's log (%LOCALAPPDATA%\GnosisVPN)
///   app/status.json              the service's last status as the app saw it
///   app/about.txt                app version, Windows version, time
///   install/install.log          the installer's log, when there is one
///   service/gvpndows*.log        the service's log, which carries the HOPR node's (core's) log too
/// The service log lives in C:\ProgramData\gvpndows, readable only by SYSTEM and administrators, so
/// the app (unelevated) writes the zip first and then starts itself elevated (one UAC prompt) as
/// `GnosisVPN.exe --export-logs ZIP` to add those files. Declining the prompt leaves a zip
/// without them. Nothing from C:\ProgramData\GnosisVPN (the node identity and its password) is read.</summary>
public static class LogExport
{
    const string SERVICE_LOGS = @"gvpndows";            // under %ProgramData%
    static readonly string[] SERVICE_FILES = { "gvpndows.log", "gvpndows.old.log" };

    /// <summary>Opens a file other processes keep writing to (the logs are open for appending).</summary>
    static Stream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static void Add(ZipArchive zip, string entry, string path)
    {
        if (!File.Exists(path)) return;
        using var src = OpenShared(path);
        using var dst = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
        src.CopyTo(dst);
    }
    static void AddText(ZipArchive zip, string entry, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open());
        w.Write(text);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);
    static string Downloads()
    {
        try { if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0 && Directory.Exists(p)) return p; } catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Writes the zip and returns its path and whether the service's log is in it.</summary>
    public static async Task<(string path, bool service, string? why)> Run(string? status, string version)
    {
        var path = Path.Combine(Downloads(), $"GnosisVPN-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            Add(zip, "app/ui.log", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GnosisVPN", "ui.log"));
            if (status != null) AddText(zip, "app/status.json", status);
            AddText(zip, "app/about.txt", $"Gnosis VPN for Windows {version} (prototype)\r\nWindows {Environment.OSVersion.Version}\r\nExported {DateTimeOffset.Now:u}\r\n");
            Add(zip, "install/install.log", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gnosis VPN Service", "setup", "install.log"));
        });
        // the service's log: needs an administrator
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GnosisVPN.exe");
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", ArgumentList = { "--export-logs", path } })!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? (path, true, null) : (path, false, $"adding the service log failed (exit {p.ExitCode})");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return (path, false, "the administrator prompt was declined"); }
        catch (Exception e) { return (path, false, e.Message); }
    }

    /// <summary>`GnosisVPN.exe --export-logs ZIP` (elevated, before WinUI starts): adds the service's log
    /// files to that zip. Only an existing GnosisVPN-logs-*.zip that is not a link is touched.</summary>
    public static int RunElevated(string[] args)
    {
        var i = Array.IndexOf(args, "--export-logs");
        if (i < 0 || i + 1 >= args.Length) return 2;
        var zipPath = Path.GetFullPath(args[i + 1]);
        if (!Path.GetFileName(zipPath).StartsWith("GnosisVPN-logs-", StringComparison.OrdinalIgnoreCase) || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(zipPath) || (File.GetAttributes(zipPath) & FileAttributes.ReparsePoint) != 0) return 3;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), SERVICE_LOGS);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
            foreach (var f in SERVICE_FILES) Add(zip, "service/" + f, Path.Combine(dir, f));
            return 0;
        }
        catch { return 1; }
    }
}
