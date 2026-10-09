# Independent skirt Bullet worker

`FFMMD.Bullet.dll` owns offline, fixed-step Bullet worlds. It does not call the game's Havok simulator, read game memory, or require Blender. The managed interface is `FFMMD/Skirt/SkirtBullet.cs`.

The implementation uses Bullet 3.25, pinned to `2c204c49e56ed15ec5fcfa71d199ab6d6570b3f5`, with single-precision scalars and the C ABI in `SkirtBullet.h`. Original PMX masks are passed through: a set mask bit allows collision. PMX sphere radius, box half extents, Y-axis capsule radius/cylindrical length, and six-degree-of-freedom spring joints are supported. Kinematic movement retains the previous pose so Bullet computes contact velocities. Modes 1 and 2 both retain full rigid-body dynamics; the baker controls how their rotation/translation is applied to bones.

Generic joints use `btGeneric6DofSpring2Constraint`, `RO_XZY` in raw PMX coordinates, and spring damping 0.5. Blender 4.2's `GENERIC_SPRING` uses `RO_XYZ` after the importer reflects Y/Z; `RO_XZY` is the corresponding order when retaining original PMX coordinates and original angular bounds.

Changing a body transform refreshes its world inertia tensor before the next step. This makes resetting a rotated box before simulation equivalent to constructing it in that orientation.

All six spring axes remain enabled when their stiffness is zero, preserving mmdtools' Spring2 velocity damping. Split impulse is explicitly disabled, matching Blender's default rigid-body scene setting.

The ABI also supports collision disabling on a real joint and a `DisableCollisionPair` helper. The helper uses a Generic constraint with all six axes free and no springs, matching Blender's noncollision constraints without adding force rows. The baker can set broadphase masks to allow all groups, then reproduce mmdtools' nearby ignored-pair rules explicitly; direct raw PMX masks remain available to other callers.

Bullet's sphere/capsule collision margin is their radius. The configurable margin applies to box shapes without changing their external half extents.

Build on Windows x64 with PowerShell 7:

```powershell
pwsh -File native/SkirtBullet/Build.ps1
```

The script downloads the pinned LLVM-MinGW UCRT toolchain and sparse Bullet core source into ignored `.build` folders, builds with static C++/compiler runtimes, and writes `artifacts/x64/FFMMD.Bullet.dll` plus its import/export inspection. Existing toolchain/source locations can be supplied with `-ToolchainDirectory` and `-BulletDirectory`. Only the DLL and license notices need to be distributed; users do not install a compiler or Python environment.

Toolchain release: `llvm-mingw-20261006-ucrt-x86_64.zip`, SHA-256 `317492c456aa27ee607a5919f1d2d38dcdc1112516a24d0bf4b00d078f52d17a`. Bullet is distributed under its zlib license; compiler runtime notices are retained in `licenses`. No mmdtools GPL implementation is copied. This solver follows the same rigid-body baking approach; it is not claimed to reproduce Blender's importer, animation evaluation, and floating-point execution exactly.
