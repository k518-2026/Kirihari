using System.Windows;
using Kirihari.Models;
using Kirihari.ViewModels;
using Kirihari.Views;

namespace Kirihari.Pro;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowHost.Attach(this, new MainViewModel(), Media, Frame, Timeline);

        HeightBox.ItemsSource = ExportOptions.HeightLabels;
        FpsBox.ItemsSource = ExportOptions.FpsLabels;
        DeintBox.ItemsSource = ExportOptions.DeinterlaceLabels;
        AudioBox.ItemsSource = ExportOptions.AudioLabels;
        EncoderBox.ItemsSource = ExportOptions.EncoderLabels;
        PresetBox.ItemsSource = ExportOptions.PresetLabels;
    }
}
