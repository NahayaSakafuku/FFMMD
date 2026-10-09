namespace FFMMD.Player;

/// <summary> 播放器 transport 动作;由 VmdPlayerService 在状态变化的控制点发出。 </summary>
public enum TransportAction
{
    /// <summary> 新动画载入成功(Path = VMD 路径),时间 0、未播放。 </summary>
    Loaded,
    /// <summary> 用户开始播放(TimeSec 已定位;若已到末尾则归 0)。Flag = 当前播放环境无效。 </summary>
    Play,
    /// <summary> 暂停切换(Flag = 暂停后状态)。 </summary>
    PauseChanged,
    /// <summary> 停止:时间 0、姿态覆盖停用。 </summary>
    Stop,
    /// <summary> 定位到 TimeSec(播放状态不变)。 </summary>
    Seek,
    /// <summary> 循环回绕(TimeSec = 回绕后时间)。 </summary>
    LoopWrap,
    /// <summary> 非循环自然播完,保持末帧;音频同样停在当前位置。 </summary>
    EndReached,
    /// <summary> 时钟冻结环境(目标有效 + GPose)失效/恢复;Flag = 是否失效。 </summary>
    TargetSuspendChanged,
    /// <summary> 播放速度变化(Speed = 新速度)。 </summary>
    SpeedChanged,
    /// <summary> 插件卸载,音频必须停止并释放。 </summary>
    Disposing,
}

public readonly record struct TransportEvent(TransportAction Action, double TimeSec, string? Path = null, bool Flag = false, float Speed = 0f);

/// <summary>
/// 音频 transport 决策状态机(纯托管、无游戏与音频库依赖,可离线测试)。
/// 消费 VmdPlayerService 的 transport 事件,对音频设备(sink)下达定位/播放/变速指令。
/// 位置定义:音频源位置 = 动画时间 + 偏移;正偏移跳过音轨开头,负偏移静音等待后进入。
/// 音频位置推进与动画时钟一致(变速变调,跟随动画速度),不在此处做漂移修正。
/// </summary>
public sealed class AudioTransportLogic
{
    /// <summary> 音频设备抽象;由 NAudio 壳实现,测试用记录桩实现。 </summary>
    public interface ISink
    {
        /// <summary> 音频文件是否已成功加载(有效输出链就绪)。 </summary>
        bool IsFileLoaded { get; }
        /// <summary> 加载文件;null = 卸载。失败时 IsFileLoaded 应保持/变为 false 并记录错误。 </summary>
        void LoadFile(string? path);
        /// <summary> 定位到源位置(秒,可为负;负值表示静音等待)。实现可合并高频调用。 </summary>
        void Seek(double sourceSec);
        /// <summary> 要求设备进入播放/暂停(非播放)。 </summary>
        void SetPlaying(bool playing);
        /// <summary> 播放速率(与动画 Speed 一致,变调)。 </summary>
        void SetSpeed(float speed);
        /// <summary> 音量 0-1。 </summary>
        void SetVolume(float volume);
    }

    private bool _playing;      // transport 播放意图 = Playing && !Paused
    private bool _suspended;    // 目标/GPose 环境失效(时钟冻结)
    private bool _enabled;
    private double _animTime;   // 最近一次已知的动画时间
    private double _offsetSec;
    private float _speed = 1f;

    public AudioTransportLogic(double offsetSec, bool enabled, float speed)
    {
        _offsetSec = offsetSec;
        _enabled = enabled;
        _speed = speed > 0 ? speed : 1f;
    }

    public bool WantsPlayback(bool fileLoaded) => _playing && !_suspended && _enabled && fileLoaded;
    public double OffsetSec => _offsetSec;
    public double AnimTimeSec => _animTime;

    private void Apply(ISink sink) => sink.SetPlaying(WantsPlayback(sink.IsFileLoaded));

    private void SeekSink(ISink sink, double sourceSec)
    {
        if (sink.IsFileLoaded) sink.Seek(sourceSec);
    }

    public void Handle(in TransportEvent e, ISink sink)
    {
        switch (e.Action)
        {
            case TransportAction.Loaded:
                // 文件切换由壳层在收到 Loaded 时先行处理;这里复位 transport 并定位到偏移处待命。
                _animTime = 0;
                _playing = false;
                _suspended = false;
                SeekSink(sink, _offsetSec);
                Apply(sink);
                break;
            case TransportAction.Play:
                _animTime = e.TimeSec;
                _playing = true;
                _suspended = e.Flag;
                SeekSink(sink, _animTime + _offsetSec);
                Apply(sink);
                break;
            case TransportAction.PauseChanged:
                _playing = !e.Flag;
                _animTime = e.TimeSec;
                Apply(sink);
                break;
            case TransportAction.Stop:
                _animTime = 0;
                _playing = false;
                _suspended = false;
                SeekSink(sink, _offsetSec);
                Apply(sink);
                break;
            case TransportAction.Seek:
                // 预览/对照定位:只定位,不改变播放状态。
                _animTime = e.TimeSec;
                SeekSink(sink, _animTime + _offsetSec);
                break;
            case TransportAction.LoopWrap:
                _animTime = e.TimeSec;
                SeekSink(sink, _animTime + _offsetSec);
                break;
            case TransportAction.EndReached:
                _animTime = e.TimeSec;
                _playing = false;
                Apply(sink);
                break;
            case TransportAction.TargetSuspendChanged:
                _suspended = e.Flag;
                Apply(sink);
                break;
            case TransportAction.SpeedChanged:
                if (e.Speed > 0)
                {
                    _speed = e.Speed;
                    sink.SetSpeed(e.Speed);
                }
                break;
            case TransportAction.Disposing:
                _playing = false;
                _suspended = true;
                Apply(sink);
                break;
        }
    }

    /// <summary>
    /// 用户导入/清除音乐:壳层已执行 LoadFile,这里按传入的当前动画时间定位并恢复播放状态。
    /// 动画播放中导入时,内部缓存的 _animTime 已经过期,必须使用实时时间。
    /// </summary>
    public void OnFileReplaced(double animTimeSec, ISink sink)
    {
        _animTime = animTimeSec;
        SeekSink(sink, _animTime + _offsetSec);
        Apply(sink);
    }

    public void SetOffset(double offsetSec, double animTimeSec, ISink sink)
    {
        _offsetSec = offsetSec;
        _animTime = animTimeSec;
        SeekSink(sink, _animTime + _offsetSec);
    }

    public void SetEnabled(bool enabled, ISink sink)
    {
        _enabled = enabled;
        Apply(sink);
    }
}
