using FFMMD.Player;

/// <summary>
/// 音乐扩展离线回归:transport 决策状态机 + 变速采样核心。
/// 只编译纯托管生产源(AudioTransportLogic/VariableSpeedSampler),不涉及 NAudio 设备。
/// </summary>
internal static class MusicRegression
{
    /// <summary> 记录指令的音频设备桩。 </summary>
    private sealed class FakeSink : AudioTransportLogic.ISink
    {
        public bool FileLoaded;
        public string? LoadedPath;
        public double? LastSeek;
        public bool Playing;
        public float LastSpeed = 1f;
        public readonly List<string> Log = [];

        bool AudioTransportLogic.ISink.IsFileLoaded => FileLoaded;
        void AudioTransportLogic.ISink.LoadFile(string? path)
        {
            LoadedPath = path;
            FileLoaded = path != null;
            Log.Add($"load:{path ?? "<null>"}");
        }
        void AudioTransportLogic.ISink.Seek(double sourceSec)
        {
            LastSeek = sourceSec;
            Log.Add($"seek:{sourceSec:0.###}");
        }
        void AudioTransportLogic.ISink.SetPlaying(bool playing)
        {
            Playing = playing;
            Log.Add($"play:{playing}");
        }
        void AudioTransportLogic.ISink.SetSpeed(float speed)
        {
            LastSpeed = speed;
            Log.Add($"speed:{speed:0.##}");
        }
        void AudioTransportLogic.ISink.SetVolume(float volume) { }
    }

    private static void Fire(AudioTransportLogic logic, AudioTransportLogic.ISink sink, TransportAction action,
        double time = 0, string? path = null, bool flag = false, float speed = 1f)
        => logic.Handle(new TransportEvent(action, time, path, flag, speed), sink);

    /// <summary> 线性编号帧源:frame f 声道 c 的值 = f * (c + 1)。 </summary>
    private sealed class CountingSource
    {
        public readonly float[][] Frames;
        public readonly int Rate;
        public int Position;

        public CountingSource(int frames, int channels, int rate)
        {
            Frames = new float[frames][];
            Rate = rate;
            for (var f = 0; f < frames; f++)
            {
                Frames[f] = new float[channels];
                for (var c = 0; c < channels; c++) Frames[f][c] = f * (c + 1);
            }
        }

        public bool ReadFrame(float[] dest)
        {
            if (Position >= Frames.Length) return false;
            Array.Copy(Frames[Position], dest, dest.Length);
            Position++;
            return true;
        }

        public void SeekFrame(double sourceSec) => Position = (int)Math.Round(sourceSec * Rate);
    }

    public static int Run()
    {
        var failed = 0;
        var count = 0;
        void Test(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        // ───────── transport 决策状态机 ─────────

        Test("Loaded→Play:从偏移处定位并开始播放", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded, path: "dance.vmd");
            sink.FileLoaded = true; // 壳层已成功加载
            Fire(logic, sink, TransportAction.Play, time: 0);
            Assert(sink.Playing);
            Assert(sink.LastSeek == 0);
        });

        Test("正偏移跳过音轨开头:音频位置 = 动画时间 + 偏移", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(2.5, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play, time: 0);
            Assert(sink.LastSeek == 2.5, $"seek={sink.LastSeek}");
            Fire(logic, sink, TransportAction.Seek, time: 10);
            Assert(sink.LastSeek == 12.5, $"seek={sink.LastSeek}");
        });

        Test("负偏移静音等待:动画 1s + 偏移 -3s → 位置 -2s", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(-3, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play, time: 1);
            Assert(sink.LastSeek == -2, $"seek={sink.LastSeek}");
        });

        Test("暂停/恢复保持播放意图语义", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play);
            Fire(logic, sink, TransportAction.PauseChanged, time: 5, flag: true);
            Assert(!sink.Playing);
            Fire(logic, sink, TransportAction.PauseChanged, time: 5, flag: false);
            Assert(sink.Playing);
        });

        Test("预览 Seek 不自动开始播放", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Seek, time: 12);
            Assert(!sink.Playing);
            Assert(sink.LastSeek == 12);
        });

        Test("停止复位到偏移处且停止播放", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(1.5, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play, time: 30);
            Fire(logic, sink, TransportAction.Stop);
            Assert(!sink.Playing);
            Assert(sink.LastSeek == 1.5, $"seek={sink.LastSeek}");
        });

        Test("循环回绕定位到回绕后时间", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play);
            Fire(logic, sink, TransportAction.LoopWrap, time: 0.1);
            Assert(sink.Playing);
            Assert(sink.LastSeek == 0.1, $"seek={sink.LastSeek}");
        });

        Test("自然结束停音,重新播放从偏移处开始", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play);
            Fire(logic, sink, TransportAction.EndReached, time: 142);
            Assert(!sink.Playing);
            Fire(logic, sink, TransportAction.Play, time: 0);
            Assert(sink.Playing);
            Assert(sink.LastSeek == 0);
        });

        Test("目标失效暂停音频,恢复继续", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play);
            Fire(logic, sink, TransportAction.TargetSuspendChanged, flag: true);
            Assert(!sink.Playing);
            Fire(logic, sink, TransportAction.TargetSuspendChanged, flag: false);
            Assert(sink.Playing);
        });

        Test("物理准备完成时目标仍无效:Play保持静音，恢复目标后继续", () =>
        {
            var sink = new FakeSink { FileLoaded = true };
            var logic = new AudioTransportLogic(1.5, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            Fire(logic, sink, TransportAction.TargetSuspendChanged, flag: true);
            sink.Log.Clear();
            // The target remains absent. No new suspension edge follows Play.
            Fire(logic, sink, TransportAction.Play, time: 12, flag: true);
            Assert(!sink.Playing && !logic.WantsPlayback(true), "unavailable target started music");
            Assert(sink.LastSeek == 13.5, "pending play did not keep its animation position and offset");
            Assert(!sink.Log.Contains("play:True"), "music briefly started before the target returned");
            // Replacing the music or unpausing cannot bypass the suspension.
            logic.OnFileReplaced(12, sink);
            Fire(logic, sink, TransportAction.PauseChanged, time: 12, flag: true);
            Fire(logic, sink, TransportAction.PauseChanged, time: 12, flag: false);
            Assert(!sink.Playing && !sink.Log.Contains("play:True"), "another transport action bypassed suspension");
            Fire(logic, sink, TransportAction.TargetSuspendChanged, flag: false);
            Assert(sink.Playing && logic.WantsPlayback(true), "target recovery lost the play intent");
            Assert(sink.LastSeek == 13.5, "target recovery reset playback position");
        });

        Test("默认Play恢复旧的正常播放语义且Stop清除等待意图", () =>
        {
            var sink = new FakeSink { FileLoaded = true };
            var logic = new AudioTransportLogic(-.5, true, 1f);
            Fire(logic, sink, TransportAction.Play, time: 4, flag: true);
            Assert(!sink.Playing, "suspended play started music");
            Fire(logic, sink, TransportAction.Stop);
            Fire(logic, sink, TransportAction.TargetSuspendChanged, flag: false);
            Assert(!sink.Playing, "Stop allowed target recovery to revive pending playback");
            Fire(logic, sink, TransportAction.TargetSuspendChanged, flag: true);
            Fire(logic, sink, TransportAction.Play, time: 7);
            Assert(sink.Playing && sink.LastSeek == 6.5, "default Play no longer clears an old suspension");
        });

        Test("速度与动画一致下发", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.SpeedChanged, speed: 2.5f);
            Assert(sink.LastSpeed == 2.5f);
        });

        Test("音乐禁用时即使播放也不出声,启用后恢复", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, false, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play);
            Assert(!sink.Playing);
            logic.SetEnabled(true, sink);
            Assert(sink.Playing);
        });

        Test("未加载文件时不发定位指令也不播放", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded, path: "dance.vmd"); // 无关联音乐 → 卸载
            Assert(!sink.FileLoaded);
            Fire(logic, sink, TransportAction.Play);
            Assert(!sink.Playing);
            Assert(sink.LastSeek == null, "未加载文件不应下发 Seek");
        });

        Test("偏移修改即时重定位到当前动画时间", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play, time: 7);
            logic.SetOffset(-1, 7, sink);
            Assert(sink.LastSeek == 6, $"seek={sink.LastSeek}");
        });

        Test("播放中修改偏移使用实时动画时间而非缓存值", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play, time: 0); // 缓存 _animTime = 0
            // 动画已推进到 40s(真实环境由 MusicService 传入 Player.TimeSec)
            logic.SetOffset(2, 40, sink);
            Assert(sink.LastSeek == 42, $"seek={sink.LastSeek},应为 40+2;若为 2 说明用了过期缓存时间");
        });

        Test("播放中导入音乐定位到当前动画时间加偏移", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(1, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play, time: 0);
            logic.OnFileReplaced(30, sink);
            Assert(sink.Playing, "播放中导入音乐应立即接续播放");
            Assert(sink.LastSeek == 31, $"seek={sink.LastSeek},应为 30+1");
        });

        Test("Disposing 停止播放", () =>
        {
            var sink = new FakeSink();
            var logic = new AudioTransportLogic(0, true, 1f);
            Fire(logic, sink, TransportAction.Loaded);
            sink.FileLoaded = true;
            Fire(logic, sink, TransportAction.Play);
            Fire(logic, sink, TransportAction.Disposing);
            Assert(!sink.Playing);
        });

        // ───────── 变速采样核心 ─────────

        Test("1x 原速:输出与源逐帧一致", () =>
        {
            const int rate = 8, frames = 100;
            var src = new CountingSource(frames, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            var output = new float[frames];
            Assert(sampler.Read(output, 0, frames) == frames);
            for (var i = 0; i < frames; i++)
                Assert(Math.Abs(output[i] - i) < 1e-4f, $"output[{i}]={output[i]} != {i}");
        });

        Test("2x 变速:输出第 i 帧对应源第 2i 帧(时间压缩)", () =>
        {
            const int rate = 8;
            var src = new CountingSource(200, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            sampler.Speed = 2;
            var output = new float[80];
            Assert(sampler.Read(output, 0, 80) == 80);
            for (var i = 0; i < 80; i++)
                Assert(Math.Abs(output[i] - 2 * i) < 1e-3f, $"output[{i}]={output[i]} != {2 * i}");
        });

        Test("0.5x 变速:整数帧精确,半帧为均值插值", () =>
        {
            const int rate = 8;
            var src = new CountingSource(100, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            sampler.Speed = 0.5;
            var output = new float[40];
            Assert(sampler.Read(output, 0, 40) == 40);
            for (var i = 0; i < 40; i++)
            {
                var expected = i % 2 == 0 ? i / 2f : (i - 1) / 2f + 0.5f;
                Assert(Math.Abs(output[i] - expected) < 1e-3f, $"output[{i}]={output[i]} != {expected}");
            }
        });

        Test("1.5x 非整数比率插值", () =>
        {
            const int rate = 8;
            var src = new CountingSource(100, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            sampler.Speed = 1.5;
            var output = new float[32];
            Assert(sampler.Read(output, 0, 32) == 32);
            for (var i = 0; i < 32; i++)
            {
                var pos = 1.5 * i;
                var lo = (int)Math.Floor(pos);
                var expected = lo + (pos - lo);
                Assert(Math.Abs(output[i] - expected) < 1e-2f, $"output[{i}]={output[i]} != {expected}");
            }
        });

        Test("负位置静音等待,跨越 0 后从源 0 帧进入", () =>
        {
            const int rate = 8;
            var src = new CountingSource(100, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            sampler.Seek(-0.5); // 4 帧
            var output = new float[12];
            Assert(sampler.Read(output, 0, 12) == 12);
            for (var i = 0; i < 4; i++)
                Assert(output[i] == 0, $"等待区 output[{i}]={output[i]} 应为静音");
            for (var i = 4; i < 12; i++)
                Assert(Math.Abs(output[i] - (i - 4)) < 1e-4f, $"output[{i}]={output[i]} != {i - 4}");
        });

        Test("越过源末尾输出静音,Seek 回退可恢复", () =>
        {
            const int rate = 8, frames = 16;
            var src = new CountingSource(frames, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            sampler.Seek(frames); // 末尾之后
            var output = new float[8];
            Assert(sampler.Read(output, 0, 8) == 8);
            Assert(output.All(v => v == 0), "EOF 后应为静音");
            sampler.Seek(0);
            Assert(sampler.Read(output, 0, 8) == 8);
            for (var i = 0; i < 8; i++)
                Assert(Math.Abs(output[i] - i) < 1e-4f, $"回退后 output[{i}]={output[i]} != {i}");
        });

        Test("播放中途 Seek 重新定位", () =>
        {
            const int rate = 8;
            var src = new CountingSource(100, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            var output = new float[4];
            Assert(sampler.Read(output, 0, 4) == 4);
            sampler.Seek(0.5); // 帧 4
            Assert(sampler.Read(output, 0, 4) == 4);
            for (var i = 0; i < 4; i++)
                Assert(Math.Abs(output[i] - (4 + i)) < 1e-4f, $"output[{i}]={output[i]} != {4 + i}");
        });

        Test("立体声交错输出与声道独立", () =>
        {
            const int rate = 8;
            var src = new CountingSource(100, 2, 8); // 帧 f → (f, 2f)
            var sampler = new VariableSpeedSampler(2, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            var output = new float[12];
            Assert(sampler.Read(output, 0, 12) == 12);
            for (var f = 0; f < 6; f++)
            {
                Assert(Math.Abs(output[f * 2] - f) < 1e-4f, $"L[{f}]={output[f * 2]} != {f}");
                Assert(Math.Abs(output[f * 2 + 1] - 2 * f) < 1e-4f, $"R[{f}]={output[f * 2 + 1]} != {2 * f}");
            }
        });

        Test("变速连续修改:播放中从 1x 切到 2x 位置连续", () =>
        {
            const int rate = 8;
            var src = new CountingSource(400, 1, 8);
            var sampler = new VariableSpeedSampler(1, rate, src.ReadFrame, sec => src.SeekFrame(sec));
            var output = new float[8];
            Assert(sampler.Read(output, 0, 8) == 8);
            sampler.Speed = 2; // 下一帧应从源帧 8 继续(时间轴连续)
            Assert(sampler.Read(output, 0, 8) == 8);
            for (var i = 0; i < 8; i++)
                Assert(Math.Abs(output[i] - (8 + 2 * i)) < 1e-3f, $"output[{i}]={output[i]} != {8 + 2 * i}");
        });

        Console.WriteLine($"音乐回归:{count - failed}/{count} 通过");
        return failed;
    }

    private static void Assert(bool condition, string message = "assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
