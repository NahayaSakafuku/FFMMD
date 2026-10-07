# FFMMD

在 FF14 游戏内直接导入 VMD 并驱动 GPose 人形角色,无需 Blender;可导入本地音乐与动作同步播放。默认使用标准 MMD A 姿态源骨架,可选 T 姿态或导入动作对应的 PMX 2.0/2.1 骨架。

## 功能

- VMD 骨骼与 IK 开关采样、贝塞尔插值、播放/暂停/循环/跳帧。
- 先解算源 FK、追加变换、足/足尖 IK 和限位,再适配目标参考姿态。
- 按实际目标腿长保留腿方向、膝盖角和弯曲平面,转移脚部朝向。
- 一个统一坐标变换和共享位移起点;自动按腿长缩放整体移动。
- 保留原生平移/缩放与未驱动骨骼;同步已识别的其他骨架分段连接。
- 上身参考曲线、臂部扭转与辅助骨、手掌坐标中的拇指适配;四指三节到两节的长度加权弯曲合并。
- **音乐配乐**:导入本地 wav/ogg/mp3,与动画共用时间轴;音量/偏移可滑条或键入;按动作记忆音乐关联;同步变速(变调)。
- **全局快捷键**:播放/暂停/停止三个可配置快捷键,窗口关闭时仍有效(默认小键盘 1/2/3)。
- **原生声音屏蔽**:音乐播放期间自动把游戏主音量归零,停止后恢复原值。
- 当前帧源/目标及游戏写入前后/最终阶段诊断导出,分别统计缩放变化。
- 禁止动画写入任何骨骼缩放,保留原生比例;离地高度通过整体根平移调整。

缺少对应源 PMX 时明确标示近似适配;不同体型不保证脚落点相同。当前不播放表情、镜头或物理,不模拟外部亲及物理结果。

## 安装

### 方式一:插件仓库(推荐,可自动更新)

1. 游戏内输入 `/xlsettings`,进入 实验性(Experimental) 选项卡;
2. 在 自定义插件仓库(Custom Plugin Repositories) 中添加:
   `https://raw.githubusercontent.com/NahayaSakafuku/FFMMD/main/repo.json`
3. 保存;在 Dalamud 插件列表搜索 FFMMD 并安装。

**国内网络镜像**(jsDelivr,版本化标签):
`https://cdn.jsdelivr.net/gh/NahayaSakafuku/FFMMD@v1.0.0.0/repo.json`

### 方式二:手动安装

从 [Releases](https://github.com/NahayaSakafuku/FFMMD/releases) 下载 `latest.zip`,解压到
`%APPDATA%\XIVLauncherCN\installedPlugins\FFMMD\1.0.0.0\`,重启游戏后在 `/xlplugins` 启用。

## 构建

需要 .NET 10 SDK 和 API 15 Dalamud dev 库。固定版本的 ECommons 源码随包提供,本地兼容改动记录在其 `LOCAL_PATCHES.md` 中。

```powershell
./build.ps1
./build.ps1 -TestVmd 'E:/mmd/bibbidibaFull_DanceMotion.vmd' -TestPmx '<可选验证用 PMX 路径>'
```

支持 `-DalamudPath`、`-DotnetPath`、`-PackageCachePath`。脚本自动检测 XIVLauncherCN/XIVLauncher,先执行离线测试再构建,产物为 `FFMMD/bin/Release/FFMMD/latest.zip`。

离线测试:

```powershell
dotnet run --project FFMMD.Test
dotnet run --project FFMMD.Test -- --verify '<VMD 路径>'
# 单独运行时,设置 FFMMD_TEST_PMX 环境变量即可增加真实 PMX 检查。
```

无素材时运行内置独立样例;提供真实 VMD 和 PMX 时共 110 项动画管线检查,另有 25 项音乐 transport/变速采样回归。真实 186 骨样例已随测试工程提供,素材来源说明见 `FFMMD.Test/Fixtures/README.md`。

## 参考与许可证

灵感来自 [Endfield-Poser](https://github.com/OedoSoldier/Endfield-Poser);游戏访问/同步规则核查参考 [Brio](https://github.com/Etheirys/Brio)、[Dalamud](https://github.com/goatcorp/Dalamud)、[FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)。新重定向核心与音乐播放独立实现,保留项目 GPL-3.0 许可证。

