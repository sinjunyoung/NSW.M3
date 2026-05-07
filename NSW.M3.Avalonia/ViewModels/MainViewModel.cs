using NSW.Avalonia.Services;
using ReactiveUI;

namespace NSW.M3.Avalonia.ViewModels;

public class MainViewModel: NSW.Avalonia.ViewModels.MainViewModelBase
{
    private bool _isCompress;
    private bool _isDecompress;

    public bool IsCompress
    {
        get => _isCompress;
        set => this.RaiseAndSetIfChanged(ref _isCompress, value);
    }
    
    public bool IsDecompress
    {
        get => _isDecompress;
        set => this.RaiseAndSetIfChanged(ref _isDecompress, value);
    }

    public MainViewModel(): base(new BaseConfig()){}
}