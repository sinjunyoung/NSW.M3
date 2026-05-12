using Avalonia.Controls;
using NSW.M3.Avalonia.ViewModels;

namespace NSW.M3.Avalonia.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}