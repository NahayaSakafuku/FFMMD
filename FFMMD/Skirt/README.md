# 内置裙骨参考来源

`DefaultReference.json` 来源于用户提供的 `KAITOwCOAT+phy-RexZ-A.pmx`，该模型用于社区 [MMD to FFXIV Guide — Skirt Physics](https://xivmodding.com/books/mmd-to-ffxiv-guide/page/skirt-physics) 中的修改裙骨物理方案。

- 原始参考 PMX SHA256：`5B967C5DBDB18101D1D81569DBBE63DA508E5DE39D4898ADF7953FD12E5160AC`。
- 内置内容仅为骨架和物理数值：132 根骨骼、30 个刚体、39 个关节。
- 六列三层裙骨输出映射到 FF14 的 18 根 `j_sk_*` 骨骼；播放仅应用裙骨旋转。
- 完整 PMX、网格、材质和纹理未随插件分发；参考模型及原始素材的权利归各原作者。

开发期使用 [blender_mmd_tools](https://github.com/powroupi/blender_mmd_tools) 与 Blender 烘焙结果对照，参考其 PMX 刚体、关节与逐对禁碰规则。玩家使用插件内置的独立 Bullet 3.25 解算器；无需安装或运行 Blender。该实现不承诺与 Blender 或 MMD 原生引擎的浮点结果完全相同。

默认参考用于通用 VMD 导入，不代表每个动作的原始模型或每种 FF14 衣服的实际网格；参考体型、裙形及目标衣服权重会影响效果。高级用户可指定包含兼容六列三层裙骨的参考 PMX。
