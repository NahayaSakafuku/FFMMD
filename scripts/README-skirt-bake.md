# 开发用裙骨离线烘焙对照

`BlenderBakeSkirt.py` 是独立的 Blender 4.2 headless 开发对照工具。产品的自动裙物理
已采用插件内独立 Bullet 固定步长解算；玩家不需要安装 Blender。此脚本通过已安装的
MMD Tools 公开 operator 导入完整 PMX（骨架与物理）和 VMD，建立 Blender 的刚体
世界，固定步长烘焙，然后只导出 18 根裙骨的旋转缓存。它不会修改 FFMMD 插件、
原 PMX、VMD 或用户 Blender 设置。

## 使用

```powershell
E:\blender4\blender.exe -b --factory-startup `
  --python E:\moodles\FFMMD\scripts\BlenderBakeSkirt.py -- `
  --pmx E:\path\model.pmx `
  --vmd E:\path\motion.vmd `
  --out E:\path\skirt-cache.ffskirt.json
```

默认参数为 `scale=0.08`、30 FPS、`substeps=120`、`iterations=40`、60 帧预热、
`collision-margin=1e-6`。大幅动作可先提高 substeps/iterations；这会增加烘焙
时间，结果仍必须以画面检查为准。`--start`/`--end` 可限制诊断用输出帧段（插件播放需要从 0 到动作末帧的完整缓存），`--bones`
可传逗号分隔的 PMX 裙骨名；默认使用 `Skirt_0..2_0..5`。

预热阶段保持 VMD 首帧的身体姿态，让刚体先静置。这里的 60 帧不会增加导出的
VMD 时间，也不会把动作身体改成休息姿态；动作帧 0 对应 Blender 帧 61。
`--no-physics` 只用于基线验证，输出会明确标成未烘焙，插件不能把它当物理缓存。

输出 JSON SchemaVersion=1，包含 PMX/VMD SHA-256、30 FPS、帧数、Blender armature
参考基、18 根源/目标骨名、每帧 parent-relative 四元数，以及 Solver 元数据。
四元数是相对于 PMX rest pose 的 parent-relative delta，不是 Blender 的
`rotation_quaternion`；目标映射按 `j_sk_f_a_r`、`j_sk_f_a_l`、`j_sk_s_a_l`、
`j_sk_b_a_l`、`j_sk_b_a_r`、`j_sk_s_a_r` 顺序展开到 b/c 层。输出 Solver 中会
记录 `PointCacheBaked=true`、刚体/关节数量和 addon 版本。

## 平滑与碰撞检查

recipe 2 保留原始 PMX 横向关节、刚体阻尼、质量、碰撞组和尺寸。默认以左右各
2 帧的短窗口生成双向 quaternion 平滑候选，强度为 0.65，每个局部旋转修正最多
3°。较大角变化的相邻帧权重会降低，不会用大范围低通追赶腿部动作。

每帧根据原始参考裙箱体和腿部 sphere/capsule 构造候选几何，再比较每个碰撞对
的有符号距离穿透指标。任何一个碰撞对变差超过 `1e-7` Blender 单位，就对所有
裙片使用相同的减半系数，最多回退 8 次，仍不通过则保留原始帧。这样保留完整
横向耦合解算，也不让碰撞对总和的改善掩盖某一对恶化。该判据只测参考刚体代理，
不测 FFXIV 实际裙网格或完整 PMX 蒙皮表面。

`--smooth-window 2 --smooth-max-degrees 3 --smooth-strength 0.65` 可明确传入默认
参数。`--skirt-angular-damping` 是开发对照选项，默认 `-1` 保持 PMX 数据；不应
仅为软化效果放宽关节极限或移除横向约束。脚本输出 `Solver.Smoothing`，记录原始/
过滤后的角变化指标、回退次数、穿透指标和修正上限；阶段进度格式为
`FFMMD_PROGRESS {"Phase":"smooth","Fraction":0.5}`。

数学模块可独立验证，不需要 Blender：

```powershell
python -m unittest discover -s E:\moodles\FFMMD\scripts -p test_skirt_smoothing.py
```

## 依赖与边界

Blender 的 MMD Tools 扩展是 GPL-3.0-or-later；本工具只在独立 Blender 进程中
调用它的公开接口，没有复制或链接其源码进入 FFMMD。产生的 JSON 只描述这份 PMX
和这套动作，不能直接解决其它裙骨拓扑或衣服网格；FFXIV 的 mesh 穿插仍需目标
服装、碰撞体和实际画面验收。Blender Bullet 与 MMD 2.75 的软约束并不完全相同，
所以该缓存是稳定的离线近似结果，不宣称等同 MMD 原生解算。

完整烘焙期间可查看控制台进度；成功结束会输出 `FFMMD_BAKE_OK`。工具用
临时文件后原子替换输出，失败不会覆盖旧缓存。

## 与当前插件的关系

本脚本保留作开发期对照，普通导入流程不会调用它。当前插件在导入 VMD 后通过
内置 Bullet 后台解算，并将自己的内容缓存保存到配置目录的 `skirt-cache`。
旧 Blender 验证结果可用于离线比较，不作为内置 solver 的生产缓存预置。

身体源 PMX 与裙骨参考独立。当前裙骨应用要求完整动作幅度、自动位移比例和
VMD 自动 IK；目标骨架变化会重新核验 18 骨映射。Seek、暂停和循环直接采样
烘焙结果，native hook 只复制已准备的裙骨旋转，不写 scale 或裙骨平移。

当前诊断 schema 9 的 `OfflineSkirtCacheApplied`、`OfflineSkirtSolver` 与
`OfflineSkirtPreparedLocalRotations` 可确认缓存应用。玩家使用说明见根目录 README。

方案参考社区 [Skirt Physics 教程](https://xivmodding.com/books/mmd-to-ffxiv-guide/page/skirt-physics)
与 [blender_mmd_tools](https://github.com/powroupi/blender_mmd_tools)。内置参考来源见
[FFMMD/Skirt/README.md](../FFMMD/Skirt/README.md)。
