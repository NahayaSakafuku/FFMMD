# FFMMD 交接报告(Handoff)

> 写给下一位接手本项目的开发者/AI。本文档自包含:不需要本项目之前的任何对话上下文。
> 最后更新:2026-10-05,v1.1.1(commit d3d9daf)。

---

## 1. 项目是什么

**FFMMD** 是一个 FF14(最终幻想14)的卫月(Dalamud)插件:在游戏内直接导入 VMD(MMD 动作)文件并逐帧驱动角色骨骼播放,无需 Blender。灵感来自《明日方舟:终末地》的 Endfield-Poser 插件。

- 仓库/目录:`FFMMD\`(独立 git 仓库,main 分支)
- 主工程:`FFMMD\FFMMD.csproj`(SDK `Dalamud.NET.SDK/15.0.0`,net10.0-windows,unsafe)
- 离线测试台:`FFMMD.Test\FFMMD.Test.csproj`(纯 .NET 控制台,直接编译 `FFMMD\Vmd\*.cs`,可无游戏验证解析器)
- 作者:NahayaSakafuku;License:GPL-3.0(参考了 Brio/Ktisis 的 GPL 代码)

## 2. 构建与部署(已验证可用)

```
构建:  cd FFMMD && dotnet build FFMMD/FFMMD.csproj -c Release
        (bash 下用 "/c/Program Files/dotnet/dotnet.exe")
产物:  FFMMD/bin/Release/FFMMD/latest.zip(DalamudPackager 打包,含 manifest)
部署:  解压到 %APPDATA%\XIVLauncherCN\installedPlugins\FFMMD\<版本号>\
        ⚠️ 升级时删除旧版本目录;改默认值时同时删除 %APPDATA%\XIVLauncherCN\pluginConfigs\FFMMD\
离线测试: dotnet run --project FFMMD.Test              # 合成 VMD 往返自测(14 项断言)
          dotnet run --project FFMMD.Test -- <vmd路径>   # 真实文件分析报告(骨骼/旋转量/表示IK)
```

- Dalamud dev 库:`%APPDATA%\XIVLauncherCN\addon\Hooks\dev\`(启动器自动更新,曾更到 15.0.3.6)
- ECommons 以 ProjectReference 引用:`MoodlesPlus\ECommons\ECommons\ECommons.csproj`
- 本地参考源码:`Dalamud`(15.0.3.6)、`FFXIVClientStructs`
- 本地测试资源:
  - 测试动作:`bibbidibaFull_DanceMotion.vmd`(207 轨道/4891 帧/模型名 ModelA)
  - AnimationKit(官方教程工具包):`AnimationKit\AnimationKit\`
    - `Retargeting File\Free retarget\KaitoToMMDV1.blend-retarget`(逐骨 rest 矩阵,JSON)
    - `MMD Model\Kaito\KAITOwCOAT+phy-RexZ-A.pmx`(源标准模型,**尚未解析使用**)
    - `FFXIV Model\MMDModel.blend`(FF14 骨架重建为 MMD 式绑定的 Blender 文件)
- 视频帧分析工具(用户录像诊断):`python -m pip install imageio imageio-ffmpeg pillow`,用 imageio_ffmpeg 自带的 ffmpeg.exe 抽帧 + PIL 算相邻帧差

## 3. 核心技术事实(全部已验证,勿重新调研)

### 3.1 游戏侧骨骼链(FFXIVClientStructs,版本需与游戏对齐)

```
IGameObject.Address → (GameObject*)addr → +0x100 DrawObject → 强转 (CharacterBase*)
CharacterBase+0xA0  → Render::Skeleton*(+0x50 PartialSkeletonCount, +0x68 PartialSkeletons)
PartialSkeleton.GetHavokPose(0) → hkaPose*(+0x00 hkaSkeleton*, +0x08 LocalPose, +0x18 ModelPose,
                                          +0x38 ModelInSync, +0x39 LocalInSync)
hkaSkeleton:+0x18 ParentIndices(short[],-1=根) +0x28 Bones(hkaBone{Name hkStringPtr}) +0x38 ReferencePose
hkQsTransformf: +0x00 Translation +0x10 Rotation +0x20 Scale(各 16 字节,hkVector4f/hkQuaternionf 为 X/Y/Z/W float)
```
- **写姿态**:直写 `pose->ModelPose.Data[i]` **并同时写** `LocalPose.Data[i]`(平移=RefLocalPos 恒定,重心骨额外加位移 offset 的父系变换),然后 `ModelInSync=1; LocalInSync=1`。渲染读 ModelPose;双写+双标志保证任何"局部↔模型重算"都不会把姿态弹回(卡顿保险)。
- **写入时机**:hook `CharacterBase::UpdateBonePhysics` post(Brio 同款,签名
  `48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4`,
  delegate `nint(nint)`,**第一个参数语义不可靠,Brio 也完全忽略它**)。绝不用 `IFramework.Update` 写骨骼(会被当帧动画覆盖)。hook 是热路径:零分配、早退。
- **每帧一次**:用 `_appliedThisFrame` 标志(在 Framework.Tick 帧首复位)去重。
- **计时**:动画时钟用 `Stopwatch.GetTimestamp()`——`Environment.TickCount64` 分辨率 10-16ms,会让时钟抖(已修的卡顿病因之一)。
- **物理冻结**:曾用 Anamnesis 签名 NOP 补丁(`0F 11 48 10 41 0F 10 44 24 ?? 0F 11 40 20 48 8B 46 28`,NOP4@addr + NOP3@addr-9),因未 VirtualProtect 直接 AV 崩溃,**已整体移除**。若重做必须 VirtualProtect 且警惕指令边界,建议改 hook 方式。
- **GPose**:`pi.UiBuilder.DisableGposeUiHide = true`(Dalamud 默认在 GPose 隐藏插件 UI!);`Svc.ClientState.IsGPosing` 边沿检测自动弹窗(GPose 里聊天框不可用,/ffmmd 敲不了)。
- 武器是独立 CharacterBase(DrawData),v1 未处理;面部在 partial 1/2,v1 未处理。

### 3.2 FF14 原生骨架(游戏内 dump 实测,172 骨 partial 0)

- 层级:`n_root(根) → n_hara → { j_kosi(髋,腿挂它), j_sebo_a(脊柱,与 j_kosi 是兄弟!) → j_sebo_b → j_sebo_c → j_kubi(颈) → j_kao(头) }`
- 手臂:`j_sako(锁骨) → n_hkata(肩) → j_ude_a(大臂) → n_hhiji(肘) → j_ude_b(前臂) → j_te(手)`;手指 `j_oya/j_hito/j_naka/j_kusu/j_ko` 各 a/b 两节
- 腿:`j_asi_a(大腿) → j_asi_b(膝辅助,不映射) → j_asi_c(小腿) → j_asi_d(脚) → j_asi_e(足尖)`
- 裙 `j_sk_*`、尾 `n_sippo_*`、发 `j_kami_*` 为物理骨,不映射(游戏物理在渲染阶段会覆盖它们)
- 官方骨骼文档:https://xivmodding.com/books/ff14-asset-reference-document/page/bone-list-and-bone-scaling-notes
- FF14 骨架约定:-Z 朝前、Y 向上(用户 Blender 导出设置佐证)

### 3.3 VMD 格式(解析器已实现并测试)

- 头 30B("Vocaloid Motion Data 0002")+ 模型名 20B(Shift-JIS,**需注册 CodePagesEncodingProvider**)
- 骨骼 KF:count × **111B**(名15 + 帧4 + 位置12 + 四元数16 + 插值64);位置是**相对骨骼自身 rest 的增量**
- 插值 64B 规范布局:字节 0..7 = X/Y/Z/R 四条贝塞尔的 P1x/P2x(按曲线交错),8..15 = 对应 P1y/P2y;16..63 冗余
- morph KF 23B;相机 KF 105B;照明 28B;自阴影 **10B(帧4+模式u16+距离4,曾错写成 9)**;表示IK 变长
- ⚠️ 本文件(可能为常见转换动作)的表示IK段与标准布局不符(名字解析乱码),位于文件末尾,错位不影响播放,已放弃深究
- VMD 是 **30fps** 帧号;播放时钟以秒×30 采样,帧间贝塞尔插值(牛顿迭代+二分,曲线在加载时预解析为零分配)

### 3.4 骨名匹配(关键坑)

- 不同模型的 VMD 骨名差异:躯干大骨基本是 MMD 标准名(センター/上半身/腕/足…),**末端骨一模型一套**
- bibbidiba 实测:手指用**全角数字**(`右中指１`)+ "人差指"缩写为"人指" → 匹配前两侧都做 **NFKC 规范化**(`string.Normalize(FormKC)`) + 候选名列表(见 `Retarget\BoneMap.cs`)
- 足尖变体:`つま先`/`足先EX`

## 4. 重定向方案演进(5 版实测结论,核心知识)

动作→FF14 的旋转映射试了三代公式,**没有一代对所有动作完美**,原因见 §6:

| 版本 | 公式 | 实测 |
|---|---|---|
| v1.0.0.2-3 | 世界增量 Δ(mmd链) ∘ RefModel,conjugate by 全局 G | 方向对(需 Z180),手臂拧转(弯轴不跟父骨)、僵硬 |
| v1.0.0.4-7 | **局部静止系**:swing=G∘q∘G⁻¹ 经父骨 RefModel 换算后叠在 RefLocal 前 | **当前默认(标准模式)**。转换动作最好水准:"有动作但僵硬不精确" |
| v1.1.0 | AnimationKit/Mwni rest 管线(见下) | 数学正确但对 bibbidiba **打结**(其数据非 MMD 标准轴),已降为可选模式 |

**当前实现(标准模式)**,`Retarget\Retargeter.cs` Evaluate:
```
swing = G ∘ q_mmd自身 ∘ G⁻¹
local = RefModelRot[父]⁻¹ ∘ swing ∘ RefModelRot[父] ∘ RefLocalRot[本骨]   (q=I ⇒ 精确回到 FF14 绑定)
模型空间逐骨级联 + 位置沿 FF14 参考偏移级联(保骨长)
```

**AnimationKit rest 模式(可选)**,来自 Mwni/blender-animation-retargeting 的 drivers.py(已读源码确认):
```
rest[T] = FF14 骨 T 被摆成"对应 MMD 骨姿态"时的模型空间矩阵(KaitoToMMDV1.blend-retarget,JSON/MMD 系)
Local = G ∘ RestArm(父FF骨)⁻¹ ∘ [Offset ∘ RestArm(T)⁻¹ ∘ q_eff ∘ RestArm(T)] ∘ G⁻¹
q_eff = q_自身 ∘ ∏(源骨与最近已映射祖先之间的未映射中间骨,如手指中节)
数据文件:Retarget\RestRetargetData.cs(48 对,由 JSON 代码生成;裙骨除外)
中立姿势 = MMD 匹配姿势(q=I 时),不是 FF14 绑定——这是官方管线语义
```

**G(旋转轴向校准)** = `RotX180/RotY180/RotZ180 三个 180° 开关 + 整体朝向 yaw 滑条`(Calibration)。**实测 Z180 方向正确**(默认开)。
**位移换算与旋转解耦**:固定 `RotY(180°+yaw)`(MMD 的 X左/Z前 → FF14 的 X右/Z后,垂直永不翻转——曾因复用 Z180 把垂直翻转导致"腿不动")。
**位移缩放**:自动 = FF14 髋高 ÷ 10(MMD 标准髋高,单位≈8cm);手动滑条兜底。基线 = センター+グルーブ 轨道的**逐轴中位数**(首帧法对 Y≈0 的动作失效)。

## 5. 测试动作(bibbidiba)的数据结论(重要!)

`bibbidibaFull_DanceMotion.vmd`(模型名 ModelA):
- **它是转换类动作**:脊柱(上半身/上半身2)与肘部轨道存在**持续 ~179° 四元数**,正常 MMD 舞蹈不可能有 → 其"标准 MMD 骨名"下的旋转写在与 MMD 不同的骨骼局部轴上(疑 Blender/VRChat 转换产物)。任何假设 MMD 标准轴的通用映射对它都有残差。
- **腿部编舞 100% 在足ＩＫ**:左/右足ＩＫ 765/776 KF,抬腿 0→6.1 单位(≈49cm)+ 跨舞台位移 ±35 单位;`ひざ`(膝)FK 只有 **1 KF**;足 FK 虽有 512/560 KF 但幅度小(每秒 5-35°,手臂是 20-78°)→ **FK 模式腿接近静止是数据属性**,腿要动必须走足ＩＫ求解(双骨解析 IK 已实现:余弦定理+参考膝方向极向量,`腿部驱动=自动` 默认启用)。
- 手指轨道用全角数字;表示IK段布局非标。
- 用户实测反馈时间线:v1.0.0.7(auto IK 腿)"腿部确实有动作了,但仍然诡异的僵硬且不精确";v1.1.0(rest 管线)"打结"。

## 6. 未解决问题(按优先级)

1. **转换类动作的"僵硬且不精确"**(核心遗留):根因 = 动作骨骼局部轴 ≠ MMD 标准,通用公式无法根治。可行方向:
   a. **逐动作/逐模型轴修正预设**(Endfield-Poser 的"角色专属校准"同思路):给 BoneMap 每条绑定加 per-pair 修正四元数,数据驱动;
   b. **解析动作目标模型的 PMX**(若用户能提供 ModelA.pmx):从 PMX 读真实骨骼局部轴/位置,做精确源侧 rest——`AnimationKit\MMD Model\Kaito\*.pmx` 的解析器写好后可复用;
   c. 离线烘焙路线(Blender 管线)作为保底。
2. **播放中的卡顿未确诊**:用户两次录像都是暂停+拖进度条状态(逐帧差分+背景差分+音频响度已验证),没有捕捉到播放态。需要一段**不暂停、进度匀速走**的录像,量化帧差节奏(每帧抖/隔帧更新/周期顿挫,修法完全不同);同时看卡顿时游戏 FPS 是否下跌。已修的相关项:Stopwatch 时钟、LocalPose 双写。
3. rest 模式的"JSON 帧↔游戏帧"转换系数 C 尚未验证(当前假设 C≡G,即 JSON 系=MMD 系;有翻译脚本的数值可交叉验证)。对标准 MMD 动作启用 rest 模式前建议先验证。
4. 物理冻结(抑制头发/尾巴物理干扰)已移除,可改 hook 方式重做。
5. 表情 morph 未映射(FF14 无 blendshape 脸);武器骨架未处理;相机 VMD/音乐对轴未做(规划 v1.2)。

## 7. 代码地图

```
FFMMD/
├─ FFMMD.csproj / FFMMD.json          # SDK 15,net10.0-windows;InternalName=FFMMD
├─ GlobalUsings.cs                    # ECommons/Svc/ImGui + using static FFMMDPlugin(P)
├─ Plugin.cs                          # 入口:编码注册/DisableGposeUiHide/ECommonsInit//ffmmd 命令
├─ Configuration.cs                   # Config(含 RetargetMode)+ Calibration(轴向/幅度/位移/LegIkMode/MedianBaseline)
├─ Vmd/VmdParser.cs                   # VMD 二进制解析(纯 .NET,测试台复用)
├─ Vmd/VmdAnimation.cs                # 轨道/贝塞尔采样(零分配,曲线预解析)
├─ Posing/SkeletonTree.cs             # hkaPose 包装:骨名/父子/参考姿势局部+模型(可被测试台直接 new)
├─ Posing/BoneApplier.cs              # UpdateBonePhysics post-hook(忽略参数,OnUpdateBonePhysics 回调)
├─ Retarget/BoneMap.cs                # Bindings(标准模式映射+候选名)/MmdParents(层级)/IgnoredBones
├─ Retarget/RestRetargetData.cs       # 代码生成:48 对 rest/offset(来自 blend-retarget JSON)
├─ Retarget/Retargeter.cs             # 核心:双模式旋转映射/级联/重心位移/腿IK/Diagnose 自检/TestSetup
├─ Player/VmdPlayerService.cs         # 状态机/时钟(Stopwatch)/目标解析/GPose 弹窗/PendingLoadPath
├─ UI/MainWindow.cs                   # 播放/校准/调试(骨架 dump、映射明细、自检按钮)
FFMMD.Test/Program.cs                 # 离线自测 + 真实 VMD 分析(旋转量随时间/表示IK/位移范围)
```

关键运行时行为:
- `Retargeter.EnsureSkeleton` 按(骨架指针, RetargetMode)缓存;`ResetCache()` 换动作时调用
- `Evaluate(frame, cal)` 全程零分配,输出 `_rot/_rotLocal/_pos/_rotLocPose/_posLocPose`;`Diagnose(frame, cal)` 返回逐骨三级读数
- UI 改配置走 `P.ConfigDirty` 脏标记 + 3 秒节流落盘(pluginConfigs\FFMMD\FFMMD.json)
- 文件对话框回调线程 → `PendingLoadPath` 字段 → Tick 消费(线程安全交接)

## 8. 给接手者的建议路线

1. 先跑通环境:构建 → 部署 → 用 bibbidiba 动作复现"能动但僵硬"(标准模式+自动 IK 腿+Z180)。
2. 卡顿确诊:让用户提供不暂停的录像,用 §2 的抽帧+帧差方法量化;同时看 FPS。
3. 僵硬根治:优先方向 6.1b——写 PMX 骨骼解析(拿到动作对应模型的真实骨骼轴),源侧 rest 从 PMX 生成,替代"标准 MMD 假设"。KAITO PMX 解析器可先拿 AnimationKit 的模型练手并对照 blend-retarget 的 rest 矩阵验证。
4. 若做逐骨校准预设:BoneMap.Bindings 每条加 `Quaternion AxisFix` 字段 + UI 导入导出(结构已预留,Endfield-Poser 模式)。
5. 提醒:所有内存签名随游戏版本更新会失效;FF14 第三方工具违反 SE ToS,传播需免责声明(README 已含)。

## 9. git 摘要(main 分支)

```
d3d9daf v1.1.1: dual retarget modes, default back to the robust local-rest formula
22a3847 v1.1.0: implement the AnimationKit/Mwni retarget pipeline in-game
e7ea4ff v1.0.0.7: default leg driver back to auto (IK when motion is IK-authored)
61f7f36 v1.0.0.6: legs use the same FK retarget path as the upper body by default
2706877 v1.0.0.5: decouple position axis from rotation calibration, hi-res clock
4d657ca v1.0.0.4: local-rest-frame retargeting (fixes stiffness and arm twist)
6607fc1 v1.0.0.1: fix playback, remove unsafe physics-freeze patch, correct bone map
043eca5 v1.0.0.2: world-delta retargeting, official bone map, GPose UI fix
37c2ddc FFMMD v1.0.0.0: in-game VMD (MMD motion) playback plugin
```

## 10. 参考链接

- Endfield-Poser(灵感来源,含"逐角色校准"设计):https://github.com/OedoSoldier/Endfield-Poser
- Brio(骨骼 hook/覆盖式应用参考,GPL-3.0):https://github.com/Etheirys/Brio
- Ktisis(拦截式冻结参考,GPL-3.0):https://github.com/ktisis-tools/Ktisis
- Mwni blender-animation-retargeting(rest 数学出处):https://github.com/Mwni/blender-animation-retargeting
- vmd_sizing(MIT,离线动作适配,用户手上的中文 exe 是它的换皮):https://github.com/miu200521358/vmd_sizing
- MMD→FFXIV 官方教程(AnimationKit 出处):https://xivmodding.com/books/mmd-to-ffxiv-guide
- blender_mmd_tools:https://github.com/MMD-Blender/blender_mmd_tools
