# glue/pathengine: frozen client code

A copy of the recovered client code that `tokbuild` needs, from
[MS2Recomp](https://github.com/riyukuro/MS2Recomp) `src/ms2/client/ms2/pathengine`. Fix bugs there, not here.

| file | use |
|---|---|
| `pathengineglue.h` | recovered declarations (`iContentProcessing3D`, `cBlockConnections`, ...); compiled into tokbuild |
| `blockconnections.cpp` | the client's off-mesh connections between block layers; compiled into tokbuild unchanged |
| `buildfromphysx.cpp` | the client's `BuildAndSaveGround`; **reference only, not compiled** (it needs the PhysX and Gamebryo headers). tokbuild's build arguments come from it |

The `MS2-*` comments, `// @0x...` tags and `MS2_REPLACE` lines are MS2Recomp's address bookkeeping. The `e:\build\...` string
in `buildfromphysx.cpp` is an assert path recovered from the client exe.

No PathEngine SDK file is here. `tools/tok/builder/CMakeLists.txt` generates small forwarding headers in its build directory
that include your SDK's headers (`MS2_PATHENGINE_SDK`) by path.
