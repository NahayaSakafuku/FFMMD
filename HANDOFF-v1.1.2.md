# FFMMD 当前交接：v1.1.2

日期：2026-10-06。旧交接原文保存在 HANDOFF-LEGACY.md，其中的“动作非标准”“持续 179°”“尾段非标准”“双写已一致”等结论已经被审查推翻，勿作为事实引用。

## 状态

- 修复实现及验证范围见 RELEASE-v1.1.2.md。
- Release 构建成功；14+27 共 41 项离线检查通过，含用户提供的 bibbidiba。
- 尚未运行游戏；角色视觉还原、hook 签名命中、游戏线程时序、换装/销毁和性能均未现场验收。
- 工作遵循“直接在 FF14 内播放 VMD”目标；没有将 Blender 作为运行必需项。

## 工程

- `FFMMD/Vmd/`：纯 .NET 格式解析、贝塞尔采样和 IK 开关时间轴。
- `Posing/SkeletonData.cs`：无游戏依赖的骨架数据和带缩放的参考级联。
- `Posing/SkeletonTree.cs`：hkaPose 读取与缓冲区验证。
- `Retarget/Retargeter.Core.cs`：纯 .NET 本帧重定向、手指合并、FK/IK、输出缓冲区。
- `Retarget/Retargeter.cs`：游戏骨架缓存与同步写入桥接。
- `Player/VmdPlayerService.cs`：GPose 角色选择、身份检查、缓存准备、播放/暂停/停止/预览。
- `Retarget/RestRetargetData.cs`：未使用的历史参考表，不是已验证的转换算法。
- `FFMMD.Test/RegressionSuite.cs`：独立格式样例和姿态不变量测试，直接编译生产代码，不使用假的游戏结构。
- `vendor/ECommons/`：固定源码与许可证；LOCAL_PATCHES.md 记录本地兼容改动。

## 构建

需要 SDK 10.x 与 API 15 的 Dalamud dev 库。`build.ps1` 会先执行回归测试，再构建打包；可传 `-DalamudPath`、`-TestVmd`、`-DotnetPath`。产物 `FFMMD/bin/Release/FFMMD/latest.zip`。

本次未改动 E:\mmd 原始材料。安装使用独立解压目录中的 FFMMD.dll 开发加载；停用旧同名来源。没有要求删除已有校准配置。

## 接下来

先按 RELEASE-v1.1.2.md 的步骤实测，确认 GPose 副本、连续播放、暂停/跳帧、模式切换、换装和退出。只有获得有效现象或测量之后，才进一步调整坐标校准、源骨架/PMX 适配和求解器约束。

不要把 VMD 最大旋转角度当成文件非标准的证据。该动作的 IK 尾段严格符合标准，全部读取至 EOF；肘部没有 >170° 关键帧，少量脊柱大角度不足以证明改轴来源。
