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
        ImGuiEx.Text(ImGuiColors.ParsedGreen, P.Applier.Available ? Loc.S.HookOk : string.Format(Loc.S.HookMissing, P.Applier.Error));
        ImGui.SameLine();
        ImGuiEx.Text(ImGuiColors.DalamudGrey, Player.TargetValid ? string.Format(Loc.S.TargetName, Player.TargetName) : Loc.S.TargetNone);
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
        DrawTargetSelector();

        // 文件加载
        var loaded = Player.LoadedPath == null ? Loc.S.NoMotionLoaded : Path.GetFileName(Player.LoadedPath);
        if (ImGui.Button(Loc.S.ImportVmd)) PickVmdFile();
        ImGui.SameLine();
        ImGuiEx.Text(Player.LoadError != null ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey,
            Player.LoadError != null ? string.Format(Loc.S.LoadFailed, Player.LoadError) : loaded);

        if (Player.Anim != null)
        {
            // 走带控制
            if (ImGui.Button(Loc.S.Play)) Player.Play();
            ImGui.SameLine();
            var paused = Player.Paused;
            if (ImGui.Checkbox(Loc.S.Pause, ref paused)) Player.SetPaused(paused);
            ImGui.SameLine();
            if (ImGui.Button(Loc.S.Stop)) Player.Stop();
            ImGui.SameLine();
            var loop = P.Config.Loop;
            if (ImGui.Checkbox(Loc.S.Loop, ref loop))
            {
                P.Config.Loop = loop;
                P.ConfigDirty = true;
            }

            var speed = P.Config.Speed;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderFloat(Loc.S.Speed, ref speed, 0.25f, 4f, "%.2fx"))
                Player.SetSpeed(speed);

            var dur = Player.Anim.DurationSec;
            var t = (float)Player.TimeSec;
            ImGui.SetNextItemWidth(-1);
            if (dur > 0 && ImGui.SliderFloat(string.Format(Loc.S.Progress, t, dur, Player.TimeSec * Vmd.VmdAnimation.FramesPerSecond), ref t, 0f, dur, "%.2f"))
                Player.Seek(t);
        }

        var autoOpen = P.Config.AutoOpenInGPose;
        if (ImGui.Checkbox(Loc.S.AutoOpenGpose, ref autoOpen))
        {
            P.Config.AutoOpenInGPose = autoOpen;
            P.ConfigDirty = true;
        }

        // 全局快捷键:窗口关闭时仍有效,由 VmdPlayerService 在 Tick 中检测按下沿。
        ImGui.TextUnformatted(Loc.S.Hotkeys);
        ImGui.SameLine();
        DrawHotkeyPicker(Loc.S.HkPlay + "##hkPlay", P.Config.PlayHotkey, v => { P.Config.PlayHotkey = v; P.ConfigDirty = true; });
        ImGui.SameLine();
        DrawHotkeyPicker(Loc.S.HkPause + "##hkPause", P.Config.PauseHotkey, v => { P.Config.PauseHotkey = v; P.ConfigDirty = true; });
        ImGui.SameLine();
        DrawHotkeyPicker(Loc.S.HkStop + "##hkStop", P.Config.StopHotkey, v => { P.Config.StopHotkey = v; P.ConfigDirty = true; });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.S.HkTip);
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, Loc.S.HkHelp);
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

    private static void DrawTargetSelector()
    {
        ImGui.TextUnformatted(Loc.S.TargetModeTitle);
        ImGui.SameLine();
        var mode = P.Config.TargetMode;
        if (ImGui.RadioButton(Loc.S.TargetSelf, mode == 0))
        {
            P.Config.TargetMode = 0;
            P.ConfigDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.S.TargetCurrent, mode == 1))
        {
            P.Config.TargetMode = 1;
            P.ConfigDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.S.TargetNearby, mode == 2))
        {
            P.Config.TargetMode = 2;
            P.ConfigDirty = true;
        }

        if (P.Config.TargetMode == 2)
        {
            var nearby = Player.GetNearbyPlayers();
            var current = nearby.FirstOrDefault(x => x.Id == P.Config.NearbyObjectId);
            var preview = current.Id == 0 ? Loc.S.PickNearby : current.Name;
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##nearby", preview))
            {
                foreach (var (id, name, dist) in nearby)
                    if (ImGui.Selectable($"{string.Format(Loc.S.NearbyDist, name, dist)}##{id}", id == P.Config.NearbyObjectId))
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
        if (!ImGui.CollapsingHeader(Loc.S.CalSection)) return;
        var s = Loc.S;
        var rig = Player.Retargeter.SourceRig;
        var srcStatus = rig == null ? s.SourceNone
            : rig.Approximate ? string.Format(s.SourceApprox, rig.Name)
            : string.Format(s.SourcePmxStatus, rig.Name);
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, srcStatus);
        var preset = Cal.SourceRestPose;
        ImGui.BeginDisabled(Player.SourcePmxPath != null);
        if (ImGui.Combo(s.StandardPose, ref preset, $"{s.StandardPoseA}\0{s.StandardPoseT}\0"))
        { Cal.SourceRestPose = preset; P.ConfigDirty = true; }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(Player.Anim == null);
        if (ImGui.Button(s.PickSourcePmx))
            OpenFileDialog.SelectFile(ofn => new TickScheduler(() => Player.PendingPmxPath = ofn.file), null,
                Player.SourcePmxPath == null ? null : Path.GetDirectoryName(Player.SourcePmxPath), s.PickPmxTitle, [(s.PmxFilter, new[] { "pmx" })]);
        ImGui.SameLine();
        if (ImGui.Button(s.UseStandardSource)) Player.UseStandardSource();
        ImGui.EndDisabled();
        if (Player.SourcePmxPath != null) ImGui.TextUnformatted(Path.GetFileName(Player.SourcePmxPath));
        if (Player.SourceError != null) ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, Player.SourceError);
        if (Player.Retargeter.SourceRig is { } source)
            foreach (var warning in source.Warnings.Take(8)) ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, warning);
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

    private static void DrawDebugSection()
    {
        var s = Loc.S;
        if (!ImGui.CollapsingHeader(s.DebugSection)) return;
        var r = Player.Retargeter;
        if (ImGui.Button(s.ExportDiag)) Player.RequestDiagnostics();
        if (Player.DiagnosticStatus != null) ImGuiEx.TextWrapped(Player.DiagnosticStatus);
        if (Player.ScaleAuditStatus != null) ImGuiEx.TextWrapped(Player.ScaleAuditStatus);
        ImGui.TextUnformatted(s.VideoTimes);
        foreach (var time in new[] { 26.16, 31.17, 41.17, 46.17 })
        {
            ImGui.SameLine();
            if (ImGui.Button($"{time:0.00}s")) { Player.SetPaused(true); Player.Seek(time); }
        }

        ImGui.TextUnformatted(s.FingerTimes);
        foreach (var time in new[] { 58.9601767, 86.5599933, 142.6141433 })
        {
            ImGui.SameLine();
            if (ImGui.Button($"{time:0.00}s##finger")) { Player.SetPaused(true); Player.Seek(time); }
        }

        if (ImGui.Button(s.ReadSkeleton))
            Player.TryBuildDebugTree();
        ImGui.SameLine();
        if (ImGui.Button(s.CheckFrame))
        {
            _diagLines = Player.DiagnoseCurrentFrame();
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
        if (Player.Anim != null)
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
