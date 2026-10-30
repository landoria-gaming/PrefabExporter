using System.Globalization;
using System.Windows;

namespace PrefabExporter.UI;

// Starts the WPF application with English application messages.
public partial class App : Application
{
    // Sets the default user interface language for application threads.
    public App()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }
}
