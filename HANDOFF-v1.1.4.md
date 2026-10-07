# FFMMD 当前交接：v1.1.4

2026-10-06。用户已对 1.1.3 进行实机验收，确认基本成功并指出脖子、大臂和拇指异常。用户明确禁止对 FF14 骨骼缩放：VMD 适配原角色，允许离地；可调整整体高度。新实现、79 项离线检查和构建完成，新版上身视觉仍待同角色实测。

## 当前证据

`diagnostics.zip` 包含四份 JSON、四张截图和一份视频，已提取至 workspace 的 work/acceptance-1.1.3。四帧为 784.8/935.1/1235.1/1385.1，源骨架是标准 A 近似，身体为 172 骨。

BeforeWrite→AfterWrite 的身体 LocalScales 无超过 1e-5 的变化，仅 n_hara 平移改变。颈／主臂／手指缩放保持原值。n_hkata/n_hhiji 是零位移的形变辅助叶骨，旧实现把它们当作 MMD 臂捩／手捩直接赋朝向，导致皮肤形变关系不匹配。n_hhiji 约 (1,1.535,1.241) 的非单位缩放在 BeforeWrite 已存在，不能归为动画新增缩放。

AfterWrite→FinalRender 的 j_mune_l/r 与 n_sippo_b 共三根出现尺寸调整，记录应与插件写入分开。不能由 Ktisis 仅加载或非单位缩放本身推出冲突。不能把截图的拉伸外观直接等同于骨骼 Scale 数值改变。

## 新实现

- `Retarget/RigWritePolicy.cs`：RotationTranslationWrite 只携带旋转／平移；CompareScales 对身体和分段逐骨审计，异常阈值 1e-5。
- `Posing/NativePoseWriter.cs`：唯一组件写入 helper，调用同一纯数学应用方法后只赋 Translation/Rotation；不暴露或赋 Scale。
- `Retarget/Retargeter.cs`：使用上述写入命令，平移只允许重心及整体高度根。辅助叶骨的动态缩放不参与比例缓存的刷新条件。
- `Posing/PartialPoseBridge.cs`：分段根只转移位置／旋转，去除完整变换复制，保留根及后代 native 缩放。
- `Retarget/Retargeter.Core.cs`：脊柱、颈和头不作强制直线参考对齐，保留原生曲线；HeightRootIndex 的 Y 根平移控制高度。
- `Retarget/Retargeter.UpperBody.cs`：主臂骨以源臂捩／手捩已求解朝向驱动；n_hkata/n_hhiji 旋转由主骨姿态计算（轴向反向半扭转及肘关节反向半变化），不是源控制骨朝向。不修改辅助骨缩放。手指在腕／掌坐标中传递相对旋转，建立指向与掌面校正，维持中立局部参考姿态。
- `Retarget/BoneMap.cs`：移除对 n_hkata/n_hhiji 的 MMD 直接映射；拇指近节选择源親指1（含 0 的累计），远节选择 2。
- `SourceRigDefinition`：标准手指分布改到掌面，拇指独立张开方向；fingerprint standard-a/t-v2，身体／腿定义延续原版。源 PMX 保持优先。
- `RigDiagnostics`：schema 3、插件 1.1.4；AnimationScaleWritesAllowed=false；两个时段的 ScaleAudit 分开，另记 OrientationSourceBindings。写入前后变化数显示在 UI。
- `Configuration`：HeightOffset 默认 0，有限范围 [-3,3]；RigPipelineVersion=3。1.1.3 设置保留，升级前备份 pre-v1.1.4 文件。根高度与 MotionScale 独立，不自动贴地或调整肢体长度。

## 验证

14 基础 + 13 格式 + 33 管线 + 19 现场回归 = 79 项通过。当前角色真实 172 骨样例来自现场 Target 定义；新增样例去除了人物名字、动作路径和时间戳。原始 186 骨 AnimationKit 样例仍在。

独立 90° 直臂扭转／肘弯曲验证半角辅助骨旋转与固定长度。中立源验证颈与手指保留 native 参考；手腕单独旋转不生成拇指弯曲；親指1 确实影响近节；偏航不改变手指局部运动；高度调整／归零不改变局部骨长、旋转或比例。

bibbidiba 当前角色全段每 15 帧抽样加四个现场帧验证骨长固定，四个现场帧左右上下臂方向误差 <1°；旧腿部全段 7.5 帧抽样及源 PMX、随机跳帧、零分配检查继续通过。现场原数据的零写入缩放与三根后续调整也形成回归。

Release 构建 API15 / .NET10，0 错误，仅 NU1900 服务不可达的漏洞元数据警告（未关闭审计）。安装包检查需确认 AssemblyVersion 1.1.4.0、DLL 与 Release 一致、带许可证；源码不包含缓存、游戏依赖二进制或原始动作／PMX／录像。

## 下轮验收边界

新主臂朝向、形变辅助骨旋转和手指校正是依据结构重算的策略，尚未证明皮肤网格的最终视觉。尤其 native 动态体积缩放被保留，不能通过设为 1、缩短肢体或强行贴地掩盖上身问题。新版本不控制游戏本身或其他插件在后续阶段的尺寸调整。

保持同角色与 bibbidiba，四个时间观察颈根、肩／大臂、肘、两只手拇指，检查连续播放、暂停和跳帧；导出新版四帧诊断。DuringWriteScaleAudit.ChangedBones 应为空；FinalStageScaleAudit 的结果独立判断。使用 HeightOffset 调整整体离地高度。

依旧没有获得动作原模型独立真值；标准骨架标示近似。PMX/物理/morph/表情/音乐/镜头边界参见 RELEASE-v1.1.3.md。当前有效使用与变更以 RELEASE-v1.1.4.md 为准。历史交接已保留 HANDOFF-v1.1.3、HANDOFF-v1.1.2 与 HANDOFF-LEGACY，不沿用旧 GLM 的未证结论。

所有工作在 workspace 完成；E:/mmd 和原始 diagnostics.zip 未修改，未部署游戏或在线发布。
