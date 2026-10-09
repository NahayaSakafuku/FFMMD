namespace FFMMD;

/// <summary> 全局校准参数：坐标约定差异全部收敛在这里，避免为调方向改代码。 </summary>
public class Calibration
{
    /// <summary> 仅为旧配置反序列化保留；新管线不读取全局翻轴。 </summary>
    public bool RotateX180;
    public bool RotateY180;
    public bool RotateZ180;
    /// <summary>0=标准 A 姿态，1=标准 T 姿态；源 PMX 优先。</summary>
    public int SourceRestPose;

    /// <summary> 整体偏航角（度）：动作整体朝向修正。 </summary>
    public float YawDegrees;

    /// <summary> 动作幅度：1 = 完整腿姿，0 = 目标绑定姿态。 </summary>
    public float MotionScale = 1.0f;

    /// <summary> 按源、目标有效腿长换算整体移动；不改变保留腿姿的比例适配。 </summary>
    public bool AutoPositionScale = true;

    /// <summary> 自动缩放关闭时，整体移动的 MMD→FF14 单位系数。 </summary>
    public float ManualPositionScale = 0.09f;
    /// <summary>整体高度偏移，使用根平移，不修改骨长或缩放。</summary>
    public float HeightOffset;
    /// <summary>旧配置兼容；不再锁定质心高度，使用根／地面放置基准。</summary>
    public bool LockHeight;

    /// <summary> 仅为旧配置兼容保留；新管线不读取全段中位数。 </summary>
    public bool MedianBaseline;

    /// <summary>
    /// 腿部驱动：0=FK，1=强制足 IK，2=按 VMD 的 IK 时间轴自动驱动（缺省开启）。
    /// </summary>
    public int LegIkMode = 2;

}

public class Config
{
    public int RigPipelineVersion;
    /// <summary> 旧版单条关联,仅为配置兼容保留;新逻辑使用 SourcePmxByMotion。 </summary>
    public string? SourcePmxPath;
    public string? SourcePmxMotionPath;
    /// <summary> 动作路径 → 源 PMX 路径;多角色槽下每个动作各自记忆源骨架。 </summary>
    public Dictionary<string, string> SourcePmxByMotion = new(StringComparer.OrdinalIgnoreCase);
    /// <summary> 自动为新动作准备裙骨物理;已有缓存直接复用。 </summary>
    public bool AutoSkirtPhysics = true;
    /// <summary> 可选的高级裙骨物理参考 PMX;为空时使用内置参考。 </summary>
    public string? SkirtPhysicsPmxPath;
    /// <summary> 动作路径 → 已准备的裙骨缓存路径。 </summary>
    public Dictionary<string, string> SkirtBakeByMotion = new(StringComparer.OrdinalIgnoreCase);
    /// <summary> 旧版配置兼容字段；缺少源参考变换的实验 rest 模式已停用。 </summary>
    public int RetargetMode;


    public string? LastVmdPath;
    public bool Loop = true;
    public float Speed = 1.0f;

    // —— 音乐(独立于动画校准参数;改变音乐状态不触发骨架缓存重建)——
    /// <summary> 最近一次导入的音乐,也是未关联动作的回退音乐。 </summary>
    public string? MusicPath;
    public bool MusicEnabled = true;
    public float MusicVolume = 0.8f;
    /// <summary> 音频位置 = 动画时间 + 偏移;正=跳过音轨开头,负=音乐延后进入。 </summary>
    public float MusicOffsetSec;
    /// <summary> 动作路径 → 音乐路径;未关联的动作回退 MusicPath。 </summary>
    public Dictionary<string, string> MusicByMotion = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 全局快捷键(Windows VirtualKey 数值,0 = 未绑定):窗口关闭时仍有效。
    /// 默认小键盘 1/2/3,聊天打字与小键盘冲突概率最低。
    /// </summary>
    public int PlayHotkey = 0x62;    // VirtualKey.NUMPAD1
    public int PauseHotkey = 0x63;   // VirtualKey.NUMPAD2
    public int StopHotkey = 0x64;    // VirtualKey.NUMPAD3

    /// <summary>
    /// 播放音乐期间把游戏主音量(SoundMaster)临时归零以屏蔽原生音轨;
    /// 停止/暂停/禁用/卸载音乐或插件时恢复原值。插件音乐走独立音频设备,不受影响。
    /// </summary>
    public bool MuteGameAudioWhilePlaying = true;

    /// <summary> UI 语言:0=自动(国服客户端为中文,其余 English),1=中文,2=English。 </summary>
    public int Language;

    /// <summary> GPose 会隐藏聊天框导致 /ffmmd 无法输入，进入时自动弹出插件窗口。 </summary>
    public bool AutoOpenInGPose = true;

    /// <summary> 播放目标：0 = 自己，1 = 当前目标，2 = 附近角色列表（NearbyObjectId）。 </summary>
    public int TargetMode;
    public ulong NearbyObjectId;

    public Calibration Cal = new();

    public void Normalize()
    {
        RetargetMode = 0;
        Cal ??= new Calibration();
        if (RigPipelineVersion < 2)
        {
            Cal.RotateX180 = Cal.RotateY180 = Cal.RotateZ180 = Cal.MedianBaseline = false;
            Cal.AutoPositionScale = true;
            RigPipelineVersion = 2;
        }
        Cal.LockHeight=false;
        RigPipelineVersion=Math.Max(RigPipelineVersion,5);
        Cal.SourceRestPose = Math.Clamp(Cal.SourceRestPose, 0, 1);
        Speed = float.IsFinite(Speed) ? Math.Clamp(Speed, 0.25f, 4) : 1;
        TargetMode = Math.Clamp(TargetMode, 0, 2);
        Cal.LegIkMode = Math.Clamp(Cal.LegIkMode, 0, 2);
        Cal.MotionScale = float.IsFinite(Cal.MotionScale) ? Math.Clamp(Cal.MotionScale, 0, 1) : 1;
        Cal.YawDegrees = float.IsFinite(Cal.YawDegrees) ? Math.Clamp(Cal.YawDegrees, -180, 180) : 0;
        Cal.ManualPositionScale = float.IsFinite(Cal.ManualPositionScale) ? Math.Clamp(Cal.ManualPositionScale, 0, 0.5f) : 0.09f;
        Cal.HeightOffset = float.IsFinite(Cal.HeightOffset) ? Math.Clamp(Cal.HeightOffset,-3,3) : 0;
        MusicVolume = float.IsFinite(MusicVolume) ? Math.Clamp(MusicVolume, 0f, 1f) : 0.8f;
        MusicOffsetSec = float.IsFinite(MusicOffsetSec) ? Math.Clamp(MusicOffsetSec, -10f, 10f) : 0;
        MusicByMotion ??= new(StringComparer.OrdinalIgnoreCase);
        SkirtPhysicsPmxPath = string.IsNullOrWhiteSpace(SkirtPhysicsPmxPath) ? null : SkirtPhysicsPmxPath.Trim();
        var skirtAssociations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (SkirtBakeByMotion != null)
            foreach (var association in SkirtBakeByMotion) skirtAssociations[association.Key] = association.Value;
        SkirtBakeByMotion = skirtAssociations;
        if (PlayHotkey < 0) PlayHotkey = 0;
        if (PauseHotkey < 0) PauseHotkey = 0;
        if (StopHotkey < 0) StopHotkey = 0;
        Language = Math.Clamp(Language, 0, 2);
        // 旧版单条源 PMX 关联迁移到按动作字典(TryAdd 幂等);旧字段保留原值不置空,
        // 与 RotateX180 等兼容字段同策略——运行时新逻辑只读字典。
        SourcePmxByMotion ??= new(StringComparer.OrdinalIgnoreCase);
        if (SourcePmxPath is { } legacyPmx && SourcePmxMotionPath is { } legacyMotion && File.Exists(legacyPmx))
            SourcePmxByMotion.TryAdd(legacyMotion, legacyPmx);
    }
}
