using System.Threading;

namespace FFMMD.Player;

/// <summary>
/// 变速(变调)采样核心:输出采样率不变,按 Speed 比率消费源采样,线性插值,
/// 行为等同把源流以 Speed 倍速率播放(音调随速度变化,与动画变速一致)。
/// 支持负位置(静音等待)与越过源末尾(静音),输出恒定填满请求长度以保持设备缓冲连续。
/// 纯托管、无音频库依赖,可离线测试。
/// </summary>
/// <remarks>
/// 线程模型:Read 在音频设备播放线程调用;Seek 在 Framework 线程调用(调用方必须保证
/// Seek 时设备已停止取数,即与 Read 无并发);Speed 可随时用 Interlocked 安全修改。
/// </remarks>
public sealed class VariableSpeedSampler
{
    /// <summary> 从源顺序读取一帧(所有声道);返回 false 表示源结束。 </summary>
    public delegate bool FrameReader(float[] dest);
    /// <summary> 把源重定位到给定源秒(非负,帧对齐由实现负责)。 </summary>
    public delegate void SourceSeeker(double sourceSec);

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _prev, _next, _frame;
    private readonly FrameReader _readFrame;
    private readonly SourceSeeker? _seekSource;

    private long _speedBits = BitConverter.DoubleToInt64Bits(1.0);
    private double _pos;          // 输出时间轴位置 = 源秒(可负)
    private long _startFrame;     // 当前源定位的起始帧号
    private long _nextFrameIdx = -1; // _next 所存帧号(_hasNext 时有效)
    private long _pendingFrame;   // 下一待读帧号
    private bool _hasNext, _hasPrev, _eof;

    public VariableSpeedSampler(int channels, int sampleRate, FrameReader readFrame, SourceSeeker? seekSource = null)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _channels = channels;
        _sampleRate = sampleRate;
        _readFrame = readFrame ?? throw new ArgumentNullException(nameof(readFrame));
        _seekSource = seekSource;
        _prev = new float[channels];
        _next = new float[channels];
        _frame = new float[channels];
    }

    /// <summary> 播放速率(1 = 原速;可在播放中修改)。 </summary>
    public double Speed
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _speedBits));
        set
        {
            var v = double.IsFinite(value) && value > 0 ? value : 1.0;
            Interlocked.Exchange(ref _speedBits, BitConverter.DoubleToInt64Bits(v));
        }
    }

    /// <summary>
    /// 定位输出时间轴位置(源秒,可负)。负位置:静音直到推进跨越 0 后从源 0 帧进入;
    /// 非负位置:立即重定位源。设备必须在调用此方法前停止取数。
    /// </summary>
    public void Seek(double sourceSec)
    {
        if (!double.IsFinite(sourceSec)) return;
        _pos = sourceSec;
        _hasNext = _hasPrev = false;
        _eof = false;
        _nextFrameIdx = -1;
        if (sourceSec >= 0) LocateSource(sourceSec);
        // 负位置:源保持未定位状态,Read 推进到 t≥0 时再定位到 0。
    }

    private void LocateSource(double sec)
    {
        sec = Math.Max(0, sec);
        var frame = (long)(sec * _sampleRate);
        _seekSource?.Invoke((double)frame / _sampleRate);
        _startFrame = frame;
        _pendingFrame = frame;
        _nextFrameIdx = -1;
        _hasNext = _hasPrev = false;
        _eof = false;
    }

    /// <summary> 读取 count 个交错采样(帧数×声道),恒返回写入长度(不足处静音)。 </summary>
    public int Read(float[] buffer, int offset, int count)
    {
        var speed = Speed;
        var stepSec = speed / _sampleRate;
        var frames = count / _channels;
        for (var f = 0; f < frames; f++)
        {
            var dest = offset + f * _channels;
            var t = _pos + f * stepSec;
            if (t < 0)
            {
                // 跨越 0 时再定位源;负位置期间保持静音等待。
                if (t + stepSec >= 0) LocateSource(0);
                Clear(buffer, dest);
                continue;
            }
            if (!SampleAt(t, buffer, dest)) Clear(buffer, dest);
        }
        _pos += frames * stepSec;
        return frames * _channels;
    }

    private void Clear(float[] buffer, int offset) => Array.Clear(buffer, offset, _channels);

    /// <summary> 写出源时间 t 处的插值采样;源未覆盖时返回 false(静音)。 </summary>
    private bool SampleAt(double t, float[] buffer, int dest)
    {
        var idxD = t * _sampleRate;
        if (idxD < 0) return false;
        var idx = (long)Math.Floor(idxD);

        // 保证帧 idx 与 idx+1 已读入(顺序读;Seek 后从定位点继续)。
        while (!_eof && _pendingFrame <= idx + 1)
        {
            if (_hasNext)
            {
                Array.Copy(_next, _prev, _channels);
                _hasPrev = true;
            }
            if (_readFrame(_frame))
            {
                Array.Copy(_frame, _next, _channels);
                _nextFrameIdx = _pendingFrame;
                _hasNext = true;
                _pendingFrame = _nextFrameIdx + 1;
            }
            else
            {
                _eof = true;
            }
        }

        if (!_hasNext) return false;
        if (idx < _startFrame && _nextFrameIdx < idx) return false;

        if (idx + 1 > _nextFrameIdx)
        {
            // EOF 且 floor 帧就是最后一帧:无上帧可插值,输出原值;再往后由 idxD 边界静音。
            if (idx != _nextFrameIdx) return false;
            CopyFrame(_next, buffer, dest);
            return true;
        }

        if (!_hasPrev)
        {
            // Seek 起点恰好落在 idx,无前一帧:输出原值(误差小于一帧)。
            CopyFrame(_next, buffer, dest);
            return true;
        }

        var frac = (float)(idxD - idx);
        for (var c = 0; c < _channels; c++)
            buffer[dest + c] = _prev[c] + (_next[c] - _prev[c]) * frac;
        return true;
    }

    private void CopyFrame(float[] src, float[] dest, int destOffset)
    {
        for (var c = 0; c < _channels; c++) dest[destOffset + c] = src[c];
    }
}
