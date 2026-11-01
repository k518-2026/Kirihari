using System.Windows;
using Kirihari.ViewModels;
using Kirihari.Views;

namespace Kirihari.Lite;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowHost.Attach(this, new MainViewModel(), Media, Frame, Timeline);
    }
}
