using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Kirihari.Controls;
using Kirihari.ViewModels;

namespace Kirihari.Views;

/// <summary>Lite と Pro のウィンドウに共通の配線（ファイルのドロップ・プレビュー・終了確認）。</summary>
public static class WindowHost
{
    public static PreviewController Attach(Window window, MainViewModel vm, MediaElement media, Image frame, TimelineControl timeline)
    {
        window.DataContext = vm;
        window.AllowDrop = true;

        window.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        window.Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await vm.AddFilesAsync(files);
        };
        window.Closing += (_, e) =>
        {
            if (!vm.ConfirmClose()) e.Cancel = true;
        };

        window.PreviewKeyDown += (_, e) => OnKey(vm, e);

        vm.ZoomAllRequested += () => timeline.ZoomAll();
        return new PreviewController(media, frame, vm);
    }

    /// <summary>ショートカットキー。文字を入力中（テキストボックスなど）は何もしない。</summary>
    private static void OnKey(MainViewModel vm, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        ICommand cmd = null;
        object param = null;

        if (ctrl)
        {
            cmd = e.Key switch
            {
                Key.Z => vm.UndoCommand,
                Key.Y => vm.RedoCommand,
                Key.O => vm.AddFilesCommand,
                Key.E => vm.ExportCommand,
                Key.Left => vm.PrevSegmentCommand,
                Key.Right => vm.NextSegmentCommand,
                _ => null,
            };
        }
        else
        {
            switch (e.Key)
            {
                case Key.Space: cmd = vm.TogglePlayCommand; break;
                case Key.S: cmd = vm.SplitCommand; break;
                case Key.I: cmd = vm.SetInCommand; break;
                case Key.O: cmd = vm.SetOutCommand; break;
                case Key.X: cmd = vm.RemoveRangeCommand; break;
                case Key.Delete: cmd = vm.DeleteSegmentCommand; break;
                case Key.Home: cmd = vm.GoStartCommand; break;
                case Key.End: cmd = vm.GoEndCommand; break;
                case Key.Left: cmd = vm.StepCommand; param = shift ? -10 : -1; break;
                case Key.Right: cmd = vm.StepCommand; param = shift ? 10 : 1; break;
            }
        }
        if (cmd != null && cmd.CanExecute(param))
        {
            cmd.Execute(param);
            e.Handled = true;
        }
    }
}
