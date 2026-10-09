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
        DrawSkirtPhysicsSettings();
        ImGui.Separator();
        DrawMusicSection();
        ImGui.Separator();
        DrawCalibrationSection();
        ImGui.Separator();
        DrawDebugSection();
    }

    private static void DrawStatusLine()
    {
        ImGuiEx.Text(ImGuiColors.ParsedGreen, P.Applier.Available ? Loc.S.HookOk : string.Format(Loc.S.HookMissing, P.Applier.Error));
        ImGui.SameLine();
        ImGuiEx.Text(ImGuiColors.DalamudGrey, Player.Primary.TargetValid ? string.Format(Loc.S.TargetName, Player.Primary.TargetName) : Loc.S.TargetNone);
        ImGui.SameLine();
        ImGuiEx.Text(Svc.ClientState.IsGPosing ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey, Loc.S.GposeLabel);
        DrawLanguagePicker();
    }

    /// <summary> 顶栏语言切换:切换即时生效并记住选择;Auto 按客户端语言判定。 </summary>
    private static void DrawLanguagePicker()
    {
        var s = Loc.S;
        var lang = P.Config.Language;
        var current = lang switch { 1 => s.LangChinese, 2 => s.LangEnglish, _ => s.LangAuto };
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo(s.LangLabel + "##ffmmd-lang", current))
        {
            foreach (var (value, name) in new[] { (0, s.LangAuto), (1, s.LangChinese), (2, s.LangEnglish) })
                if (ImGui.Selectable(name, lang == value) && lang != value)
                {
                    P.Config.Language = value;
                    P.ConfigDirty = true;
                    Loc.Invalidate();
                }
            ImGui.EndCombo();
        }
    }

    private static void DrawPlayerSection()
    {
        var s = Loc.S;

        // 全局参数:速度与循环所有槽共享;快捷键作用于角色 1。
        var speed = P.Config.Speed;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat(s.Speed, ref speed, 0.25f, 4f, "%.2fx"))
            Player.SetSpeed(speed);
        var loop = P.Config.Loop;
        if (ImGui.Checkbox(s.Loop, ref loop))
        {
            P.Config.Loop = loop;
            P.ConfigDirty = true;
        }
        ImGui.SameLine();
        var autoOpen = P.Config.AutoOpenInGPose;
        if (ImGui.Checkbox(s.AutoOpenGpose, ref autoOpen))
        {
            P.Config.AutoOpenInGPose = autoOpen;
            P.ConfigDirty = true;
        }

        // 全局快捷键:窗口关闭时仍有效,由协调器在 Tick 中检测按下沿。
        ImGui.TextUnformatted(s.Hotkeys);
        ImGui.SameLine();
        DrawHotkeyPicker(s.HkPlay + "##hkPlay", P.Config.PlayHotkey, v => { P.Config.PlayHotkey = v; P.ConfigDirty = true; });
        ImGui.SameLine();
        DrawHotkeyPicker(s.HkPause + "##hkPause", P.Config.PauseHotkey, v => { P.Config.PauseHotkey = v; P.ConfigDirty = true; });
        ImGui.SameLine();
        DrawHotkeyPicker(s.HkStop + "##hkStop", P.Config.StopHotkey, v => { P.Config.StopHotkey = v; P.ConfigDirty = true; });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.HkTip);
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, s.HkHelp);

        // 角色槽:每个槽一个角色/一条 VMD;音乐跟随角色 1。
        for (var i = 0; i < Player.Slots.Count; i++)
        {
            ImGui.PushID($"slot{i}");
            var slot = Player.Slots[i];
            if (i == 0) ImGui.SetNextItemOpen(true, ImGuiCond.Once);
            if (ImGui.CollapsingHeader(string.Format(s.SlotTitle, i + 1)))
            {
                DrawSlotBody(slot);
                if (i > 0)
                {
                    if (ImGui.SmallButton(s.RemoveSlot)) Player.RemoveSlot(i);
                }
            }
            ImGui.PopID();
        }
        if (ImGui.Button(s.AddSlot)) Player.AddSlot();
    }

    /// <summary> 单槽内容:目标选择、VMD 加载、播放控制与进度、源 PMX 与离线裙骨缓存。 </summary>
    private static void DrawSlotBody(Player.PlayerSlot slot)
    {
        var s = Loc.S;
        DrawTargetSelector(slot);

        // 文件加载
        var loaded = slot.LoadedPath == null ? s.NoMotionLoaded : Path.GetFileName(slot.LoadedPath);
        if (ImGui.Button(s.ImportVmd)) PickVmdFile(slot);
        ImGui.SameLine();
        ImGuiEx.Text(slot.LoadError != null ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey,
            slot.LoadError != null ? string.Format(s.LoadFailed, slot.LoadError) : loaded);

        if (slot.Anim != null)
        {
            // 走带控制
            if (ImGui.Button(s.Play)) slot.Play();
            ImGui.SameLine();
            var paused = slot.Paused;
            if (ImGui.Checkbox(s.Pause, ref paused)) slot.SetPaused(paused);
            ImGui.SameLine();
            if (ImGui.Button(s.Stop)) slot.Stop();

            var dur = slot.Anim.DurationSec;
            var t = (float)slot.TimeSec;
            ImGui.SetNextItemWidth(-1);
            if (dur > 0 && ImGui.SliderFloat(string.Format(s.Progress, t, dur, slot.TimeSec * Vmd.VmdAnimation.FramesPerSecond), ref t, 0f, dur, "%.2f"))
                slot.Seek(t);

            // 源 PMX(每槽独立;按动作记忆关联)
            if (ImGui.Button(s.PickSourcePmx))
                OpenFileDialog.SelectFile(ofn => new TickScheduler(() => slot.PendingPmxPath = ofn.file), null,
                    slot.SourcePmxPath == null ? null : Path.GetDirectoryName(slot.SourcePmxPath), s.PickPmxTitle, [(s.PmxFilter, new[] { "pmx" })]);
            ImGui.SameLine();
            if (ImGui.Button(s.UseStandardSource)) slot.UseStandardSource();
            if (slot.SourcePmxPath != null)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(Path.GetFileName(slot.SourcePmxPath));
            }
            if (slot.SourceError != null) ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, slot.SourceError);
            DrawSkirtBakeControls(slot);
        }
    }

    private static void DrawSkirtBakeControls(Player.PlayerSlot slot)
    {
        var s = Loc.S;
        ImGui.TextUnformatted(s.SkirtOfflineTitle);
        if (slot.SkirtPreprocessing)
        {
            var progress = float.IsFinite(slot.SkirtBakeProgress) ? Math.Clamp(slot.SkirtBakeProgress, 0, 1) : 0;
            ImGui.ProgressBar(progress, new Vector2(-1, 0), string.Format(s.SkirtPreprocessing, progress * 100));
        }
        ImGui.BeginDisabled(slot.SkirtPreprocessing || !P.Config.AutoSkirtPhysics);
        if (ImGui.Button(s.RetrySkirtBake)) slot.RetrySkirtBake();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(s.CancelSkirtBake)) slot.CancelSkirtBake();
        if (!string.IsNullOrWhiteSpace(slot.SkirtBakeStatus))
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, string.Format(s.SkirtOfflineStatus, slot.SkirtBakeStatus));
        else ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, s.SkirtBakeNone);
    }

    private static void DrawSkirtPhysicsSettings()
    {
        var s = Loc.S;
        if (!ImGui.CollapsingHeader(s.SkirtPhysicsSettings)) return;
        var automatic = P.Config.AutoSkirtPhysics;
        if (ImGui.Checkbox(s.AutoSkirtPhysics, ref automatic))
        {
            P.Config.AutoSkirtPhysics = automatic;
            P.ConfigDirty = true;
        }
        if (ImGui.Button(s.PickSkirtPhysicsPmx))
            OpenFileDialog.SelectFile(ofn => new TickScheduler(() =>
            {
                P.Config.SkirtPhysicsPmxPath = ofn.file;
                P.ConfigDirty = true;
            }), null, P.Config.SkirtPhysicsPmxPath == null ? null : Path.GetDirectoryName(P.Config.SkirtPhysicsPmxPath),
                s.PickSkirtPhysicsPmxTitle, [(s.PmxFilter, new[] { "pmx" })]);
        ImGui.SameLine();
        if (ImGui.Button(s.UseBuiltInPhysicsReference))
        {
            P.Config.SkirtPhysicsPmxPath = null;
            P.ConfigDirty = true;
        }
        ImGui.SameLine();
        DrawPhysicsPath(P.Config.SkirtPhysicsPmxPath, s.SkirtPhysicsPmxNone);
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, s.SkirtPhysicsSetupHint);
    }

    private static void DrawPhysicsPath(string? path, string unset)
    {
        ImGuiEx.Text(ImGuiColors.DalamudGrey, path == null ? unset : Path.GetFileName(path));
        if (path != null && ImGui.IsItemHovered()) ImGui.SetTooltip(path);
    }

    /// <summary> 快捷键选项;标签随语言构建(UI 每帧调用,数量固定,分配可忽略)。 </summary>
    private static (int Key, string Label)[] HotkeyOptions()
    {
        var s = Loc.S;
        return
        [
            (0, s.HkNone),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD0, $"{s.HkNumpad} 0"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD1, $"{s.HkNumpad} 1"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD2, $"{s.HkNumpad} 2"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD3, $"{s.HkNumpad} 3"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD4, $"{s.HkNumpad} 4"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD5, $"{s.HkNumpad} 5"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD6, $"{s.HkNumpad} 6"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD7, $"{s.HkNumpad} 7"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD8, $"{s.HkNumpad} 8"),
            ((int)Dalamud.Game.ClientState.Keys.VirtualKey.NUMPAD9, $"{s.HkNumpad} 9"),
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
    }

    private static void DrawHotkeyPicker(string label, int current, Action<int> set)
    {
        var options = HotkeyOptions();
        var index = Array.FindIndex(options, o => o.Key == current);
        if (index < 0) index = 0;
        ImGui.SetNextItemWidth(96);
        var items = string.Join('\0', options.Select(o => o.Label)) + '\0';
        if (ImGui.Combo(label, ref index, items))
            set(options[index].Key);
    }

    /// <summary> 音乐区:导入/清除/启用、音量与偏移。播放、暂停、进度、循环全部跟随动画 transport。 </summary>
    private static void DrawMusicSection()
    {
        var music = P.Music;
        if (music == null) return;
        ImGui.TextUnformatted(Loc.S.MusicTitle);
        ImGui.SameLine();
        ImGuiEx.Text(ImGuiColors.DalamudGrey, "(" + Loc.S.MusicFollows + ")");
        ImGui.SameLine();
        if (ImGui.Button(Loc.S.ImportMusic)) PickMusicFile();
        ImGui.SameLine();
        if (ImGui.Button(Loc.S.ClearMusic)) music.ImportMusic(null);
        ImGui.SameLine();
        var enabled = P.Config.MusicEnabled;
        if (ImGui.Checkbox(Loc.S.MusicEnabled, ref enabled)) music.SetEnabled(enabled);

        ImGuiEx.Text(music.Error != null ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey,
            music.Error != null ? string.Format(Loc.S.MusicError, music.Error)
            : music.CurrentPath != null ? Path.GetFileName(music.CurrentPath)
            : Loc.S.MusicNone);

        var volume = P.Config.MusicVolume;
        ImGui.SetNextItemWidth(190);
        if (ImGui.SliderFloat("##音乐音量", ref volume, 0f, 1f, "%.2f"))
            music.SetVolume(volume);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(64);
        if (ImGui.InputFloat("##音乐音量输入", ref volume, 0f, 0f, "%.2f"))
            music.SetVolume(volume);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.S.MusicVolume);

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
        ImGui.TextUnformatted(Loc.S.MusicOffset);
        ImGui.SameLine();
        if (ImGui.Button(Loc.S.ResetDefaults))
        {
            music.SetVolume(0.8f);
            music.SetOffset(0);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.S.ResetDefaultsTip);

        var muteGame = P.Config.MuteGameAudioWhilePlaying;
        if (ImGui.Checkbox(Loc.S.MuteGameAudio, ref muteGame))
        {
            P.Config.MuteGameAudioWhilePlaying = muteGame;
            P.ConfigDirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.S.MuteGameAudioTip);
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey,
            Loc.S.MusicHelp);
    }

    private static void DrawTargetSelector(Player.PlayerSlot slot)
    {
        var s = Loc.S;
        ImGui.TextUnformatted(s.TargetModeTitle);
        ImGui.SameLine();
        var mode = slot.TargetMode;
        if (ImGui.RadioButton(s.TargetSelf, mode == 0))
            slot.TargetMode = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton(s.TargetCurrent, mode == 1))
            slot.TargetMode = 1;
        ImGui.SameLine();
        if (ImGui.RadioButton(s.TargetNearby, mode == 2))
            slot.TargetMode = 2;

        if (slot.TargetMode == 2)
        {
            var nearby = slot.GetNearbyPlayers();
            var current = nearby.FirstOrDefault(x => x.Id == slot.NearbyObjectId);
            var preview = current.Id == 0 ? s.PickNearby : current.Name;
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##nearby", preview))
            {
                foreach (var (id, name, dist) in nearby)
                    if (ImGui.Selectable($"{string.Format(s.NearbyDist, name, dist)}##{id}", id == slot.NearbyObjectId))
                        slot.NearbyObjectId = id;
                ImGui.EndCombo();
            }
        }
    }

    private static void PickVmdFile(Player.PlayerSlot slot)
    {
        var initialDir = P.Config.LastVmdPath;
        OpenFileDialog.SelectFile(
            ofn => new TickScheduler(() => slot.PendingLoadPath = ofn.file),
            null,
            string.IsNullOrEmpty(initialDir) ? null : Path.GetDirectoryName(initialDir),
            Loc.S.PickVmdTitle,
            [(Loc.S.VmdFilter, new[] { "vmd" })]);
    }

    private static void PickMusicFile()
    {
        var initialDir = P.Config.MusicPath;
        OpenFileDialog.SelectFile(
            ofn => new TickScheduler(() => P.Music?.ImportMusic(ofn.file)),
            null,
            string.IsNullOrEmpty(initialDir) ? null : Path.GetDirectoryName(initialDir),
            Loc.S.PickMusicTitle,
            [(Loc.S.MusicFilter, new[] { "wav", "ogg", "mp3" })]);
    }

    private static void DrawCalibrationSection()
    {
        var s = Loc.S;
        if (!ImGui.CollapsingHeader(s.CalSection)) return;
        // 全局校准参数;源 PMX 按槽/动作管理,见各角色槽内按钮。此处显示主槽源状态。
        var rig = Player.Primary.Retargeter.SourceRig;
        var srcStatus = rig == null ? s.SourceNone
            : rig.Approximate ? string.Format(s.SourceApprox, rig.Name)
            : string.Format(s.SourcePmxStatus, rig.Name);
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, srcStatus);
        var preset = Cal.SourceRestPose;
        ImGui.BeginDisabled(Player.Primary.SourcePmxPath != null);
        if (ImGui.Combo(s.StandardPose, ref preset, $"{s.StandardPoseA}\0{s.StandardPoseT}\0"))
        { Cal.SourceRestPose = preset; P.ConfigDirty = true; }
        ImGui.EndDisabled();
        if (rig != null)
            foreach (var warning in rig.Warnings.Take(8)) ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, warning);
        var yaw = Cal.YawDegrees;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat(s.YawDegrees, ref yaw, -180, 180, "%.0f°")) { Cal.YawDegrees = yaw; P.ConfigDirty = true; }
        var amplitude = Cal.MotionScale;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat(s.MotionScale, ref amplitude, 0, 1, "%.2f")) { Cal.MotionScale = amplitude; P.ConfigDirty = true; }
        var auto = Cal.AutoPositionScale;
        if (ImGui.Checkbox(s.AutoPosScale, ref auto)) { Cal.AutoPositionScale = auto; P.ConfigDirty = true; }
        ImGui.BeginDisabled(auto);
        var manual = Cal.ManualPositionScale;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat(s.ManualPosScale, ref manual, 0, .5f, "%.3f")) { Cal.ManualPositionScale = manual; P.ConfigDirty = true; }
        ImGui.EndDisabled();
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, s.PlacementHint);
        var height = Cal.HeightOffset;
        ImGui.SetNextItemWidth(260);
        if (ImGui.DragFloat(s.HeightOffset, ref height, .01f, -3, 3, "%.2f")) { Cal.HeightOffset = float.IsFinite(height)?Math.Clamp(height,-3,3):0; P.ConfigDirty = true; }
        ImGui.SameLine();
        if (ImGui.Button(s.HeightReset)) { Cal.HeightOffset = 0; P.ConfigDirty = true; }
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, s.HeightHint);
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, s.PosScaleHint);
        var mode = Cal.LegIkMode;
        if (ImGui.Combo(s.LegIkTitle, ref mode, $"{s.LegIkFk}\0{s.LegIkForce}\0{s.LegIkVmd}\0")) { Cal.LegIkMode = mode; P.ConfigDirty = true; }
    }

    private static int _diagSlot;

    private static void DrawDebugSection()
    {
        var s = Loc.S;
        if (!ImGui.CollapsingHeader(s.DebugSection)) return;
        _diagSlot = Math.Clamp(_diagSlot, 0, Player.Slots.Count - 1);
        ImGui.TextUnformatted(s.DiagSlot);
        ImGui.SameLine();
        var slotIdx = _diagSlot;
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("##diagslot", ref slotIdx,
                string.Join('\0', Enumerable.Range(0, Player.Slots.Count).Select(i => string.Format(s.SlotTitle, i + 1)))))
            _diagSlot = slotIdx;
        var slot = Player.Slots[_diagSlot];
        var r = slot.Retargeter;
        if (ImGui.Button(s.ExportDiag)) slot.RequestDiagnostics();
        if (slot.DiagnosticStatus != null) ImGuiEx.TextWrapped(slot.DiagnosticStatus);
        if (slot.ScaleAuditStatus != null) ImGuiEx.TextWrapped(slot.ScaleAuditStatus);
        ImGui.TextUnformatted(s.VideoTimes);
        foreach (var time in new[] { 26.16, 31.17, 41.17, 46.17 })
        {
            ImGui.SameLine();
            if (ImGui.Button($"{time:0.00}s")) { slot.SetPaused(true); slot.Seek(time); }
        }

        ImGui.TextUnformatted(s.FingerTimes);
        foreach (var time in new[] { 58.9601767, 86.5599933, 142.6141433 })
        {
            ImGui.SameLine();
            if (ImGui.Button($"{time:0.00}s##finger")) { slot.SetPaused(true); slot.Seek(time); }
        }

        if (ImGui.Button(s.ReadSkeleton))
            slot.TryBuildDebugTree();
        ImGui.SameLine();
        if (ImGui.Button(s.CheckFrame))
        {
            _diagLines = slot.DiagnoseCurrentFrame();
        }
        if (_diagLines != null)
            foreach (var l in _diagLines)
                ImGuiEx.TextWrapped(l);

        var tree = r.Tree;
        if (tree == null)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, s.NoSkelCache);
            return;
        }

        ImGuiEx.Text(ImGuiColors.ParsedGreen, string.Format(s.SkelSummary, tree.BoneCount,
            r.CenterBoneIndex >= 0 ? tree.Names[r.CenterBoneIndex] : "?", r.AutoPosScale));
        if (slot.Anim != null)
        {
            ImGuiEx.Text(ImGuiColors.ParsedGreen, string.Format(s.MappedSummary, r.Mapped.Count, r.UnmappedMmd.Count));
            if (r.UnmappedMmd.Count > 0 && ImGui.TreeNode(string.Format(s.UnmappedTitle, r.UnmappedMmd.Count)))
            {
                ImGuiEx.TextWrapped(string.Join("、", r.UnmappedMmd.Take(60)));
                ImGui.TreePop();
            }
            if (r.Mapped.Count > 0 && ImGui.TreeNode(s.MappingDetail))
            {
                foreach (var m in r.Mapped)
                    ImGuiEx.Text(m.Track != null ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey,
                        m.Track != null
                            ? string.Format(s.MappedTrack, m.SourceJp, m.FfName, m.Track.Keys.Count)
                            : string.Format(s.MappedInherit, m.SourceJp, m.FfName));
                ImGui.TreePop();
            }
        }

        if (ImGui.TreeNode(string.Format(s.BoneList, tree.BoneCount)))
        {
            ImGui.BeginChild("##bonelist", new Vector2(0, 320));
            for (var i = 0; i < tree.BoneCount; i++)
            {
                var parentName = tree.Parent[i] >= 0 ? tree.Names[tree.Parent[i]] : s.RootLabel;
                ImGui.TextUnformatted($"{i,3}  {tree.Names[i]}   ←  {parentName}");
            }
            ImGui.EndChild();
            ImGui.TreePop();
        }
    }
}
