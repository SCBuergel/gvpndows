using Microsoft.UI.Xaml;

namespace GnosisVpn;

public partial class App : Application
{
    public static MainWindow? Main { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += (_, e) => { Log("unhandled: " + e.Exception); e.Handled = true; };
        if (Testing.IconGen.RunIfRequested()) { Exit(); return; }
        Main = new MainWindow();
        // Start in the notification area (Settings → Startup): no window, only the tray icon.
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--tray") >= 0) { Main.U.WinState = "closed"; Main.Render(); }
        else Main.Activate();
        _ = Testing.Shots.RunIfRequested(Main);
    }

    /// <summary>App log: %LOCALAPPDATA%\GnosisVPN\ui.log.</summary>
    public static void Log(string line)
    {
        try
        {
            var d = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "GnosisVPN");
            System.IO.Directory.CreateDirectory(d);
            System.IO.File.AppendAllText(System.IO.Path.Combine(d, "ui.log"), System.DateTime.UtcNow.ToString("u") + " " + line + System.Environment.NewLine);
        }
        catch { }
    }
}
