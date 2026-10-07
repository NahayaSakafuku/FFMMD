using Lumina.Data;
using Lumina.Data.Files.Excel;

namespace FFMMD;

public enum PluginLanguage
{
    Auto = 0,
    Chinese = 1,
    English = 2,
}

/// <summary>
///     双语 UI 文案。Auto 规则:游戏 Status 表声明为简体中文(国服客户端)→ 中文,
///     其余(国际服日/英/德/法)→ English;可在设置里手动固定。
///     仅覆盖 UI 与用户可见消息;诊断 dump、PMX 解析警告和日志保持原样。
/// </summary>
public static class Loc
{
    private static bool? _chineseClient;
    private static Strings? _current;

    public static Strings S => _current ??= Strings.Make(IsChinese);

    /// <summary> 当前解析出的语言(供一次性创建的 UI 使用)。 </summary>
    public static bool IsChinese
    {
        get
        {
            if (_chineseClient == null)
            {
                try
                {
                    _chineseClient = Setting switch
                    {
                        1 => true,
                        2 => false,
                        _ => DetectChineseClient(),
                    };
                }
                catch
                {
                    _chineseClient = true;
                }
            }
            return _chineseClient.Value;
        }
    }

    private static int Setting => P.Config?.Language ?? 0;

    /// <summary> 语言设置变化后调用,使文案表重新解析。 </summary>
    public static void Invalidate()
    {
        _chineseClient = null;
        _current = null;
    }

    /// <summary> 国服客户端的 Status.exh 声明为 ChineseSimplified;国际服没有该语言变体。 </summary>
    private static bool DetectChineseClient()
    {
        var exh = Svc.Data.GetFile<ExcelHeaderFile>("exd/status.exh");
        return exh?.Languages.Contains(Language.ChineseSimplified) ?? false;
    }
}

public class Strings
{
    // 状态行 / 通用
    public string HookOk = "";
    public string HookMissing = "";      // {0}
    public string TargetName = "";       // {0}
    public string TargetNone = "";
    public string GposeLabel = "";

    // 播放目标
    public string TargetModeTitle = "";
    public string TargetSelf = "";
    public string TargetCurrent = "";
    public string TargetNearby = "";
    public string PickNearby = "";
    public string NearbyDist = "";       // {0} {1}

    // 加载 / 走带
    public string ImportVmd = "";
    public string LoadFailed = "";       // {0}
    public string NoMotionLoaded = "";
    public string Play = "";
    public string Pause = "";
    public string Stop = "";
    public string Loop = "";
    public string Speed = "";
    public string Progress = "";         // {0} {1} {2}
    public string AutoOpenGpose = "";

    // 音乐
    public string MusicTitle = "";
    public string ImportMusic = "";
    public string ClearMusic = "";
    public string MusicEnabled = "";
    public string MusicError = "";       // {0}
    public string MusicNone = "";
    public string MusicVolume = "";
    public string MusicOffset = "";
    public string ResetDefaults = "";
    public string ResetDefaultsTip = "";
    public string MuteGameAudio = "";
    public string MuteGameAudioTip = "";
    public string MusicHelp = "";

    // 快捷键
    public string Hotkeys = "";
    public string HkPlay = "";
    public string HkPause = "";
    public string HkStop = "";
    public string HkNone = "";
    public string HkNumpad = "";         // + 数字
    public string HkTip = "";
    public string HkHelp = "";

    // 校准 / 源骨架
    public string CalSection = "";
    public string SourceNone = "";
    public string SourceApprox = "";     // {0}
    public string SourcePmxStatus = "";  // {0}
    public string StandardPose = "";
    public string StandardPoseA = "";
    public string StandardPoseT = "";
    public string PickSourcePmx = "";
    public string UseStandardSource = "";
    public string LegIkTitle = "";
    public string LegIkFk = "";
    public string LegIkForce = "";
    public string LegIkVmd = "";
    public string YawDegrees = "";
    public string MotionScale = "";
    public string AutoPosScale = "";
    public string ManualPosScale = "";
    public string PlacementHint = "";
    public string HeightOffset = "";
    public string HeightReset = "";
    public string HeightHint = "";
    public string PosScaleHint = "";

    // 音乐 / 加载错误消息
    public string MusicFormatUnsupported = ""; // {0}
    public string MusicFileMissing = "";
    public string SourcePmxMissing = "";       // {0}
    public string LoadNoBoneTracks = "";
    public string LoadEnterGpose = "";
    public string PickVmdTitle = "";
    public string VmdFilter = "";
    public string PickMusicTitle = "";
    public string MusicFilter = "";
    public string PickPmxTitle = "";
    public string PmxFilter = "";

    // 调试
    public string DebugSection = "";
    public string ExportDiag = "";
    public string VideoTimes = "";
    public string FingerTimes = "";
    public string ReadSkeleton = "";
    public string CheckFrame = "";
    public string NoSkelCache = "";
    public string SkelSummary = "";      // {0} {1} {2}
    public string MappedSummary = "";    // {0} {1}
    public string UnmappedTitle = "";    // {0}
    public string MappingDetail = "";
    public string MappedTrack = "";      // {0} {1} {2}
    public string MappedInherit = "";    // {0} {1}
    public string BoneList = "";         // {0}
    public string RootLabel = "";

    // 诊断状态
    public string DiagNeedGpose = "";
    public string DiagWaiting = "";
    public string DiagNoWrite = "";
    public string DiagTimeout = "";
    public string DiagExported = "";     // {0}
    public string DiagSaveFailed = "";   // {0}
    public string ScaleAudit = "";       // {0} {1} {2}
    public string ScaleNotCaptured = "";
    public string CmdDesc = "";

    // 语言选项
    public string LangLabel = "";
    public string LangAuto = "";
    public string LangChinese = "";
    public string LangEnglish = "";

    public static Strings Make(bool zh) => zh ? Chinese() : English();

    private static Strings Chinese() => new()
    {
        HookOk = "骨骼Hook正常",
        HookMissing = "骨骼Hook未命中:{0}",
        TargetName = "目标: {0}",
        TargetNone = "目标: 无(选一个角色)",
        GposeLabel = "GPose",

        TargetModeTitle = "播放目标",
        TargetSelf = "自己",
        TargetCurrent = "当前目标",
        TargetNearby = "附近角色",
        PickNearby = "选择角色…",
        NearbyDist = "{0} ({1:0.0}m)",

        ImportVmd = "导入VMD…",
        LoadFailed = "加载失败: {0}",
        NoMotionLoaded = "未加载动作",
        Play = "▶ 播放",
        Pause = "暂停",
        Stop = "停止",
        Loop = "循环",
        Speed = "速度",
        Progress = "进度  {0:0.00}s / {1:0.00}s(第 {2:0} 帧)",
        AutoOpenGpose = "进入 GPose 时自动弹出窗口",

        MusicTitle = "音乐",
        ImportMusic = "导入音乐…",
        ClearMusic = "清除音乐",
        MusicEnabled = "启用音乐",
        MusicError = "音乐错误: {0}",
        MusicNone = "未导入音乐(wav / ogg / mp3)",
        MusicVolume = "音乐音量",
        MusicOffset = "音乐偏移(秒)",
        ResetDefaults = "恢复默认",
        ResetDefaultsTip = "音量 0.80,偏移 0.00",
        MuteGameAudio = "播放音乐时屏蔽游戏原生声音",
        MuteGameAudioTip = "音乐出声期间把游戏主音量临时归零(BGM/音效/环境/系统音全部静音);\n暂停、停止、结束或卸载后自动恢复原值。插件音乐走独立音频设备,不受影响。\n若游戏在屏蔽期间崩溃退出,配置不会写盘;万一主音量异常,在游戏系统设置调回即可。",
        MusicHelp = "音频位置 = 动画时间 + 偏移:正值跳过音轨开头,负值音乐延后进入。拖动后松开鼠标或直接键入数值生效。播放、暂停、进度、循环和速度全部跟随动画(速度变化时音调同步变化)。",

        Hotkeys = "快捷键",
        HkPlay = "播放",
        HkPause = "暂停",
        HkStop = "停止",
        HkNone = "无",
        HkNumpad = "小键盘",
        HkTip = "窗口关闭时也可用;设为\"无\"可禁用。\n播放键在暂停时恢复、播放中不重复触发;暂停键为切换。\n建议使用小键盘键,聊天打字不会误触。",
        HkHelp = "播放键:暂停中恢复,未播放时从头开始;暂停键:切换;停止键:停止并复位。",

        CalSection = "源骨架与动作适配",
        SourceNone = "未建立源骨架",
        SourceApprox = "{0}(近似适配,可选源 PMX)",
        SourcePmxStatus = "PMX:{0}",
        StandardPose = "标准骨架参考姿态",
        StandardPoseA = "A 姿态",
        StandardPoseT = "T 姿态",
        PickSourcePmx = "选择源 PMX(可选)…",
        UseStandardSource = "使用标准骨架",
        LegIkTitle = "源骨架 IK",
        LegIkFk = "关闭(FK)",
        LegIkForce = "强制启用",
        LegIkVmd = "按动作开关(推荐)",
        YawDegrees = "整体朝向 (°)",
        MotionScale = "动作幅度",
        AutoPosScale = "自动换算整体位移(按实际腿长)",
        ManualPosScale = "整体位移系数",
        PlacementHint = "以 n_root 为放置基准;保留质心蹲起和跳跃,水平移动不改变放置高度。",
        HeightOffset = "根骨离地高度偏移",
        HeightReset = "高度归零",
        HeightHint = "正值抬高,负值降低;只移动整个人物,不写入骨骼缩放。",
        PosScaleHint = "位移系数只影响整体移动和升降;腿姿按源解算姿态与目标腿长适配。",

        MusicFormatUnsupported = "不支持的音频格式 {0};支持 wav / ogg / mp3。",
        MusicFileMissing = "音乐文件不存在。",
        SourcePmxMissing = "源 PMX 缺少人体关节:{0}",
        LoadNoBoneTracks = "此 VMD 没有骨骼动作关键帧;当前版本不播放纯表情或纯镜头文件。",
        LoadEnterGpose = "请先进入集体动作(GPose)再播放。",
        PickVmdTitle = "选择 VMD 动作文件",
        VmdFilter = "VMD 动作",
        PickMusicTitle = "选择音乐文件",
        MusicFilter = "音频(wav / ogg / mp3)",
        PickPmxTitle = "选择动作对应的源 PMX",
        PmxFilter = "MMD 模型",

        DebugSection = "调试:骨架 dump 与映射状态",
        ExportDiag = "导出当前帧诊断(暂停播放)",
        VideoTimes = "视频对照时间:",
        FingerTimes = "四指/高度对照时间:",
        ReadSkeleton = "读取目标骨架",
        CheckFrame = "检查当前帧",
        NoSkelCache = "尚无骨架缓存:选定目标后点上面的按钮,或播放一次。",
        SkelSummary = "骨架: {0} 根骨骼(partial 0)  重心骨: {1}  实际腿长位移比例: {2:0.0000}",
        MappedSummary = "目标关节适配: {0} 根  源骨架未覆盖的 VMD 轨道: {1} 条",
        UnmappedTitle = "未映射的 MMD 骨骼轨道({0} 条)",
        MappingDetail = "映射明细",
        MappedTrack = "{0} → {1}({2} 关键帧,参与源解算)",
        MappedInherit = "{0} → {1}(源解算/继承姿态)",
        BoneList = "骨骼列表({0})",
        RootLabel = "(根)",

        DiagNeedGpose = "请在 GPose 中选择角色并加载动作,骨架 Hook 需可用。",
        DiagWaiting = "已暂停:等待当前帧写入和最终姿态…",
        DiagNoWrite = "未捕获到骨架写入,请检查播放目标和 Hook。",
        DiagTimeout = "最终观察超时或目标已变化;FinalRender 未捕获。",
        DiagExported = "已导出:{0}",
        DiagSaveFailed = "诊断保存失败:{0}",
        ScaleAudit = "{0:0.00}s 缩放变化:写入前后 {1};后续阶段 {2}(分别记录)。",
        ScaleNotCaptured = "未捕获",
        CmdDesc = "打开 FFMMD 播放器",

        LangLabel = "Language / 语言",
        LangAuto = "自动",
        LangChinese = "中文",
        LangEnglish = "English",
    };

    private static Strings English() => new()
    {
        HookOk = "Skeleton hook active",
        HookMissing = "Skeleton hook not found: {0}",
        TargetName = "Target: {0}",
        TargetNone = "Target: none (pick a character)",
        GposeLabel = "GPose",

        TargetModeTitle = "Playback target",
        TargetSelf = "Self",
        TargetCurrent = "Current target",
        TargetNearby = "Nearby",
        PickNearby = "Pick a character...",
        NearbyDist = "{0} ({1:0.0}m)",

        ImportVmd = "Import VMD...",
        LoadFailed = "Load failed: {0}",
        NoMotionLoaded = "No motion loaded",
        Play = "▶ Play",
        Pause = "Pause",
        Stop = "Stop",
        Loop = "Loop",
        Speed = "Speed",
        Progress = "Progress  {0:0.00}s / {1:0.00}s (frame {2:0})",
        AutoOpenGpose = "Open window automatically in GPose",

        MusicTitle = "Music",
        ImportMusic = "Import music...",
        ClearMusic = "Clear music",
        MusicEnabled = "Enable music",
        MusicError = "Music error: {0}",
        MusicNone = "No music imported (wav / ogg / mp3)",
        MusicVolume = "Music volume",
        MusicOffset = "Music offset (s)",
        ResetDefaults = "Reset defaults",
        ResetDefaultsTip = "Volume 0.80, offset 0.00",
        MuteGameAudio = "Mute in-game audio while music plays",
        MuteGameAudioTip = "While the music is audible the game master volume is temporarily set to zero (BGM/SE/ambient/system sounds all muted);\nit is restored automatically on pause, stop, end or unload. Plugin music uses an independent audio device and is unaffected.\nIf the game crashes while muted, nothing is written to disk; if the master volume ever looks wrong, adjust it in the game system settings.",
        MusicHelp = "Audio position = animation time + offset: positive skips into the track, negative delays the music. Edits apply on slider release, or type a value directly. Play/pause/progress/loop/speed all follow the animation (speed changes pitch accordingly).",

        Hotkeys = "Hotkeys",
        HkPlay = "Play",
        HkPause = "Pause",
        HkStop = "Stop",
        HkNone = "None",
        HkNumpad = "Numpad",
        HkTip = "Work while the window is closed; set to \"None\" to disable.\nPlay resumes from pause and does not restart while playing; Pause toggles.\nNumpad keys are recommended to avoid clashes while typing in chat.",
        HkHelp = "Play: resumes when paused, restarts when idle; Pause: toggle; Stop: stop and reset.",

        CalSection = "Source rig & motion adaptation",
        SourceNone = "No source rig loaded",
        SourceApprox = "{0} (approximate adaptation, optional source PMX)",
        SourcePmxStatus = "PMX: {0}",
        StandardPose = "Standard rig rest pose",
        StandardPoseA = "A-pose",
        StandardPoseT = "T-pose",
        PickSourcePmx = "Select source PMX (optional)...",
        UseStandardSource = "Use standard rig",
        LegIkTitle = "Source rig IK",
        LegIkFk = "Off (FK)",
        LegIkForce = "Force enable",
        LegIkVmd = "Follow motion switches (recommended)",
        YawDegrees = "Overall yaw (°)",
        MotionScale = "Motion scale",
        AutoPosScale = "Auto motion offset scale (by actual leg length)",
        ManualPosScale = "Manual motion offset scale",
        PlacementHint = "Anchored to n_root; keeps hip crouch/jump, horizontal motion never changes placement height.",
        HeightOffset = "Root ground offset",
        HeightReset = "Reset height",
        HeightHint = "Positive raises, negative lowers; moves the whole character only, never writes bone scale.",
        PosScaleHint = "The offset scale only affects overall movement and elevation; leg poses adapt to the target leg lengths.",

        MusicFormatUnsupported = "Unsupported audio format {0}; wav / ogg / mp3 are supported.",
        MusicFileMissing = "Music file not found.",
        SourcePmxMissing = "Source PMX is missing a human joint: {0}",
        LoadNoBoneTracks = "This VMD has no bone keyframes; pure expression or camera files are not played in this version.",
        LoadEnterGpose = "Enter GPose (Group Pose) before playing.",
        PickVmdTitle = "Select a VMD motion file",
        VmdFilter = "VMD motion",
        PickMusicTitle = "Select a music file",
        MusicFilter = "Audio (wav / ogg / mp3)",
        PickPmxTitle = "Select the source PMX for this motion",
        PmxFilter = "MMD model",

        DebugSection = "Debug: skeleton dump & mapping state",
        ExportDiag = "Export current frame diagnostics (pauses playback)",
        VideoTimes = "Video reference times:",
        FingerTimes = "Finger/height reference times:",
        ReadSkeleton = "Read target skeleton",
        CheckFrame = "Inspect current frame",
        NoSkelCache = "No skeleton cache yet: pick a target and click above, or play once.",
        SkelSummary = "Skeleton: {0} bones (partial 0)  center bone: {1}  actual leg-length offset scale: {2:0.0000}",
        MappedSummary = "Adapted joints: {0}  unmapped VMD tracks: {1}",
        UnmappedTitle = "Unmapped MMD bone tracks ({0})",
        MappingDetail = "Mapping detail",
        MappedTrack = "{0} → {1} ({2} keys, feeds source solve)",
        MappedInherit = "{0} → {1} (source-solved / inherited pose)",
        BoneList = "Bones ({0})",
        RootLabel = "(root)",

        DiagNeedGpose = "Pick a character in GPose with a motion loaded; the skeleton hook must be available.",
        DiagWaiting = "Paused: waiting for the current frame write and final pose...",
        DiagNoWrite = "No skeleton write captured; check the playback target and hook.",
        DiagTimeout = "Final-stage observation timed out or the target changed; FinalRender not captured.",
        DiagExported = "Exported: {0}",
        DiagSaveFailed = "Failed to save diagnostics: {0}",
        ScaleAudit = "{0:0.00}s scale changes: across write {1}; later stages {2} (recorded separately).",
        ScaleNotCaptured = "not captured",
        CmdDesc = "Open the FFMMD player",

        LangLabel = "Language / 语言",
        LangAuto = "Auto",
        LangChinese = "中文",
        LangEnglish = "English",
    };
}
