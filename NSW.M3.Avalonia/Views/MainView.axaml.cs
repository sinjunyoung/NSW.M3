using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NSW.Avalonia.Services;
using NSW.Avalonia.UI;
using NSW.Core.Enums;
using NSW.M3.Avalonia.Services;
using NSW.M3.Avalonia.ViewModels;
using NSW.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Path = System.IO.Path;
using Res = NSW.Core.Properties.Resources;

namespace NSW.M3.Avalonia.Views;

public partial class MainView : UserControl
{
    #region Fields & Properties

    private readonly Stopwatch _totalSw = new();
    private CancellationTokenSource? _cts;
    private readonly Progress<ProgressInfo> _progressReporter;

    private MainViewModel ViewModel => DataContext as MainViewModel;

    #endregion

    #region Constructor

    public MainView()
    {
        InitializeComponent();

        _progressReporter = new(info =>
        {
            Dispatcher.UIThread.Post(() => {
                progress.Value = info.Percent;
                progressLabel.Text = info.Label;
                progressPercent.Text = $"{info.Percent}%";
                progressTime.Text = info.TimeInfo;
                progressSpeed.Text = info.Speed;
            });
        });

        this.DetachedFromVisualTree += (s, e) => ViewModel.Config.Save();
    }

    #endregion

    #region Protected Override

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    #endregion

    #region Event Hanlders

    private async void BtnStartCompress_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            await SetWorking(false);
            return;
        }

        if (!FileManagerControl.KeyExists())
        {
            string msg = OperatingSystem.IsAndroid() ? Res.Main_Err_NoKeys_Android : Res.Main_Err_NoKeys;
            await MessageBoxHelper.ShowWarning(msg);
            return;
        }

        if (fileMgr.GameFiles.Any(f => f.IsKeyMissing))
        {
            await SetWorking(true);
            progressLabel.Text = Res.Main_Log_Recalculating;

            bool completed = false;
            fileMgr.RecalcKeyMissingFiles(() => completed = true);

            while (!completed)
                await Task.Delay(100);
        }

        if (!TryGetInputsForCompress(out var inputPaths, out string errorMsg))
        {
            await MessageBoxHelper.ShowWarning(errorMsg);
            await SetWorking(false);
            return;
        }

        _cts = new CancellationTokenSource();
        await SetWorking(true);
        _totalSw.Restart();

        int compressLevel = (int)ViewModel.CompressLevel;
        bool isValidationEnabled = ViewModel.IsValidationEnabled;
        bool useBlockMode = ViewModel.UseBlockMode;

        try
        {
            await Task.Run(async () =>
            {
                var results = await NspCompressService.Compress(inputPaths, compressLevel, isValidationEnabled, useBlockMode, _progressReporter, Log, _cts.Token);

                if (results != null && results.Count > 0)
                {
                    Log(string.Format(Res.Main_Log_AllComplete, _totalSw.Elapsed.ToString(@"mm\:ss")), LogLevel.Ok);
                    Dispatcher.UIThread.Post(async () => await MessageBoxHelper.ShowInfo(Res.Main_Msg_Done));
                }
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log(Res.Button_Cancel, LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log($"{Res.Log_Error}: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            await SetWorking(false);
        }
    }

    private async void BtnStartDecompress_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            await SetWorking(false, true);
            return;
        }

        if (!FileManagerControl.KeyExists())
        {
            string msg = OperatingSystem.IsAndroid() ? Res.Main_Err_NoKeys_Android : Res.Main_Err_NoKeys;
            await MessageBoxHelper.ShowWarning(msg);
            return;
        }

        if (fileMgr.GameFiles.Any(f => f.IsKeyMissing))
        {
            await SetWorking(true);
            progressLabel.Text = Res.Main_Log_Recalculating;

            bool completed = false;
            fileMgr.RecalcKeyMissingFiles(() => completed = true);

            while (!completed)
                await Task.Delay(100);
        }

        if (!TryGetInputsForDecompress(out var inputPaths, out string errorMsg))
        {
            await MessageBoxHelper.ShowWarning(errorMsg);
            await SetWorking(false);
            return;
        }

        _cts = new CancellationTokenSource();
        await SetWorking(true, true);
        _totalSw.Restart();

        try
        {
            await Task.Run(async () =>
            {
                var results = await NspCompressService.Decompress(inputPaths, _progressReporter, Log, _cts.Token);

                if (results != null && results.Count > 0)
                {
                    Log(string.Format(Res.Main_Log_AllComplete, _totalSw.Elapsed.ToString(@"mm\:ss")), LogLevel.Ok);
                    Dispatcher.UIThread.Post(async () => await MessageBoxHelper.ShowInfo(Res.Main_Msg_Done));
                }
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log(Res.Log_Error + ": " + Res.Button_Cancel, LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log($"{Res.Log_Error}: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            await SetWorking(false);
        }
    }

    #endregion

    #region Private Methods

    private bool TryGetInputsForCompress(out List<string> inputPaths, out string errorMsg)
    {
        bool result = TryGetInputs(out inputPaths, out errorMsg, ".nsp", ".xci");

        if (!result)
            return false;

        if (inputPaths.Count == 0)
        {
            errorMsg = Res.Error_NoCompressFiles;
            return false;
        }

        return true;
    }

    private bool TryGetInputsForDecompress(out List<string> inputPaths, out string errorMsg)
    {
        bool result = TryGetInputs(out inputPaths, out errorMsg, ".nsz", ".xcz");

        if (!result)
            return false;

        if (inputPaths.Count == 0)
        {
            errorMsg = Res.Error_NoDecompressFiles;
            return false;
        }

        return true;
    }

    private bool TryGetInputs(out List<string> inputPaths, out string errorMsg, params string[] allowedExts)
    {
        inputPaths = [];
        errorMsg = string.Empty;

        if (fileMgr.GameFiles.Any(f => f.IsKeyMissing))
        {
            errorMsg = Res.Main_Err_NoKeys;
            return false;
        }

        inputPaths = [.. fileMgr.GameFiles
            .Where(f => allowedExts.Contains(Path.GetExtension(f.FilePath).ToLowerInvariant()))
            .Select(f => f.FilePath)];

        return true;
    }

    private void Log(string msg, LogLevel level = LogLevel.Info, string titleId = "") => LogHelper.Log(logBox, svLogBox, fileMgr.GetCoverImageByTitleId(titleId), msg, level);

    private async Task SetWorking(bool working, bool isDecompress = false)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            btnStartCompress.IsRunning = working && !isDecompress;
            btnStartDecompress.IsRunning = working && isDecompress;
            btnStartCompress.IsEnabled = !working || working && !isDecompress;
            btnStartDecompress.IsEnabled = !working || working && isDecompress;
            fileMgr.IsEnabled = !working;
            sliderCompression.IsEnabled = !working;
            tbValidation.IsEnabled = !working;
            progressArea.IsVisible = working;
        });
    }

    #endregion
}