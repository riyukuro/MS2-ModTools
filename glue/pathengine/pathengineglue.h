// Recovered from MapleStory2.exe KMS2_DEV x64 (2022-04-13). The client's PathEngine content-processing helpers
// (e:\build\kr\22_04\ms2code\client\ms2\pathengine\*): PhysX scene -> PathEngine 3D content processing input
// (convex solids + face/vertex meshes), the ContentProcessing3D.dll loader and the .tok writers.
//
// The client compiled a copy of the PathEngine SDK sample "Code Snippets/Building from Physics Scene Data" (partly
// modified). The SDK may not be redistributed, so nothing of it is copied here: its interface header and the sample
// headers whose classes the exe uses unchanged are included in place from your SDK; units whose sample .cpp is
// unchanged compile it in place; only the parts MS2 changed are recovered as our own source.
//
// The exe's slot use matches PathEngine interface major version 18 (iMesh::saveGround is slot 34, iFaceVertexMesh /
// iSolidObjects / iOutputStream / iErrorHandler identical). The content-processing interface (i_contentprocessing3d.h)
// is not in that SDK: the part the client calls is recovered below from the exe (it checks interface version 4.x).
#pragma once

#include "i_pathengine.h"   // generated forwarder to your SDK (tools/tok/builder/CMakeLists.txt)

#include "pathengine-sdk/samples/Code Snippets/Building from Physics Scene Data/SolidObjects.h"
#include "pathengine-sdk/samples/Code Snippets/Building from Physics Scene Data/PhysXTerrainWrapper.h"
// CoordinateMapping.h tests but never defines its guard macro.
#ifndef MS2_PATHENGINE_COORDINATEMAPPING_INCLUDED
#define MS2_PATHENGINE_COORDINATEMAPPING_INCLUDED
#include "pathengine-sdk/samples/Code Snippets/Building from Physics Scene Data/CoordinateMapping.h"
#endif

class NxShape;
class NxScene;
class NxTriangleMeshShape;

// The client's cPhysXTriangleMeshWrapper (unit_14164c5a0, 0x40 bytes): the SDK sample class plus a flag that makes
// faceAttribute(PE_FaceAttribute_CP3D_ExcludeFromGroundResult) return 1 (set for meshes in PhysX group 8). Declared
// here only as far as the snapshot builder uses it.
class cPhysXTriangleMeshWrapper : public iFaceVertexMesh
{
    std::vector<tSigned32> _coords;
    std::vector<tSigned32> _indices;
    bool _excludeFromGround;

public:
    cPhysXTriangleMeshWrapper(const NxTriangleMeshShape& source, const cCoordinateMapping& mapping, float scale);

    // iFaceVertexMesh
    tSigned32 faces() const;
    tSigned32 vertices() const;
    tSigned32 vertexIndex(tSigned32 face, tSigned32 vertexInFace) const;
    tSigned32 vertexX(tSigned32 vertex) const;
    tSigned32 vertexY(tSigned32 vertex) const;
    float vertexZ(tSigned32 vertex) const;
    tSigned32 faceAttribute(tSigned32 face, tSigned32 attributeIndex) const;

    void SetFlag(bool excludeFromGround);   // name invented
};

bool AddPhysXConvexShape(const NxShape& shape, float scale, cSolidObjects& addTo);

void BuildCP3DSnapshotFromPhysX(NxScene* scene, std::vector<const iFaceVertexMesh*>& meshes,
                                iSolidObjects*& solids, const cCoordinateMapping& mapping,
                                float scale = 1.f, bool includeDynamicComponents = false);
void DeleteCP3DSnapshotFromPhysX(std::vector<const iFaceVertexMesh*>& meshes, iSolidObjects*& solids);

// The content processing interface the client was built against (interface major version 4; i_contentprocessing3d.h
// is not in the SDK used here). Slots 1, 2 and 9 are the ones the exe calls; 0 and 3-5 follow iPathEngine's layout,
// 6-8 are unknown.
class iContentProcessing3D
{
public:
    virtual const char* const* getVersionAttributes() const = 0;
    virtual tSigned32 getInterfaceMajorVersion() const = 0;   // 4
    virtual tSigned32 getInterfaceMinorVersion() const = 0;   // >= 0
    virtual void getReleaseNumbers(tSigned32& majorReleaseNumber, tSigned32& minorReleaseNumber,
                                   tSigned32& internalReleaseNumber) const = 0;
    virtual void setErrorHandler(iErrorHandler* handler) = 0;
    virtual iErrorHandler* getErrorHandler() = 0;
    virtual void vfunc_6() = 0;
    virtual void vfunc_7() = 0;
    virtual void vfunc_8() = 0;
    virtual iMesh* buildGround(const iFaceVertexMesh* const* meshObjectPointers, tSigned32 numberOfMeshObjects,
                               const iSolidObjects* solids, tSigned32 extrudeHeight, tSigned32 maxStepHeight,
                               tSigned32 maxSlope, const char* const* options, iProgressCallBack* progressCallBack) = 0;
};

// The client's pass over a freshly built ground mesh (unit_14163bc10): walks MS2's 150-unit blocks and joins ground
// layers across block edges with off-mesh connections. Names invented.
class cBlockConnections
{
public:
    void build(iMesh* mesh);
};
