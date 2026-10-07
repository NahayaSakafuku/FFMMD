using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FFMMD.Player;

/// <summary>
/// 音乐播放:NAudio 输出壳 + AudioTransportLogic 决策。
/// 音频跟随动画主时钟(文档 18.1):位置 = 动画时间 + 偏移,速率 = 动画速度(变调)。
/// 所有公共成员只在 Framework/UI 线程调用;设备播放线程只进入采样链 Read。
/// 文件打开在事件到达时同步进行(只读文件头,毫秒级);不做全曲预解码,避免长阻塞与高内存。
/// </summary>
public sealed unsafe class MusicService : IDisposable, AudioTransportLogic.ISink
{
    private readonly AudioTransportLogic _logic;
    private WaveOutEvent? _output;
    private WaveStream? _stream;
    private VariableSpeedSampleProvider? _speedProvider;
    private VolumeSampleProvider? _volumeProvider;
    private string? _currentPath;
    private double? _pendingSeek;   // 高频 Seek(UI 进度条逐帧拖动)合并到 Framework Tick 应用
    private bool _disposed;
    private bool _gameAudioMuted;   // 音乐出声期间游戏主音量已被归零
    private uint _savedMasterVolume;
    private long _lastMuteErrorLog;

    /// <summary> 当前已加载的音乐文件;null = 未加载/已清除。 </summary>
    public string? CurrentPath => _currentPath;
    /// <summary> 加载/播放错误,显示在音乐区;不影响动画播放。 </summary>
    public string? Error { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == NAudio.Wave.PlaybackState.Playing;

    public MusicService()
    {
        _logic = new AudioTransportLogic(P.Config.MusicOffsetSec, P.Config.MusicEnabled, P.Config.Speed);
        Svc.Framework.Update += Tick;
        // 恢复上次音乐文件;只加载不播放,位置由后续 transport 事件定位。
        var last = P.Config.MusicPath;
        if (last != null && File.Exists(last)) LoadFile(last);
    }

    /// <summary> VmdPlayerService.TransportChanged 的消费者;由 Plugin 装配时接线。 </summary>
    public void OnTransport(TransportEvent e)
    {
        if (_disposed) return;
        if (e.Action == TransportAction.Loaded)
        {
            // 换动作:优先该动作关联的音乐,否则回退全局音乐;都没有则卸载。
            LoadFile(ResolveMusicForMotion(e.Path));
        }
        // 定位类动作总是采用配置中的最新偏移:UI 松手事件若因任何原因未送达,
        // 播放/停止/加载/Seek/循环仍会以当前配置值定位,避免偏移滞留为启动时的旧值。
        if (e.Action is TransportAction.Loaded or TransportAction.Play or TransportAction.Stop
            or TransportAction.Seek or TransportAction.LoopWrap)
        {
            SyncOffsetFromConfig(e);
        }
        _logic.Handle(e, this);
    }

    private void SyncOffsetFromConfig(in TransportEvent e)
    {
        var configOffset = double.IsFinite(P.Config.MusicOffsetSec) ? P.Config.MusicOffsetSec : 0;
        if (Math.Abs(configOffset - _logic.OffsetSec) > 1e-9)
            _logic.SetOffset(configOffset, e.TimeSec, this);
    }

    private string? ResolveMusicForMotion(string? motionPath)
    {
        if (motionPath != null && P.Config.MusicByMotion.TryGetValue(motionPath, out var tied) && File.Exists(tied))
            return tied;
        if (P.Config.MusicPath is { } fallback && File.Exists(fallback))
            return fallback;
        return null;
    }

    // —— UI 操作 ——

    /// <summary> 导入音乐(null = 清除):更新配置关联,加载并同步到当前动画时间;播放中立即接续。 </summary>
    public void ImportMusic(string? path)
    {
        if (path != null)
        {
            P.Config.MusicPath = path;
            if (P.Player.LoadedPath is { } motion)
                P.Config.MusicByMotion[motion] = path;
            P.ConfigDirty = true;
            LoadFile(path);
        }
        else
        {
            var current = _currentPath;
            if (current != null)
            {
                if (P.Player.LoadedPath is { } motion &&
                    P.Config.MusicByMotion.TryGetValue(motion, out var tied) &&
                    string.Equals(tied, current, StringComparison.OrdinalIgnoreCase))
                {
                    P.Config.MusicByMotion.Remove(motion);
                    P.ConfigDirty = true;
                }
                if (string.Equals(P.Config.MusicPath, current, StringComparison.OrdinalIgnoreCase))
                {
                    P.Config.MusicPath = null;
                    P.ConfigDirty = true;
                }
            }
            LoadFile(null);
        }
        // 播放/暂停中导入时,Logic 内部的动画时间已过期;用实时时间定位到"当前画面 + 偏移"。
        _logic.OnFileReplaced(P.Player.TimeSec, this);
    }

    public void SetVolume(float volume)
    {
        volume = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0.8f;
        P.Config.MusicVolume = volume;
        P.ConfigDirty = true;
        ((AudioTransportLogic.ISink)this).SetVolume(volume);
    }

    public void SetOffset(double offsetSec)
    {
        offsetSec = double.IsFinite(offsetSec) ? Math.Clamp(offsetSec, -10.0, 10.0) : 0;
        P.Config.MusicOffsetSec = (float)offsetSec;
        P.ConfigDirty = true;
        // 偏移变化是显式重定位:必须用实时动画时间,Logic 缓存的时间在播放中已过期。
        _logic.SetOffset(offsetSec, P.Player.TimeSec, this);
    }

    public void SetEnabled(bool enabled)
    {
        P.Config.MusicEnabled = enabled;
        P.ConfigDirty = true;
        _logic.SetEnabled(enabled, this);
    }

    private void Tick(IFramework framework)
    {
        if (_disposed) return;
        ApplyPendingSeek();
        UpdateGameAudioMute();
    }

    // —— 游戏原生声音屏蔽 ——

    /// <summary>
    /// 音乐设备出声期间把游戏主音量(SoundMaster)归零,其余状态恢复原值;
    /// 每帧校验,任何路径(停止/暂停/禁用/卸载/开关切换)造成的漂移都会被纠正。
    /// 插件音乐走独立音频设备,不受游戏主音量影响。
    /// </summary>
    private void UpdateGameAudioMute()
    {
        var shouldMute = P.Config.MuteGameAudioWhilePlaying
                         && _output != null && _output.PlaybackState == PlaybackState.Playing;
        if (shouldMute == _gameAudioMuted) return;
        try
        {
            if (shouldMute)
            {
                if (!Svc.GameConfig.TryGet(SystemConfigOption.SoundMaster, out _savedMasterVolume))
                {
                    LogMuteErrorOnce("读取游戏主音量失败,不屏蔽。");
                    _gameAudioMuted = true; // 视为已处理,避免每帧重试;音乐照常播放
                    return;
                }
                Svc.GameConfig.Set(SystemConfigOption.SoundMaster, 0u);
            }
            else
            {
                // 恢复失败必须重试(下一帧再次进入本方法),不能让用户主音量滞留在 0。
                Svc.GameConfig.Set(SystemConfigOption.SoundMaster, _savedMasterVolume);
            }
            _gameAudioMuted = shouldMute;
        }
        catch (Exception e)
        {
            LogMuteErrorOnce($"游戏原生声音{(shouldMute ? "屏蔽" : "恢复")}失败: {e.Message}");
            if (!shouldMute) return; // 恢复失败:保持 muted 状态,下一帧重试
            _gameAudioMuted = true;  // 屏蔽失败:视为已处理,不再每帧重试
        }
    }

    private void LogMuteErrorOnce(string message)
    {
        var now = Environment.TickCount64;
        if (now - _lastMuteErrorLog < 5000) return;
        _lastMuteErrorLog = now;
        PluginLog.Error($"[FFMMD] {message}");
    }

    // —— AudioTransportLogic.ISink ——

    bool AudioTransportLogic.ISink.IsFileLoaded => _output != null && _stream != null;

    void AudioTransportLogic.ISink.LoadFile(string? path) => LoadFile(path);

    void AudioTransportLogic.ISink.Seek(double sourceSec)
    {
        if (_output == null) return;
        _pendingSeek = sourceSec;
    }

    void AudioTransportLogic.ISink.SetPlaying(bool playing)
    {
        ApplyPendingSeek();
        if (_output == null) return;
        if (playing)
        {
            if (_output.PlaybackState != PlaybackState.Playing) _output.Play();
        }
        else if (_output.PlaybackState == PlaybackState.Playing)
        {
            _output.Pause();
        }
    }

    void AudioTransportLogic.ISink.SetSpeed(float speed)
    {
        if (_speedProvider != null && speed is > 0 and >= 0.25f and <= 4f) _speedProvider.Speed = speed;
    }

    void AudioTransportLogic.ISink.SetVolume(float volume)
    {
        if (_volumeProvider != null) _volumeProvider.Volume = Math.Clamp(volume, 0f, 1f);
    }

    /// <summary> 应用待处理的定位:停止设备丢弃已缓冲旧内容(避免定位后先放出残余),定位,按需恢复播放。 </summary>
    private void ApplyPendingSeek()
    {
        var pos = _pendingSeek;
        if (pos == null) return;
        _pendingSeek = null;
        if (_output == null || _speedProvider == null) return;
        var resume = _logic.WantsPlayback(true);
        _output.Stop();
        _speedProvider.Seek(pos.Value);
        if (resume) _output.Play();
    }

    // —— 文件与设备 ——

    private void LoadFile(string? path)
    {
        ReleaseDevice();
        _currentPath = path;
        Error = null;
        if (path == null) return;
        try
        {
            _stream = CreateReader(path);
            _speedProvider = new VariableSpeedSampleProvider(_stream)
            {
                Speed = P.Config.Speed is >= 0.25f and <= 4f ? P.Config.Speed : 1f,
            };
            _volumeProvider = new VolumeSampleProvider(_speedProvider)
            {
                Volume = float.IsFinite(P.Config.MusicVolume) ? Math.Clamp(P.Config.MusicVolume, 0f, 1f) : 0.8f,
            };
            _output = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 2 };
            _output.Init(_volumeProvider);
        }
        catch (Exception e)
        {
            Error = DescribeError(e);
            PluginLog.Error($"[FFMMD] 音乐加载失败 {path}: {e}");
            ReleaseDevice();
        }
    }

    private static WaveStream CreateReader(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("音乐文件不存在。", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wav" => new WaveFileReader(path),
            ".ogg" => new NAudio.Vorbis.VorbisWaveReader(path),
            ".mp3" => new MediaFoundationReader(path),
            _ => throw new NotSupportedException($"不支持的音频格式 {ext};支持 wav / ogg / mp3。"),
        };
    }

    private static string DescribeError(Exception e) => e switch
    {
        FileNotFoundException => "文件不存在。",
        NotSupportedException => e.Message,
        _ => e.Message,
    };

    private void ReleaseDevice()
    {
        _pendingSeek = null;
        try { _output?.Stop(); } catch { /* 设备已失效时 Stop 可能抛出;释放路径尽力而为 */ }
        _output?.Dispose();
        _output = null;
        _stream?.Dispose();
        _stream = null;
        _speedProvider = null;
        _volumeProvider = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Svc.Framework.Update -= Tick;
        ReleaseDevice();
        // 卸载时设备已释放;若主音量仍处于屏蔽状态,强制恢复,防止后台静音残留。
        if (_gameAudioMuted)
        {
            try { Svc.GameConfig.Set(SystemConfigOption.SoundMaster, _savedMasterVolume); }
            catch (Exception e) { PluginLog.Error($"[FFMMD] 卸载时恢复游戏主音量失败: {e.Message}(请在游戏系统设置中手动调回)"); }
            _gameAudioMuted = false;
        }
    }

    /// <summary>
    /// NAudio 适配:把 WaveStream 变成可变速 ISampleProvider。
    /// 输出 WaveFormat 与源一致(采样率/声道不变),变速由采样核心按比率消费源实现。
    /// </summary>
    private sealed class VariableSpeedSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly WaveStream _stream;
        private readonly VariableSpeedSampler _sampler;
        private readonly float[] _frame;

        public WaveFormat WaveFormat => _source.WaveFormat;

        /// <summary> 播放速率;可在播放中修改(音频设备线程与 Framework 线程都安全)。 </summary>
        public double Speed
        {
            get => _sampler.Speed;
            set => _sampler.Speed = value;
        }

        public VariableSpeedSampleProvider(WaveStream stream)
        {
            _stream = stream;
            _source = stream.ToSampleProvider();
            var channels = _source.WaveFormat.Channels;
            _frame = new float[channels];
            _sampler = new VariableSpeedSampler(channels, _source.WaveFormat.SampleRate,
                dest =>
                {
                    var read = _source.Read(_frame, 0, channels);
                    if (read < channels) return false;
                    Array.Copy(_frame, dest, channels);
                    return true;
                },
                sec =>
                {
                    // 统一的源定位:按平均字节率换算并对齐帧边界;不依赖各格式 TimeToByte 的语义差异。
                    var format = _stream.WaveFormat;
                    var bytes = (long)(Math.Max(0, sec) * format.AverageBytesPerSecond);
                    bytes -= bytes % format.BlockAlign;
                    _stream.Position = Math.Clamp(bytes, 0, _stream.Length);
                });
        }

        public void Seek(double sourceSec) => _sampler.Seek(sourceSec);

        public int Read(float[] buffer, int offset, int count) => _sampler.Read(buffer, offset, count);
    }
}
