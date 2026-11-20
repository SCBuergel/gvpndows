using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GnosisVpn;

/// <summary>Entry point (instead of the XAML-generated one): --selftest runs before WinUI starts,
/// so it also works without a desktop (an SSH session); everything else starts the app as usual.</summary>
public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--selftest") >= 0) return Testing.SelfTest.Run(args);
        if (Array.IndexOf(args, "--export-logs") >= 0) return LogExport.RunElevated(args);   // Settings > Logs, elevated
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }
}
