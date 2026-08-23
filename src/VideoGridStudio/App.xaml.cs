using System.Windows;

namespace MkvPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Bootstraps WinForms (high-DPI mode, visual styles, compatible text rendering) before
        // the "VideoCreator" menu ever creates a WinForms control -- the same setup
        // Application.Run() used to provide implicitly for the grid/sequential tools when they
        // were their own standalone app. Called explicitly (rather than via the SDK-generated
        // ApplicationConfiguration.Initialize()) since that generated class isn't reliably
        // available across every build pass in a combined WPF+WinForms project.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        base.OnStartup(e);
    }
}
