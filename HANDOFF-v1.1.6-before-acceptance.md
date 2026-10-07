# FFMMD 当前交接：v1.1.6

2026-10-06。用户明确左手正确、右手反向；否定锁定 n_hara，要求考虑 n_root 与地面关系。本版修正右手镜像和整体放置。源码／测试／构建完成，新世界放置及右手皮肤视觉仍待同角色验收。当前有效说明为 RELEASE-v1.1.6.md。

## 本次证据

E:/TX/QQ/QQdownloads/diagnostics (1).zip 原件只读，提取到 work/acceptance-1.1.5。两帧 1768.81 / 2596.80，172 骨，Kaito body 源。Root 模型 Y 已保持 -0.03；旧手工高度和质心下移需要区分。用户已关闭 LockHeight，仍有下移。

现场原生 BeforeWrite 握拳的左右远节本地 Z 都为负角。1.1.5 输出左负右正；叉积给出的只是有向平面法线，右手的镜像手性未处理。旧测试把两手 palm 都视为手腕局部 -Z，重复了错误假设，不能沿用为验收依据。

## 主要改变

- Retargeter.FourFingers：右手 palm 法线反转；闭合与张开符号分离。左手完全保持，右手弯曲符号翻转但张开角保持。限位及三节合并仍沿用；拇指不变。
- TargetRigProfile.GroundCoordinateMap：整体人物放置使用明确 Y-up 源／目标坐标，left/back 投影到地面；偏航围绕 Y。
- Retargeter.Core：Root 保持参考位置+HeightOffset；Root 的朝向使用地面坐标，不再把 root→pelvis 参考对齐加入假倾斜。源 FK pelvis 位移通过地面基作用到 target reference center，减去当前父骨转动后的基线再反算 local，避免重复计根旋转。N_hara 升降正常，不锁定。
- 已验收的身体角度／腿姿参考仍使用原解剖坐标适配；整体放置与参考姿态各自有明确空间。初始统一水平移动锚点不变，无全段统计或时间累加。
- LockHeight 仅保留旧配置反序列化，不参与运行；Normalize 强制解除，pipeline5；升级前备份 pre-v1.1.6，保留根偏移／PMX／朝向等。UI 移除质心锁定开关，改称根骨离地高度偏移。
- RigDiagnostics schema5：HeightAnchorY 是 root；center 单独记录；GroundPlacementMatrix、root prepared/after/final Y；PlacementSnapshot 只读记录 Actor/Draw 变换。若 DrawObject 有父对象，WorldY 留空；未实现父链世界合成。

## 根与地面关系的边界

已验证当前模型 root 在 Actor 放置原点附近、参考模型 Y=0。可以作为放置锚点，不等于证明它就是物理碰撞箱。Actor.Position.Y 是当前放置参考，不是地形射线或鞋底测量；不写 Actor 世界变换／碰撞体／任何 Scale。新快照记录 root 相对 Actor Y，便于用户在当前地点确定固定偏移。

实际元数据接口来自 FFXIVClientStructs Scene.Object（Position/Rotation/Scale/ParentObject）和 DrawObject 的独立 bounds 接口；不能由骨名推出碰撞实现。不要宣传自动贴地或地形检测。脚部与鞋底依旧可能随比例、动作变化离地，本轮禁止拉长骨骼去追地面。

## 验证

110 = 14 基础 + 13 格式 + 33 管线 + 19 前轮现场 + 18 更新的四指/根回归 + 13 新回归。撤回旧质心锁定验收，改为 root 恒定、center 保留真实源 Y。新增基于真实原生握拳数据的左右负 Z 参考，左手保持、右手闭合翻转/张开保持、旧颈/臂/拇指保持、根不假倾斜、水平百单位无下沉、蹲起/跳跃、根旋转单次计位移、迁移与诊断锚点。

全段每15帧抽样中 root=-0.03，center 约0.831–1.012跟随源动作，非根局部平移及骨长保持。未测新皮肤最终形态或世界地形；新世界高度读取也需现场确认。Release API15/.NET10，0错误、1 NU1900 服务不可达警告（未关闭审计）。

## 下轮验收

先根偏移0，保持同角色与素材。58.96/86.56检查右手闭合与正确左手；142.61检查长距离移动不下沉；蹲起与跳跃不能被锁死。导出schema5看 root model/world、Actor placement、center 的正常变化与 scale audit。附着对象世界高度为空需额外研究，不能当成功地形测量。

完整历史保留 HANDOFF-v1.1.5/v1.1.4/v1.1.3/... 与发行说明。1.1.5 的同手掌面和固定质心结论已撤回；不再当作已验收事实。原始输入只读，当前交付位于 outputs，不部署或发布。
