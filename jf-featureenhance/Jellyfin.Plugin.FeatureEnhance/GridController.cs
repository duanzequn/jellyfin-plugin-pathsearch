using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// Builds a 3x3 contact sheet on demand and caches it on disk.
///
/// Hardware acceleration is NOT implemented here: every ffmpeg input/filter argument comes from
/// Jellyfin's own <see cref="EncodingHelper"/> (GetInputArgument / GetVideoProcessingFilterParam),
/// i.e. exactly the strings the built-in trickplay extraction feeds to ffmpeg. That keeps device
/// selection, decoder choice, tonemapping and CPU/GPU transfer decisions in native hands, so the
/// plugin automatically follows Dashboard -> Playback -> Transcoding and future Jellyfin changes.
///
/// What this class adds on top: the sampling (nine seeks at 10% ... 90% of the runtime instead of
/// walking the whole file, which for a 20 GB remux on a spindle means minutes), the 3x3
/// composition and the caption.
/// </summary>
[ApiController]
[Authorize]
[Route("FeatureEnhance")]
public class GridController : ControllerBase
{
    /// <summary>Cells per row / rows per sheet.</summary>
    private const int Cells = 3;

    /// <summary>Total number of sampled frames (3x3).</summary>
    private const int TotalCells = Cells * Cells;

    /// <summary>Height of one cell in the finished sheet (width follows the frame aspect ratio).</summary>
    private const int CellHeight = 640;

    /// <summary>How far a single dark cell is nudged: one percentage point of the runtime.</summary>
    private const double NudgeFraction = 0.01d;

    /// <summary>How many cells are extracted at the same time.</summary>
    private const int MaxParallelCells = 9;

    /// <summary>Hard cap on a single ffmpeg cell extraction, so a stuck process cannot hold the request.</summary>
    private const int CellTimeoutSeconds = 60;

    private const int MinCellWidth = 180;
    private const int MaxCellWidth = 1920;
    private const int MinBrightness = 25;
    private const int MaxParallelGenerations = 2;
    private const string FontPath = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";

    /// <summary>宫格缓存上限：超过就按最旧的先删（之前完全没有淘汰，会一直涨）。</summary>
    private const int MaxCacheFiles = 4000;
    private const long MaxCacheBytes = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>单次暗场检测（一次 ffmpeg 处理全部 9 格）的超时。</summary>
    private const int BrightnessTimeoutSeconds = 20;

    private static readonly object SweepLock = new();
    private static long _lastSweepTicks;

    private static readonly SemaphoreSlim Gate = new(MaxParallelGenerations, MaxParallelGenerations);
    private static readonly ConcurrentDictionary<Guid, (MediaSourceInfo Source, DateTime At)> ProbedSources = new();
    private static readonly ConcurrentDictionary<Guid, DateTime> ProbeFailures = new();
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProbeFailureTtl = TimeSpan.FromMinutes(10);
    private const int MaxProbedSources = 512;
    private static int _loggedDecoderArgs;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _configManager;
    private readonly EncodingHelper _encodingHelper;
    private readonly ILogger<GridController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GridController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager (permission check).</param>
    /// <param name="mediaEncoder">Media encoder (ffmpeg path).</param>
    /// <param name="configManager">Server configuration.</param>
    /// <param name="encodingHelper">Jellyfin's own argument builder (same instance the transcoder and the trickplay generator use).</param>
    /// <param name="logger">Logger.</param>
    public GridController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager configManager,
        EncodingHelper encodingHelper,
        ILogger<GridController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaEncoder = mediaEncoder;
        _configManager = configManager;
        _encodingHelper = encodingHelper;
        _logger = logger;
    }

    /// <summary>
    /// Gets the contact sheet for a video item, generating it on first request.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="refresh">Regenerate an existing cache entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JPEG image.</returns>
    [HttpGet("Grid/{itemId}")]
    public async Task<ActionResult> GetGrid(
        [FromRoute] Guid itemId,
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video
            || string.IsNullOrEmpty(video.Path)
            || !System.IO.File.Exists(video.Path))
        {
            return NotFound();
        }

        // 权限：任何登录用户拿 id 就能生成任意条目的预览图（原生图片接口是有可见性校验的）。
        // API key（票据里没有用户 id）按原生规则视为全库可访问。
        var userId = PluginUser.GetUserId(User);
        if (userId != Guid.Empty)
        {
            var user = _userManager.GetUserById(userId);
            if (user is null || !video.IsVisible(user) || !IsInAccessibleLibrary(video, user))
            {
                return NotFound();
            }
        }

        var cacheDirectory = Path.Combine(Plugin.Instance!.DataFolderPath, "grid");
        Directory.CreateDirectory(cacheDirectory);

        // 每个视频只缓存一份：整张图永远是竖版，横屏素材是"每格内部旋转 90°"，客户端不做旋转
        var target = Path.Combine(cacheDirectory, itemId.ToString("N", CultureInfo.InvariantCulture) + ".jpg");

        if (refresh || !IsUsable(target))
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (refresh || !IsUsable(target))
                {
                    await GenerateAsync(video, target, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        if (!IsUsable(target))
        {
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        // 短缓存 + ETag：正常情况浏览器 5 分钟内直接用缓存，之后回源校验（没变就是 304）。
        // 之前是 max-age=86400 且 URL 不带版本戳 —— 一旦某次拿到的是旧图/失败回退图，会粘在浏览器里一整天。
        var file = new FileInfo(target);
        var etag = "\"" + file.Length.ToString(CultureInfo.InvariantCulture)
            + "-" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\"";
        Response.Headers.ETag = etag;
        Response.Headers.LastModified = file.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture);
        Response.Headers.CacheControl = "private, max-age=300";

        // PhysicalFileResult 不会自己处理条件请求，这里手动回 304
        if (Request.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return PhysicalFile(target, "image/jpeg");
    }

    private static bool IsUsable(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 4096;
    }

    /// <summary>
    /// Finds the media source that belongs to the item itself (same rule the trickplay manager uses).
    /// </summary>
    private static MediaSourceInfo? ResolveStoredMediaSource(Video video)
    {
        var sources = video.GetMediaSources(false);
        if (sources.Count == 0)
        {
            return null;
        }

        return sources.FirstOrDefault(source =>
                   Guid.TryParse(source.Id, out var id) && id.Equals(video.Id))
               ?? sources[0];
    }

    /// <summary>
    /// Media source for the item, probing the file when the library has not analyzed it yet.
    ///
    /// 本机有 36% 的视频在库里还没有媒体流信息（28k/79k）——那些条目一旦点开宫格就会失败，
    /// 而"播放一次"之后 Jellyfin 补齐了信息就正常了，之前的表现就是这样。
    /// 这里缺信息时用 Jellyfin 自己的 ffprobe 封装现探一次（只用于本次生成，不写库），
    /// 结果按 item 缓存在内存里，避免每次悬停都探一遍。
    /// </summary>
    private async Task<MediaSourceInfo?> ResolveMediaSourceAsync(Video video, CancellationToken cancellationToken)
    {
        var stored = ResolveStoredMediaSource(video);
        if (stored?.VideoStream is not null)
        {
            return stored;
        }

        if (string.IsNullOrEmpty(video.Path) || !System.IO.File.Exists(video.Path))
        {
            return stored;
        }

        // 损坏文件（缺 moov / 被截断）探测会失败，短时间记住结果，免得每次悬停都再探一遍
        if (ProbeFailures.TryGetValue(video.Id, out var failedAt))
        {
            if (DateTime.UtcNow - failedAt < ProbeFailureTtl)
            {
                return stored;
            }

            ProbeFailures.TryRemove(video.Id, out _);
        }

        if (ProbedSources.TryGetValue(video.Id, out var cached))
        {
            if (DateTime.UtcNow - cached.At < ProbeTtl)
            {
                return cached.Source;
            }

            ProbedSources.TryRemove(video.Id, out _);
        }

        try
        {
            _logger.LogDebug("Feature Enhance: item {Path} has no stored media info yet, probing it", video.Path);
            var info = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo
                    {
                        Path = video.Path,
                        Protocol = MediaProtocol.File,
                        VideoType = VideoType.VideoFile
                    },
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false
                },
                cancellationToken).ConfigureAwait(false);

            var probed = new MediaSourceInfo
            {
                Id = video.Id.ToString("N", CultureInfo.InvariantCulture),
                Path = video.Path,
                Name = video.Name,
                Container = info.Container,
                RunTimeTicks = info.RunTimeTicks,
                Size = info.Size,
                MediaStreams = info.MediaStreams,
                VideoType = VideoType.VideoFile,
                Protocol = MediaProtocol.File
            };

            if (probed.VideoStream is null)
            {
                return stored;
            }

            if (ProbedSources.Count > MaxProbedSources)
            {
                ProbedSources.Clear();
            }

            ProbedSources[video.Id] = (probed, DateTime.UtcNow);
            ProbeFailures.TryRemove(video.Id, out _);
            return probed;
        }
        catch (Exception ex)
        {
            // 探测失败基本等于文件坏了（缺 moov、被截断）。记下来，短时间内不再重试。
            _logger.LogWarning(ex, "Feature Enhance: probing {Path} failed (broken file?), no preview possible", video.Path);

            if (ProbeFailures.Count > MaxProbedSources)
            {
                ProbeFailures.Clear();
            }

            ProbeFailures[video.Id] = DateTime.UtcNow;
            return stored;
        }
    }

    private async Task GenerateAsync(Video video, string target, CancellationToken cancellationToken)
    {
        var mediaSource = await ResolveMediaSourceAsync(video, cancellationToken).ConfigureAwait(false);
        var stream = mediaSource?.VideoStream;
        if (mediaSource is null
            || stream is null
            || string.IsNullOrEmpty(mediaSource.Path)
            || !System.IO.File.Exists(mediaSource.Path))
        {
            _logger.LogWarning("Feature Enhance: no usable media source for {Path}", video.Path);
            return;
        }

        var duration = ResolveDuration(video, mediaSource);

        // 格子尺寸不再在这里算：旋转行为在硬解和软解两条路上不一样（见 BuildCellsAsync），
        // 由实际拿去跑 ffmpeg 的那组输入参数决定，生成完再把 cellWidth 带回来。
        var trickplay = _configManager.Configuration.TrickplayOptions;
        var started = Stopwatch.StartNew();
        // 临时文件和 scratch 目录都带上本次生成的唯一后缀：同一个视频被并发请求时（两个客户端同时点）
        // 两轮生成各写各的，最后只有一次 rename 生效，绝不会出现"半张图被搬到正式路径"。
        var token = Guid.NewGuid().ToString("N");
        var temp = target + "." + token + ".tmp.jpg";
        var scratch = Path.Combine(Path.GetDirectoryName(target)!, "scratch-" + token);

        try
        {
            Directory.CreateDirectory(scratch);

            // 先用 Jellyfin 配置里的硬件加速，失败（编码不被支持、设备被占用等）整轮回退到软件。
            // 暗场不在这里处理：单格偏暗由 BuildCellsAsync 把那一格往后挪 1 个百分点，
            // 不会为了几格黑画面把整张图重跑一遍。
            var attempt = await BuildCellsAsync(mediaSource, video, duration, useConfiguredHwAccel: true, scratch, trickplay, cancellationToken).ConfigureAwait(false);
            if (attempt.Cells is null)
            {
                _logger.LogDebug("Feature Enhance: hardware accelerated sheet failed for {Path}, retrying in software", video.Path);
                Cleanup(scratch);
                Directory.CreateDirectory(scratch);
                attempt = await BuildCellsAsync(mediaSource, video, duration, useConfiguredHwAccel: false, scratch, trickplay, cancellationToken).ConfigureAwait(false);
            }

            var cells = attempt.Cells;
            if (cells is null || !await ComposeAsync(cells, attempt.CellWidth, video, duration, temp, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Feature Enhance: contact sheet generation failed for {Path}", video.Path);
                return;
            }

            System.IO.File.Move(temp, target, true);
            SweepCache(Path.GetDirectoryName(target)!);
            _logger.LogDebug(
                "Feature Enhance: contact sheet for {Path} generated in {Elapsed} ms ({Width}x{Height})",
                video.Path,
                started.ElapsedMilliseconds,
                attempt.CellWidth * Cells,
                CellHeight * Cells);
        }
        finally
        {
            Cleanup(scratch);

            if (System.IO.File.Exists(temp))
            {
                System.IO.File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Runtime in seconds: the item first, the media source as a fallback (some virtual items
    /// carry no runtime of their own). Zero means "unknown", the sampling then guesses.
    /// </summary>
    private static double ResolveDuration(Video video, MediaSourceInfo mediaSource)
    {
        var ticks = video.RunTimeTicks is > 0 ? video.RunTimeTicks : mediaSource.RunTimeTicks;

        return ticks is > 0 ? TimeSpan.FromTicks(ticks.Value).TotalSeconds : 0d;
    }

    /// <summary>
    /// Samples the nine cells at 10%, 20% ... 90% of the runtime - one frame per cell, spread over
    /// the whole video instead of a few clusters. Each cell is its own ffmpeg process (one seek,
    /// one decoded frame), which also means a cell that cannot be read only costs that cell.
    /// </summary>
    /// <returns>Cell image paths in row-major order, or null when nothing could be extracted.</returns>
    private async Task<(List<string>? Cells, int CellWidth)> BuildCellsAsync(
        MediaSourceInfo mediaSource,
        Video video,
        double duration,
        bool useConfiguredHwAccel,
        string scratch,
        TrickplayOptions trickplay,
        CancellationToken cancellationToken)
    {
        var stream = mediaSource.VideoStream!;

        // 和 trickplay 完全一样的状态对象：Jellyfin 用它来决定硬件设备、解码器、滤镜链和色调映射
        var state = new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            IsVideoRequest = true,
            MediaSource = mediaSource,
            VideoStream = stream,
            // MaxWidth 先占位：它只影响滤镜链（scale），不影响输入参数，
            // 而"输入参数里有没有 -noautorotate"决定了我们该按哪个方向算格子尺寸。
            BaseRequest = new BaseEncodingJobOptions
            {
                MaxWidth = CellHeight,
                MaxFramerate = 1f
            },
            MediaPath = mediaSource.Path,
            OutputVideoCodec = "mjpeg"
        };

        EncodingOptions options;
        if (trickplay.EnableHwAcceleration && useConfiguredHwAccel)
        {
            options = _configManager.GetEncodingOptions();
        }
        else
        {
            // 与 MediaEncoder.ExtractVideoImagesOnIntervalAccelerated 的软件分支一致
            options = new EncodingOptions
            {
                HardwareAccelerationType = HardwareAccelerationType.none,
                EnableHardwareEncoding = false,
                EnableTonemapping = false
            };
        }

        var inputArgument = _encodingHelper.GetInputArgument(state, options, mediaSource.Container).Trim();
        if (inputArgument.Length == 0)
        {
            _logger.LogDebug("Feature Enhance: native input arguments unavailable, no preview possible for {Path}", mediaSource.Path);
            return (null, 0);
        }

        // 关键：旋转要用"实际拿到手的那一帧"来算。
        //   软解：ffmpeg 会按显示矩阵自动旋转（原生软解分支不加任何禁止参数）→ 帧 = 显示方向；
        //   硬解：原生在输入参数里加了 -noautorotate / -display_rotation 0（见 EncodingHelper
        //   GetInputVideoHwaccelArgs），自动旋转被关掉 → 帧 = 存储方向。
        // 以前不管哪条路都按"已旋转"算，于是带旋转元数据的竖拍视频在硬解下会得到
        // "竖格子里的小横图"（能出图，但不对）。这里按实际参数判断，两条路结果一致。
        var framesAutoRotated = inputArgument.IndexOf("-display_rotation", StringComparison.Ordinal) < 0
            && inputArgument.IndexOf("-noautorotate", StringComparison.Ordinal) < 0;
        var geometry = ComputeGeometry(stream, video, framesAutoRotated);
        var rotate = geometry.Rotate;

        // 滤镜链依赖 MaxWidth（scale），所以必须在几何算出来之后再取
        state.BaseRequest = new BaseEncodingJobOptions
        {
            MaxWidth = geometry.MaxWidth,
            MaxFramerate = 1f
        };

        var filterArgument = ExtractFilterChain(
            _encodingHelper.GetVideoProcessingFilterParam(state, options, state.OutputVideoCodec).Trim());
        if (filterArgument is null)
        {
            _logger.LogDebug("Feature Enhance: native filter chain unavailable, no preview possible for {Path}", mediaSource.Path);
            return (null, 0);
        }

        if (Interlocked.Exchange(ref _loggedDecoderArgs, 1) == 0)
        {
            _logger.LogInformation("Feature Enhance: contact sheets use Jellyfin's own encoding arguments: {Arguments}", inputArgument);
        }

        var quality = Math.Clamp(trickplay.Qscale, 1, 31);
        var threads = Math.Max(1, trickplay.ProcessThreads);
        var limit = new SemaphoreSlim(MaxParallelCells, MaxParallelCells);

        var tasks = new Task<string?>[TotalCells];
        for (var cell = 0; cell < TotalCells; cell++)
        {
            tasks[cell] = BuildCellAsync(state, inputArgument, filterArgument, quality, threads, trickplay.ProcessPriority, rotate, duration, 0d, string.Empty, cell, scratch, limit, cancellationToken);
        }

        var cells = await Task.WhenAll(tasks).ConfigureAwait(false);

        // 单格偏暗（刚好落在转场/黑帧上）时只把那一格往后挪 1 个百分点，只挪一次、不循环；
        // 大半格子都暗说明这本来就是暗片，挪了也没用，直接放过 —— 全黑视频不会因此多跑一轮。
        var darkFlags = await FindDarkCellsAsync(cells, cancellationToken).ConfigureAwait(false);
        var dark = Enumerable.Range(0, TotalCells).Where(cell => darkFlags[cell]).ToList();

        if (dark.Count > 0 && dark.Count <= TotalCells / 2)
        {
            _logger.LogDebug("Feature Enhance: {Count} dark cell(s), resampling them 1% later ({Path})", dark.Count, state.MediaPath);

            var nudges = new Task<string?>[dark.Count];
            for (var i = 0; i < dark.Count; i++)
            {
                nudges[i] = BuildCellAsync(state, inputArgument, filterArgument, quality, threads, trickplay.ProcessPriority, rotate, duration, NudgeFraction, "_n", dark[i], scratch, limit, cancellationToken);
            }

            var nudged = await Task.WhenAll(nudges).ConfigureAwait(false);
            for (var i = 0; i < dark.Count; i++)
            {
                if (nudged[i] is not null)
                {
                    System.IO.File.Move(nudged[i]!, cells[dark[i]]!, true);
                }
            }
        }
        else if (dark.Count > 0)
        {
            _logger.LogDebug("Feature Enhance: {Count}/{Total} cells are dark, keeping them as is ({Path})", dark.Count, TotalCells, state.MediaPath);
        }

        // 对齐成九格：哪一格没取到就用前面一格顶上，避免整体左移导致后面几张的时间点全部错位
        var filled = new List<string>(TotalCells);
        var fallback = cells.FirstOrDefault(cell => cell is not null);
        foreach (var cell in cells)
        {
            fallback = cell ?? fallback;
            if (fallback is not null)
            {
                filled.Add(fallback);
            }
        }

        return filled.Count > 0 ? (filled, geometry.CellWidth) : (null, geometry.CellWidth);
    }

    /// <summary>
    /// 格子形状与缩放宽度。
    ///
    /// 竖屏源保持原方向，横屏源每格顺时针转 90 度（transpose=1）成竖格；帧按"宽"缩放
    /// （原生 MaxWidth 就是宽），所以需要转的源要按格子的高来缩放，转完才刚好填满格子。
    /// <paramref name="framesAutoRotated"/> 为 false 时拿到的是**存储方向**的帧，
    /// 显示方向要自己从 Rotation 推出来。
    /// </summary>
    private static (int CellWidth, int MaxWidth, bool Rotate) ComputeGeometry(MediaStream stream, Video video, bool framesAutoRotated)
    {
        var rawWidth = stream.Width ?? video.Width;
        var rawHeight = stream.Height ?? video.Height;
        if (rawWidth <= 0 || rawHeight <= 0)
        {
            rawWidth = 1080;
            rawHeight = 1920;
        }

        int frameWidth, frameHeight;
        if (framesAutoRotated)
        {
            var rotation = stream.Rotation ?? 0;
            var rotated = Math.Abs(rotation) == 90;
            frameWidth = rotated ? rawHeight : rawWidth;
            frameHeight = rotated ? rawWidth : rawHeight;
        }
        else
        {
            frameWidth = rawWidth;
            frameHeight = rawHeight;
        }

        var rotate = frameWidth > frameHeight;
        var cellAspect = rotate
            ? (double)frameHeight / frameWidth
            : (double)frameWidth / frameHeight;

        // 必须是偶数：奇数尺寸在 yuv420p 下非法，pad/tile 会直接报错
        var cellWidth = Math.Clamp(
            (int)Math.Round(CellHeight * cellAspect / 2, MidpointRounding.AwayFromZero) * 2,
            MinCellWidth,
            MaxCellWidth);
        var maxWidth = rotate ? CellHeight : cellWidth;

        return (cellWidth, maxWidth, rotate);
    }

    /// <summary>
    /// The time of one cell: cell 0 is at 10% of the runtime, cell 8 at 90% (plus an optional nudge).
    /// </summary>
    private static double SampleTime(double duration, int cell, double nudge)
    {
        var fraction = Math.Clamp(0.10d + (0.80d * cell / (TotalCells - 1)) + nudge, 0.05d, 0.95d);

        if (duration <= 0)
        {
            return Math.Max(1d, 30d * cell);
        }

        // 留出 0.2s 余量，避免正好落在文件末尾导致这一格取不到帧
        return Math.Clamp(duration * fraction, 0.05d, Math.Max(0.05d, duration - 0.2d));
    }

    /// <summary>
    /// Extracts the single frame that represents one cell.
    /// </summary>
    private async Task<string?> BuildCellAsync(
        EncodingJobInfo state,
        string inputArgument,
        string filterChain,
        int quality,
        int threads,
        ProcessPriorityClass priority,
        bool rotate,
        double duration,
        double nudge,
        string suffix,
        int cell,
        string scratch,
        SemaphoreSlim limit,
        CancellationToken cancellationToken)
    {
        var start = SampleTime(duration, cell, nudge);
        var path = Path.Combine(scratch, "cell" + cell.ToString("00", CultureInfo.InvariantCulture) + suffix + ".jpg");
        var rotateFilter = rotate ? ",transpose=1" : string.Empty;

        // 固定用 CPU 的 mjpeg 编码器（trickplay 关闭 EnableHwEncoding 时走的就是这条路）：
        // 硬解的帧由原生滤镜链里的 hwdownload 送回内存，硬件解码本身照旧。
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "-hide_banner -v error -nostdin -y -an -sn -ss {0} {1} -filter_complex \"[0:v]{2}{3}[cell]\" -map \"[cell]\" -frames:v 1 -threads {4} -c:v mjpeg -qscale:v {5} -f image2 \"{6}\"",
            start.ToString("0.000", CultureInfo.InvariantCulture),
            inputArgument,
            filterChain,
            rotateFilter,
            threads.ToString(CultureInfo.InvariantCulture),
            quality.ToString(CultureInfo.InvariantCulture),
            path);

        await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await RunAsync(command, priority, cancellationToken).ConfigureAwait(false) || !IsUsable(path))
            {
                return null;
            }

            return path;
        }
        finally
        {
            limit.Release();
        }
    }

    /// <summary>
    /// Lays the extracted frames out as a 3x3 sheet and burns in duration and file size.
    /// Pure JPEG work - no video decoding, no hardware involvement.
    /// </summary>
    private async Task<bool> ComposeAsync(
        List<string> cells,
        int cellWidth,
        Video video,
        double duration,
        string output,
        CancellationToken cancellationToken)
    {
        var total = Cells * Cells;

        // 少于九格（短视频、黑场、取不到帧）时循环复用已有的帧，版式保持不变
        var sources = new List<string>(total);
        for (var i = 0; i < total; i++)
        {
            sources.Add(cells[i % cells.Count]);
        }

        var filters = new List<string>();

        for (var i = 0; i < total; i++)
        {
            filters.Add(string.Format(
                CultureInfo.InvariantCulture,
                "[{0}:v]scale={1}:{2}:force_original_aspect_ratio=decrease,pad={1}:{2}:(ow-iw)/2:(oh-ih)/2,setsar=1[c{0}]",
                i,
                cellWidth,
                CellHeight));
        }

        for (var row = 0; row < Cells; row++)
        {
            filters.Add(
                string.Concat(Enumerable.Range(0, Cells).Select(c => "[c" + (row * Cells + c).ToString(CultureInfo.InvariantCulture) + "]"))
                + "hstack=" + Cells.ToString(CultureInfo.InvariantCulture)
                + "[r" + row.ToString(CultureInfo.InvariantCulture) + "]");
        }

        filters.Add(
            string.Concat(Enumerable.Range(0, Cells).Select(r => "[r" + r.ToString(CultureInfo.InvariantCulture) + "]"))
            + "vstack=" + Cells.ToString(CultureInfo.InvariantCulture)
            + "[tt]");

        var caption = BuildCaption(video, duration);
        var sheetWidth = cellWidth * Cells;
        if (System.IO.File.Exists(FontPath) && caption.Length > 0)
        {
            var escaped = caption.Replace("\\", "\\\\", StringComparison.Ordinal)
                                 .Replace(":", "\\:", StringComparison.Ordinal);

            // 字号跟着整张图的宽度走，窄画布上才不会顶到右边被切掉
            filters.Add(string.Format(
                CultureInfo.InvariantCulture,
                "[tt]drawtext=fontfile={0}:text='{1}':x={2}:y=h-th-{3}:fontsize={4}:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=16[v]",
                FontPath,
                escaped,
                Math.Max(12, cellWidth / 12),
                Math.Max(12, CellHeight / 12),
                Math.Clamp(sheetWidth / 18, 20, CellHeight / 6)));
        }
        else
        {
            filters.Add("[tt]null[v]");
        }

        var inputs = string.Concat(sources.Select(cell => " -i \"" + cell + "\""));
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "-hide_banner -v error -nostdin -y{0} -filter_complex \"{1}\" -map \"[v]\" -frames:v 1 -q:v 3 \"{2}\"",
            inputs,
            string.Join(';', filters),
            output);

        return await RunAsync(command, ProcessPriorityClass.Normal, cancellationToken).ConfigureAwait(false) && IsUsable(output);
    }

    /// <summary>
    /// Pulls the plain video filter chain out of the native " -vf &quot;...&quot;" argument.
    /// A subtitle filter graph never happens here: no subtitle stream is attached to the job state.
    /// </summary>
    private static string? ExtractFilterChain(string filterArgument)
    {
        if (!filterArgument.StartsWith("-vf ", StringComparison.Ordinal))
        {
            return null;
        }

        var chain = filterArgument[4..].Trim();
        if (chain.Length > 1 && chain[0] == '"' && chain[^1] == '"')
        {
            chain = chain[1..^1];
        }

        return string.IsNullOrWhiteSpace(chain) ? null : chain;
    }

    /// <summary>
    /// 找出偏暗的格子。
    ///
    /// 以前是每格起一个 ffmpeg 做 16x16 灰度解码（一张图多 9 个进程）；现在一条命令把 9 张图
    /// 各缩成 16x16 灰度、按 3x3 排成 48x48 rawvideo 写到 stdout，在 C# 里按键位算每格均值。
    /// 9 个进程 → 1 个，语义完全一样。
    /// </summary>
    private async Task<bool[]> FindDarkCellsAsync(string?[] cells, CancellationToken cancellationToken)
    {
        var flags = new bool[TotalCells];

        // 缺失的格子用第一张存在的图顶上（只为占位，结果不读），保证 9 路输入都是真 JPEG。
        var substitute = cells.FirstOrDefault(cell => cell is not null);
        if (substitute is null)
        {
            return flags;
        }

        var inputs = string.Concat(
            Enumerable.Range(0, TotalCells).Select(cell => " -i \"" + (cells[cell] ?? substitute) + "\""));

        var filters = new List<string>();
        for (var i = 0; i < TotalCells; i++)
        {
            filters.Add(string.Format(CultureInfo.InvariantCulture, "[{0}:v]format=gray,scale=16:16[a{0}]", i));
        }

        for (var row = 0; row < Cells; row++)
        {
            filters.Add(string.Concat(Enumerable.Range(0, Cells).Select(c => "[a" + (row * Cells + c).ToString(CultureInfo.InvariantCulture) + "]"))
                + "hstack=" + Cells.ToString(CultureInfo.InvariantCulture)
                + "[dr" + row.ToString(CultureInfo.InvariantCulture) + "]");
        }

        filters.Add(string.Concat(Enumerable.Range(0, Cells).Select(r => "[dr" + r.ToString(CultureInfo.InvariantCulture) + "]"))
            + "vstack=" + Cells.ToString(CultureInfo.InvariantCulture)
            + "[dark]");

        var command = string.Format(
            CultureInfo.InvariantCulture,
            "-hide_banner -v error -nostdin{0} -filter_complex \"{1}\" -map \"[dark]\" -frames:v 1 -pix_fmt gray -f rawvideo -",
            inputs,
            string.Join(';', filters));

        var raw = await RunCaptureBytesAsync(command, BrightnessTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        const int Side = 16;
        var sheet = Side * Cells;
        if (raw.Length < sheet * sheet)
        {
            return flags;
        }

        for (var cell = 0; cell < TotalCells; cell++)
        {
            if (cells[cell] is null)
            {
                continue;
            }

            var originX = (cell % Cells) * Side;
            var originY = (cell / Cells) * Side;
            var sum = 0d;
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                {
                    sum += raw[((originY + y) * sheet) + originX + x];
                }
            }

            flags[cell] = (sum / (Side * Side)) < MinBrightness;
        }

        return flags;
    }

    /// <summary>
    /// 条目是否落在该用户可访问的库里（IsVisible 只管分级和标签）。
    /// </summary>
    private bool IsInAccessibleLibrary(BaseItem item, User user)
    {
        var access = new InternalItemsQuery(user);
        _libraryManager.ConfigureUserAccess(access, user);
        var allowed = access.TopParentIds;
        if (allowed.Length == 0)
        {
            return false;
        }

        var top = item.GetTopParent();
        return top is not null && allowed.Contains(top.Id);
    }

    private static string BuildCaption(Video video, double duration)
    {
        var parts = new List<string>();

        if (duration > 0)
        {
            var span = TimeSpan.FromSeconds(duration);
            parts.Add(span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"m\:ss", CultureInfo.InvariantCulture));
        }

        try
        {
            if (!string.IsNullOrEmpty(video.Path) && System.IO.File.Exists(video.Path))
            {
                parts.Add(FormatSize(new FileInfo(video.Path).Length));
            }
        }
        catch
        {
            // ignore
        }

        return string.Join("  |  ", parts);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, unit <= 1 ? "{0:0} {1}" : "{0:0.#} {1}", value, units[unit]);
    }

    private async Task<bool> RunAsync(string arguments, ProcessPriorityClass priority, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        TrySetPriority(process, priority);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(CellTimeoutSeconds));

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogDebug("Feature Enhance: ffmpeg exited {Code} for {Arguments}: {Error}", process.ExitCode, arguments, stderr);
            }

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("Feature Enhance: ffmpeg timed out after {Seconds}s, killed: {Arguments}", CellTimeoutSeconds, arguments);
            return false;
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Feature Enhance: could not kill ffmpeg");
        }
    }

    private async Task<byte[]> RunCaptureBytesAsync(string arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return [];
        }

        // 以前这里没有超时：一个卡住的 ffmpeg 会一直挂着请求，只能等客户端断开。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var memory = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(memory, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return memory.ToArray();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            // 拿不到亮度就当"不暗"，与旧实现的失败行为一致（不会因为它多跑一轮重采样）
            return [];
        }
    }

    private void TrySetPriority(Process process, ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Feature Enhance: could not set ffmpeg priority to {Priority}", priority);
        }
    }

    /// <summary>
    /// 宫格缓存的容量控制：超过文件数/字节上限就按 LastWriteTime 从旧到新删到 90%。
    /// 之前完全没有淘汰（实测已经 45MB/228 个文件，按 14.5 万条库规模会把磁盘吃满）。
    /// 最多每 <see cref="SweepInterval"/> 扫一次目录，正常路径几乎零开销。
    /// </summary>
    private void SweepCache(string directory)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now - last < SweepInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last)
        {
            return;
        }

        lock (SweepLock)
        {
            try
            {
                var files = new DirectoryInfo(directory).GetFiles("*.jpg");
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                }

                if (files.Length <= MaxCacheFiles && total <= MaxCacheBytes)
                {
                    return;
                }

                Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                var targetBytes = (long)(MaxCacheBytes * 0.9);
                var targetFiles = (int)(MaxCacheFiles * 0.9);
                var count = files.Length;
                var deleted = 0;

                foreach (var file in files)
                {
                    if (count <= targetFiles && total <= targetBytes)
                    {
                        break;
                    }

                    try
                    {
                        var length = file.Length;
                        file.Delete();
                        total -= length;
                        count--;
                        deleted++;
                    }
                    catch
                    {
                        // 正在被读取/删除失败就跳过，下次再收
                    }
                }

                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "Feature Enhance: grid cache trimmed, removed {Deleted} sheet(s), {Remaining} left, {Bytes} bytes",
                        deleted,
                        count,
                        total);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Feature Enhance: grid cache sweep failed");
            }
        }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
