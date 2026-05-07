using Avalonia.Controls;
using NSW.Utils;
using Res = NSW.Core.Properties.Resources;

namespace NSW.M3.Avalonia.Views;

public partial class MainWindow : Window
{
    public static string AppVersion
    {
        get
        {
            string version = Common.ToAppVersionString();
            return $"{Res.Main_CompressTitle} - Ver {version}";
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = AppVersion;
    }
}