using Dalamud.Interface.Colors;
using ECommons.Interop;

namespace FFMMD.UI;

public static class MainWindow
{
    private static Player.VmdPlayerService Player => P.Player;
    private static Calibration Cal => P.Config.Cal;
    private static List<string>? _diagLines;

    public static void Draw()
    {
        if (P.Config == null || P.Player == null || P.Applier == null) return;
        DrawStatusLine();
        ImGui.Separator();
        DrawPlayerSection();
        ImGui.Separator();
        DrawMusicSection();
        ImGui.Separator();
        DrawCalibrationSection();
        ImGui.Separator();
        DrawDebugSection();
    }

    private static void DrawStatusLine()
    {
        ImGuiEx.Text(ImGuiColors.ParsedGreen, P.Applier.Available ? "骨骼Hook正常" : $"骨骼Hook未命中：{P.Applier.Error}");
        ImGui.SameLine();
        ImGuiEx.Text(ImGuiColors.DalamudGrey, Player.TargetValid ? $"目标: {Player.TargetName}" : "目标: 无（选一个角色）");
        ImGui.SameLine();
        ImGuiEx.Text(Svc.ClientState.IsGPosing ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey, "GPose");
    }

    private static void DrawPlayerSection()
    {
        DrawTargetSelector();

        // 文件加载
        var loaded = Player.LoadedPath == null ? "未加载动作" : Path.GetFileName(Player.LoadedPath);
        if (ImGui.Button("导入VMD…")) PickVmdFile();
        ImGui.SameLine();
        ImGuiEx.Text(Player.LoadError != null ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey,
            Player.LoadError != null ? $"加载失败: {Player.LoadError}" : loaded);

        if (Player.Anim != null)
        {
            // 走带控制
            if (ImGui.Button("▶ 播放")) Player.Play();
            ImGui.SameLine();
            var paused = Player.Paused;
            if (ImGui.Checkbox("暂停", ref paused)) Player.SetPaused(paused);
            ImGui.SameLine();
            if (ImGui.Button("停止")) Player.Stop();
            ImGui.SameLine();
            var loop = P.Config.Loop;
            if (ImGui.Checkbox("循环", ref loop))
            {
                P.Config.Loop = loop;
                P.ConfigDirty = true;
            }

            var speed = P.Config.Speed;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderFloat("速度", ref speed, 0.25f, 4f, "%.2fx"))
                Player.SetSpeed(speed);

            var dur = Player.Anim.DurationSec;
            var t = (float)Player.TimeSec;
            ImGui.SetNextItemWidth(-1);
            if (dur > 0 && ImGui.SliderFloat($"进度  {t:0.00}s / {dur:0.00}s（第 {Player.TimeSec * Vmd.VmdAnimation.FramesPerSecond:0} 帧）", ref t, 0f, dur, "%.2f"))
                Player.Seek(t);
        }

        var autoOpen = P.Config.AutoOpenInGPose;
        if (ImGui.Checkbox("进入 GPose 时自动弹出窗口", ref autoOpen))
        {
            P.Config.AutoOpenInGPose = autoOpen;
            P.ConfigDirty = true;
        }

        // 全局快捷键:窗口关闭时仍有效,由 VmdPlayerService 在 Tick 中检测按下沿。
        ImGui.TextUnformatted("快捷键");
        ImGui.SameLine();
        DrawHotkeyPicker("播放##hkPlay", P.Config.PlayHotkey, v => { P.Config.PlayHotkey = v; P.ConfigDirty = true; });
        ImGui.SameLine();
        DrawHotkeyPicker("暂停##hkPause", P.Config.PauseHotkey, v => { P.Config.PauseHotkey = v; P.ConfigDirty = true; });
        ImGui.SameLine();
        DrawHotkeyPicker("停止##hkStop", P.Config.StopHotkey, v => { P.Config.StopHotkey = v; P.ConfigDirty = true; });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("窗口关闭时也可用;设为\"无\"可禁用。\n播放键在暂停时恢复、播放中不重复触发;暂停键为切换。\n建议使用小键盘键,聊天打字不会误触。");
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey,
            "播放键:暂停中恢复,未播放时从头开始;暂停键:切换;停止键:停止并复位。");
    }

    private static readonly (int Key, string Label)[] HotkeyOptions =
    [
        (0, "无"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD0, "小键盘 0"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD1, "小键盘 1"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD2, "小键盘 2"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD3, "小键盘 3"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD4, "小键盘 4"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD5, "小键盘 5"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD6, "小键盘 6"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD7, "小键盘 7"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD8, "小键盘 8"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD9, "小键盘 9"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F5, "F5"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F6, "F6"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F7, "F7"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F8, "F8"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F9, "F9"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F10, "F10"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F11, "F11"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.F12, "F12"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.PRIOR, "PageUp"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NEXT, "PageDown"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.END, "End"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.HOME, "Home"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.INSERT, "Insert"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.DELETE, "Delete"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.OEM_PLUS, "+"),
        ((int)Dalamud.Game.ClientState.Keys.VirtualKey.OEM_MINUS, "-"),
    ];

    private static void DrawHotkeyPicker(string label, int current, Action<int> set)
    {
        var index = Array.FindIndex(HotkeyOptions, o => o.Key == current);
        if (index < 0) index = 0;
        ImGui.SetNextItemWidth(96);
        var items = string.Join('\0', HotkeyOptions.Select(o => o.Label)) + '\0';
        if (ImGui.Combo(label, ref index, items))
            set(HotkeyOptions[index].Key);
    }

    /// <summary> 音乐区:导入/清除/启用、音量与偏移。播放、暂停、进度、循环全部跟随动画 transport。 </summary>
    private static void DrawMusicSection()
    {
        var music = P.Music;
        if (music == null) return;
        ImGui.TextUnformatted("音乐");
        ImGui.SameLine();
        if (ImGui.Button("导入音乐…")) PickMusicFile();
        ImGui.SameLine();
        if (ImGui.Button("清除音乐")) music.ImportMusic(null);
        ImGui.SameLine();
        var enabled = P.Config.MusicEnabled;
        if (ImGui.Checkbox("启用音乐", ref enabled)) music.SetEnabled(enabled);

        ImGuiEx.Text(music.Error != null ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey,
            music.Error != null ? $"音乐错误: {music.Error}"
            : music.CurrentPath != null ? Path.GetFileName(music.CurrentPath)
            : "未导入音乐(wav / ogg / mp3)");

        var volume = P.Config.MusicVolume;
        ImGui.SetNextItemWidth(190);
        if (ImGui.SliderFloat("##音乐音量", ref volume, 0f, 1f, "%.2f"))
            music.SetVolume(volume);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(64);
        if (ImGui.InputFloat("##音乐音量输入", ref volume, 0f, 0f, "%.2f"))
            music.SetVolume(volume);
        ImGui.SameLine();
        ImGui.TextUnformatted("音乐音量");

        var offset = P.Config.MusicOffsetSec;
        ImGui.SetNextItemWidth(190);
        if (ImGui.SliderFloat("##音乐偏移", ref offset, -5f, 5f, "%+.2f s"))
        {
            P.Config.MusicOffsetSec = offset;   // 拖动中只改数值,避免连续 Seek
            P.ConfigDirty = true;
        }
        var sliderDeactivated = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(64);
        if (ImGui.InputFloat("##音乐偏移输入", ref offset, 0f, 0f, "%+.2f"))
        {
            P.Config.MusicOffsetSec = offset;
            P.ConfigDirty = true;
        }
        // 松手/回车帧 SliderFloat 与 InputFloat 都返回 false(值已定格),IsItemDeactivatedAfterEdit
        // 必须在返回值检查之外调用,否则永远检测不到编辑结束,偏移重定位不会触发。
        if (sliderDeactivated || ImGui.IsItemDeactivatedAfterEdit())
            music.SetOffset(offset);
        ImGui.SameLine();
        ImGui.TextUnformatted("音乐偏移(秒)");
        ImGui.SameLine();
        if (ImGui.Button("恢复默认"))
        {
            music.SetVolume(0.8f);
            music.SetOffset(0);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("音量 0.80,偏移 0.00");

        var muteGame = P.Config.MuteGameAudioWhilePlaying;
        if (ImGui.Checkbox("播放音乐时屏蔽游戏原生声音", ref muteGame))
        {
            P.Config.MuteGameAudioWhilePlaying = muteGame;
            P.ConfigDirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("音乐出声期间把游戏主音量临时归零(BGM/音效/环境/系统音全部静音);\n暂停、停止、结束或卸载后自动恢复原值。插件音乐走独立音频设备,不受影响。\n若游戏在屏蔽期间崩溃退出,配置不会写盘;万一主音量异常,在游戏系统设置调回即可。");
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey,
            "音频位置 = 动画时间 + 偏移:正值跳过音轨开头,负值音乐延后进入。拖动后松开鼠标或直接键入数值生效。播放、暂停、进度、循环和速度全部跟随动画(速度变化时音调同步变化)。");
    }

    private static void DrawTargetSelector()
    {
        ImGui.TextUnformatted("播放目标");
        ImGui.SameLine();
        var mode = P.Config.TargetMode;
        if (ImGui.RadioButton("自己", mode == 0))
        {
            P.Config.TargetMode = 0;
            P.ConfigDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("当前目标", mode == 1))
        {
            P.Config.TargetMode = 1;
            P.ConfigDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("附近角色", mode == 2))
        {
            P.Config.TargetMode = 2;
            P.ConfigDirty = true;
        }

        if (P.Config.TargetMode == 2)
        {
            var nearby = Player.GetNearbyPlayers();
            var current = nearby.FirstOrDefault(x => x.Id == P.Config.NearbyObjectId);
            var preview = current.Id == 0 ? "选择角色…" : $"{current.Name}";
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##nearby", preview))
            {
                foreach (var (id, name, dist) in nearby)
                    if (ImGui.Selectable($"{name} ({dist:0.0}m)##{id}", id == P.Config.NearbyObjectId))
                    {
                        P.Config.NearbyObjectId = id;
                        P.ConfigDirty = true;
                    }
                ImGui.EndCombo();
            }
        }
    }

    private static void PickVmdFile()
    {
        var initialDir = P.Config.LastVmdPath;
        OpenFileDialog.SelectFile(
            ofn => new TickScheduler(() => Player.PendingLoadPath = ofn.file),
            null,
            string.IsNullOrEmpty(initialDir) ? null : Path.GetDirectoryName(initialDir),
            "选择 VMD 动作文件",
            [("VMD 动作", new[] { "vmd" })]);
    }

    private static void PickMusicFile()
    {
        var initialDir = P.Config.MusicPath;
        OpenFileDialog.SelectFile(
            ofn => new TickScheduler(() => P.Music?.ImportMusic(ofn.file)),
            null,
            string.IsNullOrEmpty(initialDir) ? null : Path.GetDirectoryName(initialDir),
            "选择音乐文件",
            [("音频(wav / ogg / mp3)", new[] { "wav", "ogg", "mp3" })]);
    }

    private static void DrawCalibrationSection()
    {
        if (!ImGui.CollapsingHeader("源骨架与动作适配")) return;
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, Player.Retargeter.SourceStatus);
        var preset = Cal.SourceRestPose;
        ImGui.BeginDisabled(Player.SourcePmxPath != null);
        if (ImGui.Combo("标准骨架参考姿态", ref preset, "A 姿态\0T 姿态\0"))
        { Cal.SourceRestPose = preset; P.ConfigDirty = true; }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(Player.Anim == null);
        if (ImGui.Button("选择源 PMX（可选）…"))
            OpenFileDialog.SelectFile(ofn => new TickScheduler(() => Player.PendingPmxPath = ofn.file), null,
                Player.SourcePmxPath == null ? null : Path.GetDirectoryName(Player.SourcePmxPath), "选择动作对应的源 PMX", [("MMD 模型", new[] { "pmx" })]);
        ImGui.SameLine();
        if (ImGui.Button("使用标准骨架")) Player.UseStandardSource();
        ImGui.EndDisabled();
        if (Player.SourcePmxPath != null) ImGui.TextUnformatted(Path.GetFileName(Player.SourcePmxPath));
        if (Player.SourceError != null) ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, Player.SourceError);
        if (Player.Retargeter.SourceRig is { } source)
            foreach (var warning in source.Warnings.Take(8)) ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, warning);
        var yaw = Cal.YawDegrees;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat("整体朝向 (°)", ref yaw, -180, 180, "%.0f°")) { Cal.YawDegrees = yaw; P.ConfigDirty = true; }
        var amplitude = Cal.MotionScale;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat("动作幅度", ref amplitude, 0, 1, "%.2f")) { Cal.MotionScale = amplitude; P.ConfigDirty = true; }
        var auto = Cal.AutoPositionScale;
        if (ImGui.Checkbox("自动换算整体位移（按实际腿长）", ref auto)) { Cal.AutoPositionScale = auto; P.ConfigDirty = true; }
        ImGui.BeginDisabled(auto);
        var manual = Cal.ManualPositionScale;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat("整体位移系数", ref manual, 0, .5f, "%.3f")) { Cal.ManualPositionScale = manual; P.ConfigDirty = true; }
        ImGui.EndDisabled();
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "以 n_root 为放置基准；保留质心蹲起和跳跃，水平移动不改变放置高度。");
        var height = Cal.HeightOffset;
        ImGui.SetNextItemWidth(260);
        if (ImGui.DragFloat("根骨离地高度偏移", ref height, .01f, -3, 3, "%.2f")) { Cal.HeightOffset = float.IsFinite(height)?Math.Clamp(height,-3,3):0; P.ConfigDirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("高度归零")) { Cal.HeightOffset = 0; P.ConfigDirty = true; }
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "正值抬高，负值降低；只移动整个人物，不写入骨骼缩放。");
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "位移系数只影响整体移动和升降；腿姿按源解算姿态与目标腿长适配。");
        var mode = Cal.LegIkMode;
        if (ImGui.Combo("源骨架 IK", ref mode, "关闭（FK）\0强制启用\0按动作开关（推荐）\0")) { Cal.LegIkMode = mode; P.ConfigDirty = true; }
    }

    private static void DrawDebugSection()
    {
        if (!ImGui.CollapsingHeader("调试：骨架 dump 与映射状态")) return;
        var r = Player.Retargeter;
        if (ImGui.Button("导出当前帧诊断（暂停播放）")) Player.RequestDiagnostics();
        if (Player.DiagnosticStatus != null) ImGuiEx.TextWrapped(Player.DiagnosticStatus);
        if (Player.ScaleAuditStatus != null) ImGuiEx.TextWrapped(Player.ScaleAuditStatus);
        ImGui.TextUnformatted("视频对照时间：");
        foreach (var time in new[] { 26.16, 31.17, 41.17, 46.17 })
        {
            ImGui.SameLine();
            if (ImGui.Button($"{time:0.00}s")) { Player.SetPaused(true); Player.Seek(time); }
        }

        ImGui.TextUnformatted("四指／高度对照时间：");
        foreach (var time in new[] { 58.9601767, 86.5599933, 142.6141433 })
        {
            ImGui.SameLine();
            if (ImGui.Button($"{time:0.00}s##finger")) { Player.SetPaused(true); Player.Seek(time); }
        }

        if (ImGui.Button("读取目标骨架"))
            Player.TryBuildDebugTree();
        ImGui.SameLine();
        if (ImGui.Button("检查当前帧"))
        {
            _diagLines = Player.DiagnoseCurrentFrame();
        }
        if (_diagLines != null)
            foreach (var l in _diagLines)
                ImGuiEx.TextWrapped(l);

        var tree = r.Tree;
        if (tree == null)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "尚无骨架缓存：选定目标后点上面的按钮，或播放一次。");
            return;
        }

        ImGuiEx.Text(ImGuiColors.ParsedGreen, $"骨架: {tree.BoneCount} 根骨骼（partial 0）  重心骨: {(r.CenterBoneIndex >= 0 ? tree.Names[r.CenterBoneIndex] : "未找到")}" +
                                                $"  实际腿长位移比例: {r.AutoPosScale:0.0000}");
        if (Player.Anim != null)
        {
            ImGuiEx.Text(ImGuiColors.ParsedGreen, $"目标关节适配: {r.Mapped.Count} 根  " +
                                                $"源骨架未覆盖的 VMD 轨道: {r.UnmappedMmd.Count} 条");
            if (r.UnmappedMmd.Count > 0 && ImGui.TreeNode("未映射的 MMD 骨骼轨道"))
            {
                ImGuiEx.TextWrapped(string.Join("、", r.UnmappedMmd.Take(60)));
                ImGui.TreePop();
            }
            if (r.Mapped.Count > 0 && ImGui.TreeNode("映射明细"))
            {
                foreach (var m in r.Mapped)
                    ImGuiEx.Text(m.Track != null ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey,
                        $"{m.SourceJp} → {m.FfName}{(m.Track != null ? $"（{m.Track.Keys.Count} 关键帧，参与源解算）" : "（源解算／继承姿态）")}");
                ImGui.TreePop();
            }
        }

        if (ImGui.TreeNode($"骨骼列表（{tree.BoneCount}）"))
        {
            ImGui.BeginChild("##bonelist", new Vector2(0, 320));
            for (var i = 0; i < tree.BoneCount; i++)
            {
                var parentName = tree.Parent[i] >= 0 ? tree.Names[tree.Parent[i]] : "(根)";
                ImGui.TextUnformatted($"{i,3}  {tree.Names[i]}   ←  {parentName}");
            }
            ImGui.EndChild();
            ImGui.TreePop();
        }
    }
}
