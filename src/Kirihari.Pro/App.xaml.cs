using System.Windows;
using System.Windows.Threading;

namespace Kirihari.Pro;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("予期しないエラーが起きました。\n\n" + e.Exception.Message,
            "Kirihari Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
