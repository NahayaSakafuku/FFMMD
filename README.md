# FFMMD

在 FF14 游戏内直接导入 VMD 并驱动 GPose 人形角色，无需 Blender。支持内置裙骨物理、本地音乐与动作同步播放，以及实验性的多人舞蹈。默认使用标准 MMD A 姿态源骨架，可选 T 姿态或导入动作对应的 PMX 2.0/2.1 骨架。

## 1.1.0.0 更新

- **支持裙骨物理**：导入 VMD 后，插件使用内置物理引擎和裙骨参考在本地后台解算，生成可复用的动画缓存。无需安装 Blender、mmd_tools 或额外提供参考模型。
- **支持多人舞蹈（实验性）**：同一 GPose 会话中可添加多个角色槽，分别选择角色、导入动作并控制播放。此功能尚未完成全部多人组合验证。
- 修复内置物理引擎的安装路径定位，保留中英文 UI、音乐同步和快捷键。

## 功能

- VMD 骨骼与 IK 开关采样、贝塞尔插值、播放/暂停/循环/跳帧。
- **裙骨物理**：自动后台预处理、进度提示、取消与重试、内容校验及磁盘缓存复用；暂停、跳帧和循环直接采样已生成的裙骨动画。
- **多人舞蹈（实验性，尚未完全验证）**：每个角色槽拥有独立目标、动作与时间轴；速度和循环设置全局共享，音乐与快捷键跟随角色 1。效果作用于本地 GPose 会话。
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

缺少对应源 PMX 时明确标示近似适配；不同体型不保证脚落点相同。当前支持裙骨物理，不播放表情、镜头、头发或其他饰品物理，也不模拟外部亲。

## 裙骨物理怎么使用

1. 进入 GPose、选择目标角色并导入 VMD；默认开启“自动裙骨物理”，使用内置参考。
2. 首次导入新动作会在本地后台解算并烘焙整段裙骨动画，界面显示处理进度。耗时取决于动作长度和 CPU 性能，较长动作可能需要等待数分钟。
3. 预处理期间可以先点击播放；准备完成且目标就绪后，动作与音乐会按待播请求启动。也可取消裙骨物理，仅播放身体，或在失败后重试。
4. 相同动作再次导入时会复用有效缓存，播放、暂停、跳帧和循环直接采样缓存。多个角色请求相同内容会共享预处理任务，其他动作按队列处理。

缓存自动保存至 `%APPDATA%\XIVLauncherCN\pluginConfigs\FFMMD\skirt-cache\`。插件根据动作内容、参考模型、引擎和解算参数等校验缓存；内容或相关实现变化时会重新处理。升级本版本后，已有旧缓存也可能需要重新生成。

解算在导入后的后台预处理阶段完成，播放时采样烘焙结果。裙骨物理不需要在每次播放时重新解算，也不要求用户运行任何额外软件。身体源 PMX 和裙骨物理参考分别设置；高级用户可替换兼容的裙骨参考 PMX。

当前裙骨缓存适用于完整动作幅度、自动位移比例和按 VMD 自动解算腿 IK；修改这些校准选项可能挂起缓存应用。默认参考针对六列三层的 18 根 FF14 裙骨，实际效果随服装长度、权重和角色体型变化。

## 安装

### 方式一:插件仓库(推荐,可自动更新)

1. 游戏内输入 `/xlsettings`,进入 实验性(Experimental) 选项卡;
2. 在 自定义插件仓库(Custom Plugin Repositories) 中添加:
   `https://raw.githubusercontent.com/NahayaSakafuku/FFMMD/main/repo.json`
3. 保存;在 Dalamud 插件列表搜索 FFMMD 并安装。

**国内网络镜像**(jsDelivr,版本化标签):
`https://cdn.jsdelivr.net/gh/NahayaSakafuku/FFMMD@v1.1.0.0/repo.json`

使用旧版本标签镜像的用户，请将仓库地址更新为上述链接以安装本版本。

### 方式二:手动安装

从 [Releases](https://github.com/NahayaSakafuku/FFMMD/releases) 下载 `latest.zip`,解压到
`%APPDATA%\XIVLauncherCN\installedPlugins\FFMMD\<版本>\`,重启游戏后在 `/xlplugins` 启用。

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

无素材时运行内置独立样例；测试覆盖动画管线、音乐、物理数据解析、后台队列、缓存、引擎路径、轨道平滑及 native Bullet。真实骨架样例已随测试工程提供，素材来源说明见 [FFMMD.Test/Fixtures/README.md](FFMMD.Test/Fixtures/README.md)。

仓库提供已构建的 Windows x64 `FFMMD.Bullet.dll`。需要自行重建物理引擎时，运行 `pwsh -File native/SkirtBullet/Build.ps1`；源码版本、工具链和许可证见 [native/SkirtBullet/README.md](native/SkirtBullet/README.md)。开发用 Blender 对照脚本见 [scripts/README-skirt-bake.md](scripts/README-skirt-bake.md)。

## 参考与许可证

| 来源 | 参考或使用内容 |
|---|---|
| [Endfield-Poser](https://github.com/OedoSoldier/Endfield-Poser) | 游戏内导入动作与姿态播放的项目灵感。 |
| [Brio](https://github.com/Etheirys/Brio)、[Dalamud](https://github.com/goatcorp/Dalamud)、[FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) | GPose 角色访问、骨架结构和姿态同步规则。 |
| [MMD to FFXIV Guide — Skirt Physics](https://xivmodding.com/books/mmd-to-ffxiv-guide/page/skirt-physics) | 裙骨物理采用刚体解算后烘焙动画的社区方案参考。 |
| [blender_mmd_tools](https://github.com/powroupi/blender_mmd_tools) | PMX 刚体、关节、碰撞关系及物理烘焙行为参考；开发期用于对照验证。 |
| [Bullet Physics](https://github.com/bulletphysics/bullet3) | 插件内置物理引擎使用 Bullet 3.25，按 zlib 许可证分发。 |
| `KAITOwCOAT+phy-RexZ-A.pmx` | 社区教程使用的修改裙骨参考模型；内置参考提取其骨架、刚体和关节数值，用于默认裙骨解算。完整 PMX、网格、材质与纹理未随插件分发。 |

感谢以上项目、教程及参考模型的作者与维护者。模型内容的权利归各原作者；参考数值来源与结构详见 [FFMMD/Skirt/README.md](FFMMD/Skirt/README.md)。

FFMMD 保留项目 [GPL-3.0 许可证](LICENSE)。内置原生引擎的 Bullet、LLVM-MinGW 和运行库许可证与声明随插件包分发，源码副本位于 [native/SkirtBullet/licenses](native/SkirtBullet/licenses)。玩家无需安装 Blender 或 mmd_tools。

