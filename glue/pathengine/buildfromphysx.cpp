// Recovered from MapleStory2.exe KMS2_DEV x64 (2022-04-13). PathEngine glue: the client's version of the SDK sample's
// ExampleBuildFromPhysX/LoadCP3D (file name assumed; the range has no __FILE__ of its own). A copy of the PhysX scene
// is made without the excluded actors (boxes and triangle meshes shrunk vertically by 74/75, triangle meshes recooked
// through Gamebryo's NiPhysXMemStream), turned into a PathEngine ground mesh by ContentProcessing3D.dll, linked
// across blocks and saved as .tok to a file or to memory. cFileOutputStream is the client's copy of the SDK's
// fileoutputstream.h (one line longer). See pathengineglue.h.
// MS2-UNIT: ?\nxcapsuleshapedesc.cpp
// MS2-RANGE: 0x141642030-0x141649870 .text$mn
// MS2-RANGE: 0x142cb3d10-0x142cb3d30 .text$yd
// MS2-PINS-BEGIN tplpin
// MS2-PINS-END tplpin
#define MS2_THIS_FILE "e:\\build\\kr\\22_04\\ms2code\\client\\ms2\\pathengine\\buildfromphysx.cpp"
#define NOMINMAX
#include "ms2/placeholder/ms2stdafx.h"
#include "pathengineglue.h"

#include <NxPhysics.h>
#include <NxCooking.h>
#include <NiMemObject.h>
#include <NiPhysXMemStream.h>
#include <NiString.h>

#include <stdio.h>
#include <errno.h>
#include <map>
#include <set>
#include <vector>

#include "ms2/ms2recover.h"
#include "hook/ms2hook.h"

// loadcontentprocessing3d.cpp
void LoadContentProcessing3D(const char* fileName, iErrorHandler* errorHandler, void*& root,
                             iContentProcessing3D*& contentProcessing);
void ReportErrorMessage(const char* title, const char* text);
void Error(const char* errorType, const char* errorString, const char* const* attributes);
bool HandleAssertion(const char* file, long line, const char* comment);

float FUN_14147af60(float);

// The exe's assert expression strings are passed as text.
#define MS2_PE_ASSERTD(file, line, condition, text)                                                                  \
    {                                                                                                                \
        static bool reportThis = true;                                                                               \
        if (reportThis && !(condition))                                                                              \
            reportThis = HandleAssertion(file, line, text);                                                          \
    }
#define MS2_PE_FILEOUTPUTSTREAM_H "e:\\build\\kr\\22_04\\ms2code\\client\\ms2\\pathengine\\fileoutputstream.h"

// Writes the .tok to a file (the client's fileoutputstream.h).
class cFileOutputStream : public iOutputStream
{
    FILE* _fp;

public:
    cFileOutputStream(const char* path)
    {
        _fp = fopen(path, "wb");
        if (!_fp)
        {
            char errorNumber[20];
            sprintf(errorNumber, "%i", errno);
            const char* details[5];
            details[0] = "fileName";
            details[1] = path;
            details[2] = "errno";
            details[3] = errorNumber;
            details[4] = 0;
            Error("NonFatal", "Could not open file for writing. Nothing will be written.", details);
        }
    }
    ~cFileOutputStream()
    {
        if (_fp)
        {
            int closeResult = fclose(_fp);
            MS2_PE_ASSERTD(MS2_PE_FILEOUTPUTSTREAM_H, 47, !closeResult, "!errorOccurred");
        }
    }
    void put(const char* bytes, tUnsigned32 count)
    {
        if (_fp)
        {
            size_t done = fwrite(bytes, 1, count, _fp);
            MS2_PE_ASSERTD(MS2_PE_FILEOUTPUTSTREAM_H, 55, done == count, "written == dataSize");
        }
    }
};

// Appends to a caller's byte buffer, growing its capacity in powers of two from 1 KB.
// (The class name is misspelt in the exe's RTTI.)
class cMemoryOutputStresm : public iOutputStream
{
    std::vector<unsigned char>* _buffer;
    size_t _size;

public:
    cMemoryOutputStresm(std::vector<unsigned char>& buffer)
    {
        _buffer = &buffer;
        _size = 0;
        _buffer->reserve(0x400);
    }
    void put(const char* bytes, tUnsigned32 count)
    {
        if (_size + count > _buffer->capacity())
        {
            size_t capacity = _buffer->capacity();
            if (capacity == 0)
            {
                capacity = 0x400;
            }
            while (capacity < _size + count)
            {
                capacity <<= 1;
            }
            _buffer->reserve(capacity);
        }
        _buffer->insert(_buffer->end(), bytes, bytes + count);
        _size += count;
    }
};

class cErrorHandler : public iErrorHandler
{
    cErrorHandler() {}

public:
    tSigned32 handle(const char* type, const char* text, const char* const*)
    {
        ReportErrorMessage(type, text);
        return 0;
    }
    static cErrorHandler& instance()
    {
        static cErrorHandler single;
        return single;
    }
};

// Copies of the scene's shape descriptors (owned, deleted with the copier); triangle meshes are recooked once each.
class cShapeDescCopier
{
    std::map<NxTriangleMesh*, NxTriangleMesh*> _recookedMeshes;
    std::vector<NxShapeDesc*> _descs;

    template <class tDesc, class tShape> tDesc* copy(NxShape* shape)
    {
        tDesc* desc = new tDesc;
        _descs.push_back(desc);
        tShape* typedShape = static_cast<tShape*>(shape);
        typedShape->saveToDesc(*desc);
        return desc;
    }

public:
    ~cShapeDescCopier()
    {
        for (unsigned int i = 0; i < _descs.size(); ++i)
        {
            delete _descs[i];
        }
        _descs.clear();
    }

    NxShapeDesc* copyBox(NxShape* shape);
    NxShapeDesc* copyTriangleMesh(NxShape* shape);
    NxShapeDesc* copyOther(NxShape* shape);
};

static const float kVerticalShrink = 0.9866667f;

// @0x141642800
NxScene* CopyScene(NxScene* source, std::set<NxActor*>& excludedActors)
{
    NxSceneDesc sceneDesc;
    source->saveToDesc(sceneDesc);
    cShapeDescCopier copier;
    NxPhysicsSDK& sdk = source->getPhysicsSDK();
    NxScene* scene = sdk.createScene(sceneDesc);
    NxActor** actors = source->getActors();
    for (unsigned int i = 0; i < source->getNbActors(); ++i)
    {
        NxActor* actor = actors[i];
        if (excludedActors.find(actor) != excludedActors.end())
        {
            continue;
        }
        NxActorDesc actorDesc;
        actor->saveToDesc(actorDesc);
        NxBodyDesc bodyDesc;
        if (actor->isDynamic())
        {
            actor->saveBodyToDesc(bodyDesc);
            actorDesc.body = &bodyDesc;
        }
        NxShape* const* shapes = actor->getShapes();
        NxArray<NxShapeDesc*, NxAllocatorDefault>& shapeDescs = actorDesc.shapes;
        for (unsigned int j = 0; j < actor->getNbShapes(); ++j)
        {
            NxShape* shape = shapes[j];
            NxShapeType type = shape->getType();
            NxShapeDesc*& shapeDesc = shapeDescs.pushBack();
            if (type == NX_SHAPE_MESH)
            {
                shapeDesc = copier.copyTriangleMesh(shape);
            }
            else if (type == NX_SHAPE_BOX)
            {
                shapeDesc = copier.copyBox(shape);
            }
            else
            {
                shapeDesc = copier.copyOther(shape);
            }
            if (shapeDesc == 0)
            {
                shapeDescs.popBack();
            }
        }
        scene->createActor(actorDesc);
    }
    return scene;
}

static bool LoadPathEngine(iContentProcessing3D*& contentProcessing);

// @0x141642030
bool BuildAndSaveGround(const NiString& fileName, NxScene* scene, std::set<NxActor*>& excludedActors)
{
    if (scene == 0)
    {
        return false;
    }
    scene = CopyScene(scene, excludedActors);   // the copy replaces the argument
    iContentProcessing3D* contentProcessing;
    if (!LoadPathEngine(contentProcessing))
    {
        return false;
    }

    cCoordinateMapping mapping;
    std::vector<const iFaceVertexMesh*> meshes;
    iSolidObjects* solids;
    const float scale = 100.f;
    BuildCP3DSnapshotFromPhysX(scene, meshes, solids, mapping, scale);

    const tSigned32 extrudeHeight = 128;
    const tSigned32 maxStepHeight = 145;
    tSigned32 maxSlope = static_cast<tSigned32>(FUN_14147af60(1.553343f) * 100.f);
    const char* options[] = {"method", "bsp", "excludeDownwardFacingFromGroundResult", "true", 0};
    iMesh* mesh = contentProcessing->buildGround(meshes.empty() ? 0 : &meshes[0],
                                                 static_cast<tSigned32>(meshes.size()), solids,
                                                 extrudeHeight, maxStepHeight, maxSlope, options, 0);
    if (mesh)
    {
        cBlockConnections connections;
        connections.build(mesh);
        cFileOutputStream fos(fileName);
        mesh->saveGround("tok", false, fos);
        mesh->destroy();
    }
    DeleteCP3DSnapshotFromPhysX(meshes, solids);
    scene->getPhysicsSDK().releaseScene(*scene);
    return true;
}

// @0x141642330
bool BuildAndSaveGround(std::vector<unsigned char>& buffer, NxScene* scene, std::set<NxActor*>& excludedActors)
{
    if (scene == 0)
    {
        return false;
    }
    scene = CopyScene(scene, excludedActors);   // the copy replaces the argument
    iContentProcessing3D* contentProcessing;
    if (!LoadPathEngine(contentProcessing))
    {
        return false;
    }

    cCoordinateMapping mapping;
    std::vector<const iFaceVertexMesh*> meshes;
    iSolidObjects* solids;
    const float scale = 100.f;
    BuildCP3DSnapshotFromPhysX(scene, meshes, solids, mapping, scale);

    const tSigned32 extrudeHeight = 128;
    const tSigned32 maxStepHeight = 145;
    tSigned32 maxSlope = static_cast<tSigned32>(FUN_14147af60(1.553343f) * 100.f);
    const char* options[] = {"method", "bsp", "excludeDownwardFacingFromGroundResult", "true", 0};
    iMesh* mesh = contentProcessing->buildGround(meshes.empty() ? 0 : &meshes[0],
                                                 static_cast<tSigned32>(meshes.size()), solids,
                                                 extrudeHeight, maxStepHeight, maxSlope, options, 0);
    if (mesh)
    {
        cBlockConnections connections;
        connections.build(mesh);
        cMemoryOutputStresm os(buffer);
        mesh->saveGround("tok", false, os);
        mesh->destroy();
    }
    DeleteCP3DSnapshotFromPhysX(meshes, solids);
    scene->getPhysicsSDK().releaseScene(*scene);
    return true;
}

// @0x141642620
static bool LoadPathEngine(iContentProcessing3D*& contentProcessing)
{
    void* root;
    LoadContentProcessing3D("ContentProcessing3D.dll", &cErrorHandler::instance(), root, contentProcessing);
    if (contentProcessing == 0)
    {
        return false;
    }
    if (contentProcessing->getInterfaceMajorVersion() != 4 || contentProcessing->getInterfaceMinorVersion() < 0)
    {
        cErrorHandler::instance().handle(
            "Fatal", "LoadPathEngine: 3D content processing dll version is incompatible with headers used for compilation.",
            0);
        return false;
    }
    return true;
}

// Moves every shape of the scene horizontally (unused).
// @0x1416426d0
void OffsetScene(NxScene* scene, float dx, float dy)
{
    NxActor** actors = scene->getActors();
    for (unsigned int i = 0; i < scene->getNbActors(); ++i)
    {
        NxActor* actor = actors[i];
        NxShape* const* shapes = actor->getShapes();
        for (unsigned int j = 0; j < actor->getNbShapes(); ++j)
        {
            NxShape* shape = shapes[j];
            NxVec3 position = shape->getGlobalPosition();
            position.x += dx;
            position.y += dy;
            shape->setGlobalPosition(position);
        }
    }
}

// @0x141646290
NxShapeDesc* cShapeDescCopier::copyBox(NxShape* shape)
{
    if (shape == 0)
    {
        return 0;
    }
    if (shape->getType() != NX_SHAPE_BOX)
    {
        return 0;
    }
    NxBoxShapeDesc* desc = copy<NxBoxShapeDesc, NxBoxShape>(shape);
    desc->dimensions.z *= kVerticalShrink;
    return desc;
}

// @0x141646310
NxShapeDesc* cShapeDescCopier::copyTriangleMesh(NxShape* shape)
{
    if (shape == 0)
    {
        return 0;
    }
    if (shape->getType() != NX_SHAPE_MESH)
    {
        return 0;
    }
    NxTriangleMeshShapeDesc* desc = new NxTriangleMeshShapeDesc;
    _descs.push_back(desc);
    NxTriangleMeshShape* meshShape = static_cast<NxTriangleMeshShape*>(shape);
    meshShape->saveToDesc(*desc);

    NxTriangleMesh* mesh = 0;
    std::map<NxTriangleMesh*, NxTriangleMesh*>::iterator it;
    it = _recookedMeshes.find(desc->meshData);
    if (it != _recookedMeshes.end())
    {
        mesh = it->second;
    }
    else
    {
        NxTriangleMeshDesc meshDesc;
        desc->meshData->saveToDesc(meshDesc);
        NxVec3* points = (NxVec3*)meshDesc.points;
        for (unsigned int i = 0; i < meshDesc.numVertices; ++i)
        {
            points[i].z *= kVerticalShrink;
        }
        NxU32 pointStride = meshDesc.pointStrideBytes;   // unused (an unused-variable idiom survives at /Od)
        pointStride = pointStride;
        NiPhysXMemStream* stream = NiNew NiPhysXMemStream;
        NxCookingInterface* cooking = NxGetCookingLib(NX_PHYSICS_SDK_VERSION);
        cooking->NxInitCooking(0, 0);
        cooking->NxCookTriangleMesh(meshDesc, *stream);
        stream->Reset();
        mesh = shape->getActor().getScene().getPhysicsSDK().createTriangleMesh(*stream);
        _recookedMeshes.insert(std::make_pair(desc->meshData, mesh));
    }
    desc->meshData = mesh;
    return desc;
}

// @0x1416466c0
NxShapeDesc* cShapeDescCopier::copyOther(NxShape* shape)
{
    if (shape == 0)
    {
        return 0;
    }
    NxShapeType type = shape->getType();
    if (type == NX_SHAPE_CONVEX)
    {
        return copy<NxConvexShapeDesc, NxConvexShape>(shape);
    }
    else if (type == NX_SHAPE_PLANE)
    {
        return copy<NxPlaneShapeDesc, NxPlaneShape>(shape);
    }
    else if (type == NX_SHAPE_SPHERE)
    {
        return copy<NxSphereShapeDesc, NxSphereShape>(shape);
    }
    else if (type == NX_SHAPE_CAPSULE)
    {
        return copy<NxCapsuleShapeDesc, NxCapsuleShape>(shape);
    }
    else if (type == NX_SHAPE_WHEEL)
    {
        return copy<NxWheelShapeDesc, NxWheelShape>(shape);
    }
    else if (type == NX_SHAPE_HEIGHTFIELD)
    {
        return copy<NxHeightFieldShapeDesc, NxHeightFieldShape>(shape);
    }
    return 0;
}

typedef bool tBuildAndSaveGroundToFile(const NiString&, NxScene*, std::set<NxActor*>&);
typedef bool tBuildAndSaveGroundToBuffer(std::vector<unsigned char>&, NxScene*, std::set<NxActor*>&);

MS2_REPLACE_OVERLOAD(0x141642030, tBuildAndSaveGroundToFile, BuildAndSaveGround, 750, 0x8F35740D);
MS2_REPLACE_OVERLOAD(0x141642330, tBuildAndSaveGroundToBuffer, BuildAndSaveGround, 742, 0xE52B5275);
MS2_REPLACE(0x141642620, LoadPathEngine, 161, 0x5D8F2662);
MS2_REPLACE(0x1416426D0, OffsetScene, 298, 0xB23DEA39);
MS2_REPLACE(0x141642800, CopyScene, 806, 0x22010A27);
MS2_REPLACE_METHOD(0x141646290, cShapeDescCopier, copyBox, 118, 0xBBFF7B89);
MS2_REPLACE_METHOD(0x141646310, cShapeDescCopier, copyTriangleMesh, 940, 0x8FC5051F);
MS2_REPLACE_METHOD(0x1416466C0, cShapeDescCopier, copyOther, 206, 0xC692070D);
