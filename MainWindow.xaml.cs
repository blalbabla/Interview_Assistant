using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using InterviewAssistant.ViewModels;
using NHotkey;
using NHotkey.Wpf;

namespace InterviewAssistant;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
    private const uint WDA_EXCLUDE_FROM_CAPTURE = 0x00000011;
    private const uint WDA_NONE = 0x00000000;

    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ApplyScreenCaptureHiding(_vm.IsScreenCaptureHidden);
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        HotkeyManager.Current.AddOrReplace("ToggleRecording", Key.Space, ModifierKeys.Control, OnToggleRecording);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsScreenCaptureHidden))
            ApplyScreenCaptureHiding(_vm.IsScreenCaptureHidden);
    }

    private void ApplyScreenCaptureHiding(bool hide)
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowDisplayAffinity(handle, hide ? WDA_EXCLUDE_FROM_CAPTURE : WDA_NONE);
    }

    private void OnToggleRecording(object? sender, HotkeyEventArgs e)
    {
        _vm.ToggleRecording();
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        HotkeyManager.Current.Remove("ToggleRecording");
        _ = _vm.DisposeAsync();
        base.OnClosed(e);
    }
}
