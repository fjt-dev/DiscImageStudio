using DiscImageStudio.Localization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;
using DvdImageSolver.Solver;

namespace DiscImageStudio.ViewModels;

/// <summary>Export workspace: turns the design page's composite into the disc artifact file.
/// The disc family comes from the open project, so the CD and DVD parameter pages collapse
/// into this one; the generation geometry lives in the project settings. Streaming that same
/// material to a blank disc is the burn workspace's job. The tone parameters live with the
/// design page, next to the composite they affect; this workspace only passes the shared
/// values to the engine.</summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    public ExportViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !_shell.IsBusy);
        BrowseDataCommand = new RelayCommand(BrowseData, () => !_shell.IsBusy);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !_shell.IsBusy);
        BrowseEmbedFileCommand = new RelayCommand(BrowseEmbedFile, () => !_shell.IsBusy);
        ClearEmbedFileCommand = new RelayCommand(() => _state.DvdEmbedFilePath = string.Empty, () => !_shell.IsBusy);
        CheckEmbedCapacityCommand = new AsyncRelayCommand(CheckEmbedCapacityAsync, () => !_shell.IsBusy);
        BrowseExtractIsoCommand = new RelayCommand(BrowseExtractIso, () => !_shell.IsBusy);
        BrowseExtractOutputCommand = new RelayCommand(BrowseExtractOutput, () => !_shell.IsBusy);
        ExtractCommand = new AsyncRelayCommand(ExtractAsync, () => !_shell.IsBusy);
        _shell.BusyChanged += NotifyCommands;
        _shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.IsProjectOpen)
                or nameof(ShellViewModel.CurrentProject))
            {
                OnPropertyChanged(nameof(IsCd));
                OnPropertyChanged(nameof(IsDvd));
                OnPropertyChanged(nameof(IsBluRay));
                OnPropertyChanged(nameof(FamilyLabel));
                OnPropertyChanged(nameof(GenerateButtonText));
                UpdateSourceSummary();
            }
        };
        _design.DesignEdited += UpdateSourceSummary;
        _design.CanvasChanged += UpdateSourceSummary;
        _design.DesignEdited += () => EmbedStatusText = string.Empty;
        _state.PropertyChanged += (_, args) =>
        {
            // The measured capacity belongs to one picture, geometry, tone and file layout.
            if (args.PropertyName is nameof(DiscParametersState.DvdEmbedFilePath)
                or nameof(DiscParametersState.DvdDataDirectory)
                or nameof(DiscParametersState.DvdFilesAtOuterRing)
                or nameof(DiscParametersState.DvdTotalSectors)
                or nameof(DiscParametersState.DvdInnerRadius)
                or nameof(DiscParametersState.DvdOuterRadius)
                or nameof(DiscParametersState.DvdPitchLinear)
                or nameof(DiscParametersState.DvdPitchQuadratic)
                or nameof(DiscParametersState.DvdPitchCubic)
                or nameof(DiscParametersState.ToneDitherStrength)
                or nameof(DiscParametersState.ToneBrightness)
                or nameof(DiscParametersState.ToneSrgbLinearization))
            {
                EmbedStatusText = string.Empty;
            }

            if (args.PropertyName == nameof(DiscParametersState.CdDataDirectory))
            {
                OnPropertyChanged(nameof(GenerateButtonText));
            }
        };
    }

    public bool IsCd => _shell.CurrentProject?.IsCd ?? true;

    public bool IsDvd => _shell.CurrentProject?.IsDvd ?? false;

    public bool IsBluRay => _shell.CurrentProject?.IsBluRay ?? false;

    /// <summary>False while the design page has no layers; the source row then offers the
    /// jump to where the picture comes from instead of a dead end.</summary>
    public bool HasLayers => _design.Layers.Count > 0;

    /// <summary>Family chip in the page header.</summary>
    public string FamilyLabel => _shell.CurrentProject?.FamilyLabel ?? "CD";

    /// <summary>Primary action label: the artifact differs per family (WAV track vs ISO).</summary>
    public string GenerateButtonText => !IsCd || _state.HasCdFiles ? L.T("生成镜像") : L.T("生成音轨");

    /// <summary>The source card's jump into the project dialog, where the generation geometry
    /// is edited; every other workspace shows the same affordance for the same reason.</summary>
    public IAsyncRelayCommand OpenProjectSettingsCommand => _shell.EditProjectCommand;

    /// <summary>Workspace navigation for the page header's "下一步" action.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand BrowseDataCommand { get; }

    public IAsyncRelayCommand GenerateCommand { get; }

    public RelayCommand BrowseEmbedFileCommand { get; }

    public RelayCommand ClearEmbedFileCommand { get; }

    public IAsyncRelayCommand CheckEmbedCapacityCommand { get; }

    public RelayCommand BrowseExtractIsoCommand { get; }

    public RelayCommand BrowseExtractOutputCommand { get; }

    public IAsyncRelayCommand ExtractCommand { get; }

    /// <summary>Result of the last capacity check for the file stored in the drawing; empty
    /// whenever the picture, geometry, tone, file layout or file changed since.</summary>
    [ObservableProperty]
    private string _embedStatusText = string.Empty;

    [ObservableProperty]
    private string _extractIsoPath = string.Empty;

    [ObservableProperty]
    private string _extractOutputPath = string.Empty;

    [ObservableProperty]
    private string _sourceSummary = L.T("请先在设计页导入图片。");

    private void NotifyCommands()
    {
        BrowseOutputCommand.NotifyCanExecuteChanged();
        BrowseDataCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        BrowseEmbedFileCommand.NotifyCanExecuteChanged();
        ClearEmbedFileCommand.NotifyCanExecuteChanged();
        CheckEmbedCapacityCommand.NotifyCanExecuteChanged();
        BrowseExtractIsoCommand.NotifyCanExecuteChanged();
        BrowseExtractOutputCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
    }

    private void BrowseEmbedFile()
    {
        string? picked = _shell.Dialogs?.PickOpen(L.T("选择要写入绘图区的文件"), L.T("所有文件|*.*"));
        if (picked is not null)
        {
            _state.DvdEmbedFilePath = picked;
        }
    }

    private void BrowseExtractIso()
    {
        string? picked = _shell.Dialogs?.PickOpen(
            L.T("选择 ISO 或从盘片读出的镜像"), L.T("ISO 镜像|*.iso|所有文件|*.*"));
        if (picked is not null)
        {
            ExtractIsoPath = picked;
        }
    }

    private void BrowseExtractOutput()
    {
        string? picked = _shell.Dialogs?.PickSave(L.T("所有文件|*.*"), string.Empty, "embedded-file");
        if (picked is not null)
        {
            ExtractOutputPath = picked;
        }
    }

    private async Task ExtractAsync()
    {
        try
        {
            string iso = RequirePath(ExtractIsoPath, L.T("请选择要读取的 ISO 或盘片镜像。"));
            string output = RequirePath(ExtractOutputPath, L.T("请选择取出文件的保存位置。"));
            await _shell.RunDiscJobAsync(
                L.T("正在取出内嵌文件…"),
                ["extract", "--iso", iso, "--output", output],
                output);
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private string RequireEmbedFile()
    {
        string path = _state.DvdEmbedFilePath.Trim();
        return path.Length == 0 || !File.Exists(path)
            ? throw new ArgumentException(L.F($"内嵌文件不存在：{path}"))
            : path;
    }

    /// <summary>The hybrid layout the drawing sits in, or null when no folder is stored.</summary>
    internal DvdHybridStreamingPlan? PrepareDvdFilePlan(DvdStreamingOptions options)
    {
        string dataDirectory = _state.DvdDataDirectory.Trim();
        return dataDirectory.Length == 0
            ? null
            : DvdStreamingGenerator.PrepareHybrid(
                dataDirectory,
                options,
                string.IsNullOrWhiteSpace(_state.DvdVolumeLabel) ? "DISC_IMAGE" : _state.DvdVolumeLabel.Trim(),
                filesAtOuterRing: _state.DvdFilesAtOuterRing);
    }

    private async Task CheckEmbedCapacityAsync()
    {
        try
        {
            string file = RequireEmbedFile();
            DvdStreamingOptions options = _state.ReadDvdStreamingOptions();
            string composite = await _design.EnsureExportCompositeAsync();
            DvdHybridStreamingPlan? plan = PrepareDvdFilePlan(options);
            DvdCarrierCapacity capacity = await MeasureEmbedCapacityAsync(composite, options, plan);
            EmbedStatusText = DescribeEmbedCapacity(capacity, new FileInfo(file).Length);
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    /// <summary>Throws when the stored file does not fit; the streaming burn calls this before
    /// it asks for confirmation, because a truncated file cannot be repaired on a written disc.</summary>
    internal async Task EnsureEmbedFitsAsync(
        string compositePath, DvdStreamingOptions options, DvdHybridStreamingPlan? plan)
    {
        string file = RequireEmbedFile();
        long size = new FileInfo(file).Length;
        DvdCarrierCapacity capacity = await MeasureEmbedCapacityAsync(compositePath, options, plan);
        EmbedStatusText = DescribeEmbedCapacity(capacity, size);
        if (!capacity.Fits(size))
        {
            throw new ArgumentException(EmbedStatusText);
        }
    }

    private async Task<DvdCarrierCapacity> MeasureEmbedCapacityAsync(
        string compositePath, DvdStreamingOptions options, DvdHybridStreamingPlan? plan)
    {
        _shell.SetBusy(true);
        _shell.BeginDockJob(L.T("检查内嵌容量"), determinate: true);
        Progress<DvdStreamingProgress> progress = new(value =>
            _shell.ReportDockJob(L.F($"正在测量绘图区容量… {value.Fraction:P0}"), value.Fraction * 100));
        try
        {
            // Image decoding goes through WIC, which wants the same STA thread as a job.
            DvdCarrierCapacity capacity = await StaWorker.RunAsync(() =>
                DvdStreamingGenerator.EstimateCarrierCapacity(
                    compositePath, options, plan, ((IProgress<DvdStreamingProgress>)progress).Report));
            _shell.CompleteDockJob(L.T("容量测量完成"));
            return capacity;
        }
        catch
        {
            _shell.AbortDockJob(L.T("容量测量失败"), expandLog: true);
            throw;
        }
        finally
        {
            _shell.SetBusy(false);
        }
    }

    private static string DescribeEmbedCapacity(DvdCarrierCapacity capacity, long fileBytes)
    {
        double mebibyte = 1024.0 * 1024.0;
        double max = capacity.MaxFileBytes / mebibyte;
        double size = fileBytes / mebibyte;
        if (capacity.Fits(fileBytes))
        {
            double percent = capacity.MaxFileBytes == 0 ? 0 : 100.0 * fileBytes / capacity.MaxFileBytes;
            return L.F($"可以放入：绘图区最多可存 {max:F2} MiB，当前文件 {size:F2} MiB（占 {percent:F1}%）。");
        }

        return L.F($"放不下：绘图区最多可存 {max:F2} MiB，当前文件 {size:F2} MiB，超出 {fileBytes - capacity.MaxFileBytes} 字节。")
            + L.T("请换更小的文件，或使用中间调更多的图片。");
    }

    private void BrowseOutput()
    {
        if (IsBluRay)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "bd-image.iso");
            if (iso is not null)
            {
                _state.BdOutputPath = iso;
            }
        }
        else if (IsDvd)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "dvd-image.iso");
            if (iso is not null)
            {
                _state.DvdOutputPath = iso;
            }
        }
        else if (_state.HasCdFiles)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "cd-image.iso");
            if (iso is not null)
            {
                _state.CdOutputPath = iso;
            }
        }
        else
        {
            string? wav = _shell.Dialogs?.PickSave(L.T("WAV 音轨（推荐）|*.wav|RAW 音轨（旧格式）|*.raw|所有文件|*.*"), ".wav", "cd-track.wav");
            if (wav is not null)
            {
                _state.CdOutputPath = wav;
            }
        }
    }

    private void BrowseData()
    {
        if (IsCd)
        {
            string? picked = _shell.Dialogs?.PickFolder(L.T("选择要放入 CD 数据盘的文件夹"));
            if (picked is not null)
            {
                _state.CdDataDirectory = picked;
            }

            return;
        }

        if (IsBluRay)
        {
            string? picked = _shell.Dialogs?.PickFolder(L.T("选择要放入蓝光盘的文件夹"));
            if (picked is not null)
            {
                _state.BdDataDirectory = picked;
            }

            return;
        }

        string? dvdFolder = _shell.Dialogs?.PickFolder(L.T("选择要放入 DVD 的文件夹"));
        if (dvdFolder is not null)
        {
            _state.DvdDataDirectory = dvdFolder;
        }
    }

    private void UpdateSourceSummary()
    {
        if (!_shell.IsProjectOpen)
        {
            SourceSummary = L.T("请先打开或新建一个工程。");
            return;
        }

        int layerCount = _design.Layers.Count;
        SourceSummary = layerCount == 0
            ? L.T("请先在设计页导入图片。")
            : L.F($"设计页合成图源（{RingImageQuality.GenerationSize} px）");
        OnPropertyChanged(nameof(HasLayers));
    }

    private async Task GenerateAsync()
    {
        try
        {
            // The composite renders here at export quality even when the design page has not
            // been visited in this session, so a reopened project can generate immediately.
            string compositePath = await _design.EnsureExportCompositeAsync();
            if (IsBluRay)
            {
                await GenerateBluRayAsync(compositePath);
            }
            else if (IsDvd)
            {
                await GenerateDvdAsync(compositePath);
            }
            else
            {
                await GenerateCdAsync(compositePath);
            }
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private async Task GenerateCdAsync(string compositePath)
    {
        string output = RequirePath(_state.CdOutputPath,
            _state.HasCdFiles ? L.T("请选择 CD ISO 输出位置。") : L.T("请选择 CD 音轨输出位置。"));
        List<string> arguments = ["cd-generate", "--input", compositePath, "--output", output];
        // The CD engine defaults (1200 mm/s, 0°, 57.5 mm) cover velocity, start angle and the
        // image outer radius; only the user-visible geometry is passed through.
        ToneProfile tone = _state.Tone;
        arguments.AddRange(
        [
            "--r0", _state.CdInnerRadius.Trim(),
            "--r1", _state.CdOuterRadius.Trim(),
            "--sectors", _state.CdSectors.Trim(),
            "--tone-dither", tone.DitherStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-levels", tone.Levels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--tone-brightness", tone.Brightness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-gamma", tone.SrgbLinearization.ToString().ToLowerInvariant(),
        ]);
        if (_state.HasCdFiles)
        {
            arguments.AddRange(
            [
                "--data-dir", _state.CdDataDirectory.Trim(),
                "--volume-label", string.IsNullOrWhiteSpace(_state.CdVolumeLabel) ? "CD_IMAGE" : _state.CdVolumeLabel.Trim(),
                "--file-position", _state.CdFilesAtOuterRing ? "outer" : "inner",
            ]);
        }
        else
        {
            arguments.AddRange(
            [
                "--interleave", _state.CdInterleave.ToString().ToLowerInvariant(),
                "--cue", _state.CdWriteCue.ToString().ToLowerInvariant(),
            ]);
        }
        await _shell.RunDiscJobAsync(
            _state.HasCdFiles ? L.T("正在生成 CD 数据盘 ISO…") : L.T("正在生成 CD 音轨…"),
            arguments.ToArray(),
            output);
        // The generated track is the live preview page's read-back input.
        _state.CdTrackPath = output;
    }

    private async Task GenerateDvdAsync(string compositePath)
    {
        string output = RequirePath(_state.DvdOutputPath, L.T("请选择 DVD ISO 输出位置。"));
        ToneProfile tone = _state.DvdTone;
        List<string> arguments =
        [
            "solve",
            "--image", compositePath,
            "--iso-output", output,
            "--total-sectors", _state.DvdTotalSectors.Trim(),
            "--inner-radius-mm", _state.DvdInnerRadius.Trim(),
            "--outer-radius-mm", _state.DvdOuterRadius.Trim(),
            "--pitch-linear", _state.DvdPitchLinear.Trim(),
            "--pitch-quadratic", _state.DvdPitchQuadratic.Trim(),
            "--pitch-cubic", _state.DvdPitchCubic.Trim(),
            "--spiral-direction", "cw",
            "--image-threshold", "128",
            "--alpha-threshold", "1",
            "--tone-dither", tone.DitherStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-levels", "2",
            "--tone-brightness", tone.Brightness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-gamma", tone.SrgbLinearization.ToString().ToLowerInvariant(),
            "--algorithm", "dispersion",
            "--iterations-per-block", "1",
            "--fast-output", "true",
            "--volume-label", string.IsNullOrWhiteSpace(_state.DvdVolumeLabel) ? "DISC_IMAGE" : _state.DvdVolumeLabel.Trim(),
        ];
        if (string.IsNullOrWhiteSpace(_state.DvdDataDirectory))
        {
            arguments.Add("--fill-sectors");
            arguments.Add(_state.DvdTotalSectors.Trim());
        }
        else
        {
            arguments.Add("--data-dir");
            arguments.Add(_state.DvdDataDirectory.Trim());
            arguments.Add("--file-position");
            arguments.Add(_state.DvdFilesAtOuterRing ? "outer" : "inner");
        }

        if (_state.HasDvdEmbedFile)
        {
            arguments.Add("--embed-file");
            arguments.Add(RequireEmbedFile());
        }

        await _shell.RunDiscJobAsync(
            L.T("正在生成 DVD ISO…"),
            arguments.ToArray(),
            output);
        // The generated ISO is the live preview page's read-back input.
        _state.DvdIsoPath = output;
    }

    private async Task GenerateBluRayAsync(string compositePath)
    {
        string output = RequirePath(_state.BdOutputPath, L.T("请选择蓝光 ISO 输出位置。"));
        ToneProfile tone = _state.BluRayTone;
        List<string> arguments =
        [
            "bd-generate",
            "--input", compositePath,
            "--output", output,
            "--total-sectors", _state.BdTotalSectors.Trim(),
            "--r0", _state.BdInnerRadius.Trim(),
            "--r1", _state.BdOuterRadius.Trim(),
            "--invert", _state.BdInvertMapping.ToString().ToLowerInvariant(),
            "--volume-label", string.IsNullOrWhiteSpace(_state.BdVolumeLabel)
                ? "BD_IMAGE" : _state.BdVolumeLabel.Trim(),
            "--tone-dither", tone.DitherStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-levels", "2",
            "--tone-brightness", tone.Brightness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "--tone-gamma", tone.SrgbLinearization.ToString().ToLowerInvariant(),
        ];
        if (!string.IsNullOrWhiteSpace(_state.BdDataDirectory))
        {
            arguments.Add("--data-dir");
            arguments.Add(_state.BdDataDirectory.Trim());
            arguments.Add("--file-position");
            arguments.Add(_state.BdFilesAtOuterRing ? "outer" : "inner");
        }

        await _shell.RunDiscJobAsync(
            L.T("正在生成蓝光 ISO…"),
            arguments.ToArray(),
            output);
        // The generated ISO and its sidecar are the live preview page's read-back input.
        _state.BdIsoPath = output;
    }

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }
}
