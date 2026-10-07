using System.Text;

namespace FFMMD;

public class FFMMDPlugin : IDalamudPlugin
{
    public static FFMMDPlugin P = null!;
    public Config Config = new();
    public Player.VmdPlayerService Player = null!;
    public Player.MusicService? Music;
    public Posing.BoneApplier Applier = null!;
    private bool _disposed;
    private bool _needsConfigBackup;

    /// <summary> UI 改了配置的脏标记，由播放器 Tick 节流落盘。 </summary>
    public bool ConfigDirty;

    private string ConfigPath => Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "FFMMD.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public FFMMDPlugin(IDalamudPluginInterface pi)
    {
        P = this;
        // VMD 骨骼名是 Shift-JIS（932），.NET Core 默认不带这张码表。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // Dalamud 默认在 GPose 里隐藏插件 UI（Brio/Ktisis 都关掉了这一行为），否则窗口进 GPose 看不见。
        pi.UiBuilder.DisableGposeUiHide = true;
        ECommonsMain.Init(pi, this);
        new TickScheduler(() =>
        {
            if (_disposed) return;
            Config = Load();
            var oldPipeline = Config.RigPipelineVersion < 5;
            Config.Normalize();
            ConfigDirty = oldPipeline;
            Applier = new Posing.BoneApplier();
            Player = new Player.VmdPlayerService(Applier);
            // 音乐跟随动画 transport;Player Dispose 时先发出 Disposing 通知再由 Music 释放设备。
            Music = new Player.MusicService();
            Player.TransportChanged += Music.OnTransport;
            EzConfigGui.Init(UI.MainWindow.Draw);
            EzConfigGui.Window.SetMinSize(560, 420);
            EzCmd.Add("/ffmmd", ToggleUi, Loc.S.CmdDesc);
            PluginLog.Information($"[FFMMD] 初始化完成。骨骼Hook: {(Applier.Available ? "OK" : "未命中: " + Applier.Error)}");
        });
    }

    private void ToggleUi(string _, string __)
    {
        if (EzConfigGui.Window is { } window)
            window.IsOpen = !window.IsOpen;
    }

    public void Save()
    {
        try
        {
            EnsureConfigBackup();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOptions));
            ConfigDirty = false;
        }
        catch (Exception e)
        {
            PluginLog.Error($"[FFMMD] 配置保存失败: {e.Message}");
        }
    }

    private Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                _needsConfigBackup=true;
                var config=JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions) ?? new();
                _needsConfigBackup=config.RigPipelineVersion<5;
                try{EnsureConfigBackup();}
                catch(Exception e){PluginLog.Error($"[FFMMD] 旧配置备份失败，将保留原文件并重试：{e.Message}");}
                return config;
            }
        }
        catch (Exception e)
        {
            PluginLog.Error($"[FFMMD] 配置读取失败: {e.Message}");
        }
        return new();
    }
    private void EnsureConfigBackup()
    {
        if(!_needsConfigBackup)return;
        if(File.Exists(ConfigPath))
            File.Copy(ConfigPath,Path.Combine(Path.GetDirectoryName(ConfigPath)!, $"FFMMD.pre-v1.1.6-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"));
        _needsConfigBackup=false;
    }

    public void Dispose()
    {
        _disposed = true;
        if (ConfigDirty && Config != null) Save();
        Player?.Dispose();
        Music?.Dispose();
        Applier?.Dispose();
        ECommonsMain.Dispose();
        P = null!;
    }
}
