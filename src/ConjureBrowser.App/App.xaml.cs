using System.IO;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;

namespace ConjureBrowser.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prepare CefSharp/Chromium before the main window opens.
        CefSharpSettings.SubprocessExitIfParentProcessClosed = true;

        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConjureBrowser",
            "cef-cache");

        Directory.CreateDirectory(cachePath);

        var settings = new CefSettings
        {
            CachePath = cachePath,
            PersistSessionCookies = true
        };

        Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Cef.IsInitialized == true)
        {
            Cef.Shutdown();
        }

        base.OnExit(e);
    }
}
