using System.Drawing;
using System.Windows.Forms;

namespace VideoGridStudio.Forms;

/// <summary>The exe's own icon (embedded via ApplicationIcon in the .csproj), shared by every
/// WinForms window so they match the WPF MainWindow's taskbar/title-bar icon instead of the
/// default .NET form icon.</summary>
internal static class AppIcon
{
    public static readonly Icon Value = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
}
