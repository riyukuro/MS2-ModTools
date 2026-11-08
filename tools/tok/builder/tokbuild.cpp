// tokbuild: builds a PathEngine ground mesh (.tok) for an MS2 map from a geometry dump (tools/tok/FORMAT.md), the way
// the client's dead-code builder BuildAndSaveGround does it, using the user's own ContentProcessing3D.dll.
//
// 32-bit only: the only ContentProcessing3D.dll MS2 ever shipped (GMS2, PathEngine release 5.34, iPathEngine interface
// 18.0, content processing interface 4.0) is a 32-bit DLL. Nothing PathEngine is in the repository: the DLL path is a
// command-line argument and the SDK interface header is included in place from the user's SDK (see README.md).
// The DLL is used only through its exported root interfaces.
//
// Where each part comes from:
//   - DLL loading (export ordinal 2 -> iPathEngine + iContentProcessing3D, interface major version 4):
//     MS2Recomp src/ms2/client/ms2/pathengine/loadcontentprocessing3d.cpp (LoadContentProcessing3D). On x86 the export is
//     __stdcall (12 bytes of arguments).
//   - iContentProcessing3D: the recovered declaration in glue/pathengine/pathengineglue.h.
//   - buildGround arguments (extrude 128, max step 145, max slope tan(1.553343) * 100, options method=bsp and
//     excludeDownwardFacingFromGroundResult=true, no progress callback) and the save (saveGround "tok", no
//     translation info): glue/pathengine/buildfromphysx.cpp (BuildAndSaveGround).
//   - Face attribute PE_FaceAttribute_CP3D_ExcludeFromGroundResult = 1 on flagged faces: the client's
//     cPhysXTriangleMeshWrapper::SetFlag (pathengineglue.h, addphysxconvexshape.cpp; set for PhysX group 8 meshes).
//   - The off-mesh connections between 150-unit blocks: the client's cBlockConnections, compiled unchanged from
//     glue/pathengine/blockconnections.cpp.
//   - The input conventions (x, y converted to integers by C truncation, z kept as float) and the first version of
//     this program: an earlier test harness. Output for the same input is byte-identical to it.
#define NOMINMAX
#include <windows.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

#include "pathengineglue.h"   // glue/pathengine (pulls i_pathengine.h from the user's SDK)

namespace
{
// ---------------------------------------------------------------------------------------------------------------------
// Input: tools/tok/FORMAT.md

class cDumpMesh : public iFaceVertexMesh
{
public:
    std::vector<tSigned32> x, y;
    std::vector<float> z;
    std::vector<tSigned32> index;     // 3 per face
    std::vector<tSigned32> exclude;   // 1 per face
    tSigned32 section = -1;           // .geom "section <n>": SectionID of every face (-1 = unset)

    tSigned32 faces() const { return static_cast<tSigned32>(exclude.size()); }
    tSigned32 vertices() const { return static_cast<tSigned32>(x.size()); }
    tSigned32 vertexIndex(tSigned32 face, tSigned32 vertexInFace) const { return index[face * 3 + vertexInFace]; }
    tSigned32 vertexX(tSigned32 vertex) const { return x[vertex]; }
    tSigned32 vertexY(tSigned32 vertex) const { return y[vertex]; }
    float vertexZ(tSigned32 vertex) const { return z[vertex]; }
    tSigned32 faceAttribute(tSigned32 face, tSigned32 attributeIndex) const
    {
        if (attributeIndex == PE_FaceAttribute_CP3D_ExcludeFromGroundResult)
        {
            return exclude[face];
        }
        // The client's heightfield wrapper puts every terrain face in section 0
        // (cPhysXTerrainWrapper::faceAttribute, MS2Recomp src/ms2/client/ms2/pathengine/physxterrainwrapper.cpp); its triangle
        // mesh wrapper leaves SectionID unset. A .geom gets the former with "section 0".
        if (attributeIndex == PE_FaceAttribute_SectionID)
        {
            return section;
        }
        return -1;
    }
};

class cDumpSolids : public iSolidObjects
{
public:
    std::vector<std::vector<tSigned32> > solids;   // x, y, z per point

    tSigned32 numberOfConvexSolids() const { return static_cast<tSigned32>(solids.size()); }
    tSigned32 convexSolid_Points(tSigned32 i) const { return static_cast<tSigned32>(solids[i].size() / 3); }
    void convexSolid_Point(tSigned32 i, tSigned32 p, tSigned32& x, tSigned32& y, tSigned32& z) const
    {
        x = solids[i][p * 3];
        y = solids[i][p * 3 + 1];
        z = solids[i][p * 3 + 2];
    }
    tSigned32 convexSolid_Attribute(tSigned32, tSigned32) const { return -1; }
};

void fail(const char* format, const char* arg = "")
{
    fprintf(stderr, "tokbuild: ");
    fprintf(stderr, format, arg);
    fprintf(stderr, "\n");
    exit(2);
}

// Splits a file into whitespace-separated tokens, dropping '#' comments; keeps the line number of each token.
struct cTokens
{
    std::vector<std::string> text;
    std::vector<int> line;
    size_t next;

    explicit cTokens(const char* path) : next(0)
    {
        FILE* f = fopen(path, "rb");
        if (!f)
        {
            fail("cannot open %s", path);
        }
        std::string token;
        int lineNumber = 1;
        bool comment = false;
        for (int c = fgetc(f);; c = fgetc(f))
        {
            if (c == EOF || c == ' ' || c == '\t' || c == '\r' || c == '\n' || (c == '#' && !comment))
            {
                if (!token.empty())
                {
                    text.push_back(token);
                    line.push_back(lineNumber);
                    token.clear();
                }
                if (c == EOF)
                {
                    break;
                }
                if (c == '#')
                {
                    comment = true;
                }
                if (c == '\n')
                {
                    ++lineNumber;
                    comment = false;
                }
            }
            else if (!comment)
            {
                token += static_cast<char>(c);
            }
        }
        fclose(f);
    }
    bool done() const { return next >= text.size(); }
    int currentLine() const { return next < line.size() ? line[next] : (line.empty() ? 0 : line.back()); }
    const std::string& word() { return text[next++]; }
};

// The earlier harness read numbers with %f into a float; strtof gives the same value.
float readFloat(cTokens& t, const char* path)
{
    if (t.done())
    {
        fail("%s: unexpected end of file", path);
    }
    const std::string& s = t.word();
    char* end;
    float v = strtof(s.c_str(), &end);
    if (*end)
    {
        fprintf(stderr, "tokbuild: %s line %d: ", path, t.currentLine());
        fail("not a number: %s", s.c_str());
    }
    return v;
}

long readInt(cTokens& t, const char* path)
{
    if (t.done())
    {
        fail("%s: unexpected end of file", path);
    }
    const std::string& s = t.word();
    char* end;
    long v = strtol(s.c_str(), &end, 10);
    if (*end)
    {
        fprintf(stderr, "tokbuild: %s line %d: ", path, t.currentLine());
        fail("not an integer: %s", s.c_str());
    }
    return v;
}

// .geom: "v x y z", "f a b c [exclude]" and "section n" records (0-based vertex indices). x and y become integers by C truncation.
void readGeom(const char* path, cDumpMesh& mesh)
{
    cTokens t(path);
    while (!t.done())
    {
        int line = t.currentLine();
        std::string kind = t.word();
        if (kind == "v")
        {
            float x = readFloat(t, path), y = readFloat(t, path), z = readFloat(t, path);
            mesh.x.push_back(static_cast<tSigned32>(x));
            mesh.y.push_back(static_cast<tSigned32>(y));
            mesh.z.push_back(z);
        }
        else if (kind == "f")
        {
            for (int i = 0; i < 3; ++i)
            {
                mesh.index.push_back(static_cast<tSigned32>(readInt(t, path)));
            }
            // Optional 4th field on the same line.
            tSigned32 exclude = 0;
            if (!t.done() && t.line[t.next] == line && t.text[t.next] != "v" && t.text[t.next] != "f"
                && t.text[t.next] != "section")
            {
                exclude = static_cast<tSigned32>(readInt(t, path));
            }
            mesh.exclude.push_back(exclude);
        }
        else if (kind == "section")
        {
            mesh.section = static_cast<tSigned32>(readInt(t, path));
        }
        else
        {
            fprintf(stderr, "tokbuild: %s line %d: ", path, line);
            fail("unknown record '%s' (expected v, f or section)", kind.c_str());
        }
    }
    for (size_t i = 0; i < mesh.index.size(); ++i)
    {
        if (mesh.index[i] < 0 || mesh.index[i] >= mesh.vertices())
        {
            fail("%s: face vertex index out of range", path);
        }
    }
}

// .solids: "s n x1 y1 z1 ... xn yn zn" records; every coordinate becomes an integer by C truncation.
void readSolids(const char* path, cDumpSolids& solids)
{
    cTokens t(path);
    while (!t.done())
    {
        int line = t.currentLine();
        std::string kind = t.word();
        if (kind != "s")
        {
            fprintf(stderr, "tokbuild: %s line %d: ", path, line);
            fail("unknown record '%s' (expected s)", kind.c_str());
        }
        long n = readInt(t, path);
        if (n < 4)
        {
            fprintf(stderr, "tokbuild: %s line %d: ", path, line);
            fail("a convex solid needs at least 4 points%s", "");
        }
        std::vector<tSigned32> points(n * 3);
        for (long i = 0; i < n * 3; ++i)
        {
            points[i] = static_cast<tSigned32>(readFloat(t, path));
        }
        solids.solids.push_back(points);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// PathEngine plumbing

class cErrorHandler : public iErrorHandler
{
public:
    tSigned32 handle(const char* type, const char* description, const char* const* attributes)
    {
        fprintf(stderr, "PathEngine %s: %s\n", type, description);
        for (int i = 0; attributes && attributes[i]; i += 2)
        {
            fprintf(stderr, "    %s = %s\n", attributes[i], attributes[i + 1] ? attributes[i + 1] : "");
        }
        return 0;
    }
};

class cBufferOutputStream : public iOutputStream
{
public:
    std::vector<char> bytes;
    void put(const char* data, tUnsigned32 size) { bytes.insert(bytes.end(), data, data + size); }
};

cErrorHandler gErrorHandler;

// loadcontentprocessing3d.cpp: GetProcAddress(module, ordinal 2), then the CP3D interface major version must be 4.
void loadDll(const char* path, iPathEngine*& pathEngine, iContentProcessing3D*& contentProcessing)
{
    HMODULE module = LoadLibraryA(path);
    if (!module)
    {
        fprintf(stderr, "tokbuild: LoadLibrary failed (error %lu): ", GetLastError());
        fail("%s", path);
    }
    typedef void(__stdcall * tGetInterfaces)(iErrorHandler*, iPathEngine*&, iContentProcessing3D*&);
    tGetInterfaces getInterfaces = reinterpret_cast<tGetInterfaces>(GetProcAddress(module, reinterpret_cast<LPCSTR>(2)));
    if (!getInterfaces)
    {
        fail("%s has no export ordinal 2 (not a ContentProcessing3D.dll?)", path);
    }
    pathEngine = 0;
    contentProcessing = 0;
    getInterfaces(&gErrorHandler, pathEngine, contentProcessing);
    if (!pathEngine || !contentProcessing)
    {
        fail("%s returned no interfaces", path);
    }
    if (pathEngine->getInterfaceMajorVersion() != PATHENGINE_INTERFACE_MAJOR_VERSION)
    {
        fprintf(stderr, "tokbuild: iPathEngine interface %ld.%ld, the SDK header is %ld.%ld\n",
                static_cast<long>(pathEngine->getInterfaceMajorVersion()),
                static_cast<long>(pathEngine->getInterfaceMinorVersion()),
                static_cast<long>(PATHENGINE_INTERFACE_MAJOR_VERSION),
                static_cast<long>(PATHENGINE_INTERFACE_MINOR_VERSION));
        fail("interface mismatch: use SDK headers that match the DLL%s", "");
    }
    if (contentProcessing->getInterfaceMajorVersion() != 4)
    {
        fail("content processing interface is not 4.x%s", "");
    }
}

std::vector<char> readFile(const char* path)
{
    std::vector<char> bytes;
    FILE* f = fopen(path, "rb");
    if (!f)
    {
        fail("cannot open %s", path);
    }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    bytes.resize(size);
    if (size && fread(&bytes[0], 1, size, f) != static_cast<size_t>(size))
    {
        fail("cannot read %s", path);
    }
    fclose(f);
    return bytes;
}

void writeFile(const char* path, const std::vector<char>& bytes)
{
    FILE* f = fopen(path, "wb");
    if (!f || (bytes.size() && fwrite(&bytes[0], 1, bytes.size(), f) != bytes.size()) || fclose(f))
    {
        fail("cannot write %s", path);
    }
}

// One line per 3D face: "t x y z x y z x y z c0 c1 c2" (same text as the earlier harness's .faces dumps).
void writeFaces(iMesh* mesh, const char* path)
{
    FILE* f = fopen(path, "w");
    if (!f)
    {
        fail("cannot write %s", path);
    }
    tSigned32 n = mesh->getNumberOf3DFaces();
    for (tSigned32 face = 0; face < n; ++face)
    {
        fprintf(f, "t");
        for (int v = 0; v < 3; ++v)
        {
            tSigned32 x, y;
            float z;
            mesh->get3DFaceVertex(face, v, x, y, z);
            fprintf(f, " %ld %ld %.1f", static_cast<long>(x), static_cast<long>(y), z);
        }
        for (int v = 0; v < 3; ++v)
        {
            fprintf(f, " %ld", static_cast<long>(mesh->get3DFaceConnection(face, v)));
        }
        fprintf(f, "\n");
    }
    fclose(f);
}

void usage()
{
    fprintf(stderr,
            "usage:\n"
            "  tokbuild build --dll <ContentProcessing3D.dll> --out <out.tok> [--geom <file.geom>]... [--solids <file.solids>]\n"
            "                 [--faces <out.faces>] [--no-connections] [--option <name>=<value>]...\n"
            "      Ground from the dump with the client's buildGround settings, then the client's block connections.\n"
            "      --geom may be given more than once (one PathEngine face/vertex mesh each, in order).\n"
            "      --option adds a buildGround option after the client's (method=bsp, excludeDownwardFacingFromGroundResult=true).\n"
            "  tokbuild reconnect --dll <ContentProcessing3D.dll> --in <in.tok> --out <out.tok> [--faces <out.faces>]\n"
            "      Loads a .tok, drops its off-mesh connections and end points and reruns the client's block connections.\n"
            "  tokbuild info --dll <ContentProcessing3D.dll> [--in <in.tok>]\n"
            "      Prints the DLL's versions (and face/connection counts of a .tok).\n");
    exit(1);
}
}   // namespace

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        usage();
    }
    std::string command = argv[1];
    const char* dll = 0;
    const char* out = 0;
    const char* in = 0;
    const char* solidsPath = 0;
    const char* facesPath = 0;
    bool connections = true;
    std::vector<const char*> geomPaths;
    std::vector<std::string> extraOptions;
    for (int i = 2; i < argc; ++i)
    {
        std::string a = argv[i];
        bool hasValue = i + 1 < argc;
        if (a == "--dll" && hasValue)
            dll = argv[++i];
        else if (a == "--out" && hasValue)
            out = argv[++i];
        else if (a == "--in" && hasValue)
            in = argv[++i];
        else if (a == "--geom" && hasValue)
            geomPaths.push_back(argv[++i]);
        else if (a == "--solids" && hasValue)
            solidsPath = argv[++i];
        else if (a == "--faces" && hasValue)
            facesPath = argv[++i];
        else if (a == "--option" && hasValue)
        {
            std::string o = argv[++i];
            size_t eq = o.find('=');
            if (eq == std::string::npos || eq == 0)
            {
                fail("--option wants name=value, got %s", o.c_str());
            }
            extraOptions.push_back(o.substr(0, eq));
            extraOptions.push_back(o.substr(eq + 1));
        }
        else if (a == "--no-connections")
            connections = false;
        else
        {
            fprintf(stderr, "tokbuild: bad argument %s\n", argv[i]);
            usage();
        }
    }
    if (!dll)
    {
        fprintf(stderr, "tokbuild: --dll is required (your own ContentProcessing3D.dll)\n");
        usage();
    }

    if (command == "build")
    {
        if (!out || (geomPaths.empty() && !solidsPath))
        {
            usage();
        }
        // Parse first: input errors should not wait for the DLL.
        std::vector<cDumpMesh> meshes(geomPaths.size());
        for (size_t i = 0; i < geomPaths.size(); ++i)
        {
            readGeom(geomPaths[i], meshes[i]);
        }
        cDumpSolids solids;
        if (solidsPath)
        {
            readSolids(solidsPath, solids);
        }

        iPathEngine* pathEngine;
        iContentProcessing3D* contentProcessing;
        loadDll(dll, pathEngine, contentProcessing);

        // buildfromphysx.cpp, BuildAndSaveGround.
        const tSigned32 extrudeHeight = 128;
        const tSigned32 maxStepHeight = 145;
        tSigned32 maxSlope = static_cast<tSigned32>(tanf(1.553343f) * 100.f);
        std::vector<const char*> options;
        options.push_back("method");
        options.push_back("bsp");
        options.push_back("excludeDownwardFacingFromGroundResult");
        options.push_back("true");
        for (size_t i = 0; i < extraOptions.size(); ++i)
        {
            options.push_back(extraOptions[i].c_str());
        }
        options.push_back(0);

        std::vector<const iFaceVertexMesh*> meshPointers;
        tSigned32 inputFaces = 0, inputVertices = 0;
        for (size_t i = 0; i < meshes.size(); ++i)
        {
            meshPointers.push_back(&meshes[i]);
            inputFaces += meshes[i].faces();
            inputVertices += meshes[i].vertices();
        }
        printf("input: %d mesh(es), %ld vertices, %ld faces, %d convex solids\n", static_cast<int>(meshes.size()),
               static_cast<long>(inputVertices), static_cast<long>(inputFaces),
               static_cast<int>(solids.solids.size()));
        DWORD start = GetTickCount();
        iMesh* mesh = contentProcessing->buildGround(meshPointers.empty() ? 0 : &meshPointers[0],
                                                     static_cast<tSigned32>(meshPointers.size()), &solids,
                                                     extrudeHeight, maxStepHeight, maxSlope, &options[0], 0);
        if (!mesh)
        {
            fail("buildGround returned no mesh%s", "");
        }
        if (connections)
        {
            cBlockConnections blockConnections;   // blockconnections.cpp
            blockConnections.build(mesh);
        }
        cBufferOutputStream os;
        mesh->saveGround("tok", false, os);
        writeFile(out, os.bytes);
        printf("built %ld faces, %ld end points, %ld off-mesh connections, %u bytes, %lu ms -> %s\n",
               static_cast<long>(mesh->getNumberOf3DFaces()), static_cast<long>(mesh->getNumberOfEndPoints()),
               static_cast<long>(mesh->getNumberOfOffMeshConnections()), static_cast<unsigned>(os.bytes.size()),
               static_cast<unsigned long>(GetTickCount() - start), out);
        if (facesPath)
        {
            writeFaces(mesh, facesPath);
        }
        mesh->destroy();
        return 0;
    }
    if (command == "reconnect")
    {
        if (!in || !out)
        {
            usage();
        }
        std::vector<char> bytes = readFile(in);
        iPathEngine* pathEngine;
        iContentProcessing3D* contentProcessing;
        loadDll(dll, pathEngine, contentProcessing);
        iMesh* mesh = pathEngine->loadMeshFromBuffer("tok", bytes.empty() ? "" : &bytes[0],
                                                     static_cast<tUnsigned32>(bytes.size()), 0);
        if (!mesh)
        {
            fail("cannot load %s as a .tok", in);
        }
        mesh->clearOffMeshConnectionsAndEndPoints();
        cBlockConnections blockConnections;
        blockConnections.build(mesh);
        cBufferOutputStream os;
        mesh->saveGround("tok", false, os);
        writeFile(out, os.bytes);
        printf("%ld faces, %ld off-mesh connections, %u bytes (%s input)\n",
               static_cast<long>(mesh->getNumberOf3DFaces()), static_cast<long>(mesh->getNumberOfOffMeshConnections()),
               static_cast<unsigned>(os.bytes.size()), os.bytes == bytes ? "same as" : "differs from");
        if (facesPath)
        {
            writeFaces(mesh, facesPath);
        }
        mesh->destroy();
        return 0;
    }
    if (command == "info")
    {
        iPathEngine* pathEngine;
        iContentProcessing3D* contentProcessing;
        loadDll(dll, pathEngine, contentProcessing);
        tSigned32 major, minor, internal;
        pathEngine->getReleaseNumbers(major, minor, internal);
        printf("iPathEngine %ld.%ld, release %ld.%ld.%ld; content processing interface %ld.%ld\n",
               static_cast<long>(pathEngine->getInterfaceMajorVersion()),
               static_cast<long>(pathEngine->getInterfaceMinorVersion()), static_cast<long>(major),
               static_cast<long>(minor), static_cast<long>(internal),
               static_cast<long>(contentProcessing->getInterfaceMajorVersion()),
               static_cast<long>(contentProcessing->getInterfaceMinorVersion()));
        if (in)
        {
            std::vector<char> bytes = readFile(in);
            iMesh* mesh = pathEngine->loadMeshFromBuffer("tok", bytes.empty() ? "" : &bytes[0],
                                                         static_cast<tUnsigned32>(bytes.size()), 0);
            if (!mesh)
            {
                fail("cannot load %s as a .tok", in);
            }
            printf("%s: %ld faces, %ld end points, %ld off-mesh connections\n", in,
                   static_cast<long>(mesh->getNumberOf3DFaces()), static_cast<long>(mesh->getNumberOfEndPoints()),
                   static_cast<long>(mesh->getNumberOfOffMeshConnections()));
            mesh->destroy();
        }
        return 0;
    }
    usage();
    return 1;
}
