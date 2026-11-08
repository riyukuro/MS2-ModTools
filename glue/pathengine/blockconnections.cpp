// Recovered from MapleStory2.exe KMS2_DEV x64 (2022-04-13). PathEngine glue: the client's pass over a ground mesh fresh
// out of ContentProcessing3D (called from BuildAndSaveGround). MS2 maps are built from 150-unit blocks: for every block
// column the ground layers are sampled at the block corner (top down, one position per layer), then each layer is
// joined to the layer of a neighbouring block column one block up (or, failing that, one block down) by an off-mesh
// connection, unless a layer of its own column already sits there. All names are invented (no strings in the range).
// MS2-UNIT: unit_14163bc10
// MS2-RANGE: 0x14163bc10-0x141641150 .text$mn
// MS2-PINS-BEGIN tplpin
// MS2-PINS-END tplpin
#define MS2_THIS_FILE "e:\\build\\kr\\22_04\\ms2code\\client\\ms2\\pathengine\\blockconnections.cpp"
#define NOMINMAX
#include "ms2/placeholder/ms2stdafx.h"
#include "pathengineglue.h"

#include <float.h>
#include <limits.h>
#include <algorithm>
#include <map>
#include <vector>

#include "ms2/ms2recover.h"
#include "hook/ms2hook.h"

static const tSigned32 kBlockSize = 150;

// A block column (x, y in blocks), packed into the map key.
struct cBlockId
{
    unsigned short c[2];

    unsigned int key() const
    {
        unsigned int high = c[0];
        high <<= 16;
        unsigned int low = c[1];
        return high | low;
    }
};

// One ground layer found at a block corner; the PathEngine end point is added on first use.
class cGroundPoint
{
    iMesh* _mesh;
    short _x;
    short _y;
    float _height;
    tSigned32 _endPoint;
    cPosition _position;

public:
    cGroundPoint(iMesh* mesh, short x, short y, float height, const cPosition& position)
    {
        _mesh = mesh;
        _x = x;
        _y = y;
        _position = position;
        _height = height;
        _endPoint = -1;
    }

    tSigned32 endPoint()
    {
        if (_endPoint == -1)
        {
            _endPoint = _mesh->addEndPoint(_position);
        }
        return _endPoint;
    }
    short x() const
    {
        return _x;
    }
    short y() const
    {
        return _y;
    }
    float height() const
    {
        return _height;
    }
};

// Matches a layer within 4 units of a height.
struct cHeightNear
{
    float _height;

    explicit cHeightNear(float height) : _height(height)
    {
    }
    bool operator()(const cGroundPoint& point) const
    {
        return point.height() > _height - 4.f && point.height() < _height + 4.f;
    }
};

typedef std::map<unsigned int, std::vector<cGroundPoint>> tLayers;

// @0x14163bd70
static void GetBounds(iMesh* mesh, tSigned32* minimum, tSigned32* maximum, float& maximumZ)
{
    maximum[0] = maximum[1] = INT_MIN;
    minimum[0] = minimum[1] = INT_MAX;
    maximumZ = FLT_MIN;
    for (tSigned32 face = 0; face < mesh->getNumberOf3DFaces(); ++face)
    {
        for (tSigned32 vertex = 0; vertex < 3; ++vertex)
        {
            tSigned32 x, y;
            float z;
            mesh->get3DFaceVertex(face, vertex, x, y, z);
            maximum[0] = std::max(maximum[0], x);
            minimum[0] = std::min(minimum[0], x);
            maximum[1] = std::max(maximum[1], y);
            minimum[1] = std::min(minimum[1], y);
            maximumZ = std::max(maximumZ, z);
        }
    }
}

// @0x14163bf70
static void CollectLayers(const tSigned32* minimumBlock, const tSigned32* maximumBlock, float maximumZ, iMesh* mesh,
                          tLayers& layers)
{
    for (tSigned32 bx = minimumBlock[0]; bx <= maximumBlock[0]; ++bx)
    {
        for (tSigned32 by = minimumBlock[1]; by <= maximumBlock[1]; ++by)
        {
            tSigned32 point[3] = {bx * kBlockSize, by * kBlockSize, static_cast<tSigned32>(maximumZ) + 25};
            cPosition position = mesh->positionFor3DPoint(point[0], point[1], point[2]);
            while (position.cell != -1)
            {
                cBlockId id = {static_cast<unsigned short>(bx), static_cast<unsigned short>(by)};
                std::vector<cGroundPoint>& column = layers[id.key()];
                float height = mesh->heightAtPositionF(position);
                column.push_back(cGroundPoint(mesh, bx, by, height, position));
                point[2] = static_cast<tSigned32>(height) - 25;
                position = mesh->positionFor3DPoint(point[0], point[1], point[2]);
            }
        }
    }
}

// @0x14163e910
static cGroundPoint* FindLayer(tLayers& layers, unsigned int key, const cHeightNear& predicate)
{
    tLayers::iterator it = layers.find(key);
    if (it == layers.end())
    {
        return 0;
    }
    std::vector<cGroundPoint>& column = it->second;
    std::vector<cGroundPoint>::iterator found = std::find_if(column.begin(), column.end(), predicate);
    if (found == column.end())
    {
        return 0;
    }
    return &*found;
}

// @0x14163c220
static void ConnectLayers(tLayers& layers, iMesh* mesh)
{
    for (tLayers::iterator it = layers.begin(); it != layers.end(); ++it)
    {
        std::vector<cGroundPoint>& column = it->second;
        for (unsigned int i = 0; i < column.size(); ++i)
        {
            cGroundPoint& point = column[i];
            cHeightNear oneUp(point.height() + 150.f);
            cHeightNear twoUp(point.height() + 300.f);
            cHeightNear oneDown(point.height() - 150.f);
            cHeightNear level(point.height());
            cBlockId id = {static_cast<unsigned short>(point.x()), static_cast<unsigned short>(point.y())};
            unsigned int key = id.key();
            short neighbours[4][2] = {{1, 0}, {-1, 0}, {0, -1}, {0, 1}};
            if (FindLayer(layers, key, oneUp))
            {
                continue;
            }
            bool roofed = FindLayer(layers, key, twoUp) != 0;
            for (int n = 0; n < 4; ++n)
            {
                cBlockId other = {static_cast<unsigned short>(neighbours[n][0] + point.x()),
                                  static_cast<unsigned short>(neighbours[n][1] + point.y())};
                cGroundPoint* target = FindLayer(layers, other.key(), oneUp);
                if (target)
                {
                    if (!roofed)
                    {
                        mesh->addOffMeshConnection(point.endPoint(), target->endPoint(), 1);
                    }
                }
                else
                {
                    target = FindLayer(layers, other.key(), oneDown);
                    if (target)
                    {
                        if (FindLayer(layers, other.key(), level))
                        {
                            continue;
                        }
                        if (!FindLayer(layers, other.key(), oneUp))
                        {
                            mesh->addOffMeshConnection(point.endPoint(), target->endPoint(), 1);
                        }
                    }
                }
            }
        }
    }
}

// @0x14163bc10
void cBlockConnections::build(iMesh* mesh)
{
    float maximumZ;
    tSigned32 minimum[2];
    tSigned32 maximum[2];
    GetBounds(mesh, minimum, maximum, maximumZ);
    tLayers layers;
    tSigned32 minimumBlock[2] = {minimum[0] / kBlockSize, minimum[1] / kBlockSize};
    tSigned32 maximumBlock[2] = {maximum[0] / kBlockSize, maximum[1] / kBlockSize};
    CollectLayers(minimumBlock, maximumBlock, maximumZ, mesh, layers);
    ConnectLayers(layers, mesh);
}

MS2_REPLACE_METHOD(0x14163BC10, cBlockConnections, build, 235, 0xD94A4A84);
MS2_REPLACE(0x14163BD70, GetBounds, 503, 0x7D993111);
MS2_REPLACE(0x14163BF70, CollectLayers, 673, 0x2B83EE18);
MS2_REPLACE(0x14163C220, ConnectLayers, 1073, 0x0B48C353);
MS2_REPLACE(0x14163E910, FindLayer, 220, 0x7DED2876);
