using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using Sledge.Formats.Map.Formats;
using Sledge.Formats.Map.Objects;

namespace RayQuakeMap;

public class MeshModel : IDisposable
{
    public Model Model;
    public Texture2D Texture;
    public IntPtr VerticesPtr;
    public IntPtr IndicesPtr;
    public IntPtr NormalsPtr;
    public IntPtr TexturesPtr;

    private bool disposed;

    public MeshModel(Model model, Texture2D texture, nint verticesPtr, nint indicesPtr, nint normalsPtr, nint texturesPtr)
    {
        Model = model;
        Texture = texture;
        VerticesPtr = verticesPtr;
        IndicesPtr = indicesPtr;
        NormalsPtr = normalsPtr;
        TexturesPtr = texturesPtr;
    }

    ~MeshModel()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (!disposed)
        {
            Marshal.FreeHGlobal(VerticesPtr);
            Marshal.FreeHGlobal(IndicesPtr);
            Marshal.FreeHGlobal(NormalsPtr);
            Marshal.FreeHGlobal(TexturesPtr);

            // This prevents a crash
            unsafe
            {
                Model.Meshes[0].Vertices = null;
                Model.Meshes[0].Indices = null;
                Model.Meshes[0].Normals = null;
                Model.Meshes[0].TexCoords = null;
            }

            Raylib.UnloadModel(Model);
            Raylib.UnloadTexture(Texture);

            disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

public class Program
{
    const float worldScale = 1.0f / 39.3701f; // Converting inches to meters (1 quake unit is approx. 1 inch)
    static bool showWireframe = false;
    static bool reloadRequested = false;

    public static void Main(string[] args)
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        Raylib.InitWindow(1280, 720, "RayQuakeMap demo");
        Raylib.SetTargetFPS(60);
        Raylib.DisableCursor();

        var mapModels = LoadMapAsModels("Content/Maps/1.map");

        Camera3D camera = new Camera3D()
        {
            Position = new Vector3(0, 8, 8),
            Target = Vector3.Zero,
            Up = Vector3.UnitZ, // Quake is Z-up
            FovY = 74,
            Projection = CameraProjection.Perspective
        };

        while (!Raylib.WindowShouldClose())
        {
            Raylib.UpdateCamera(ref camera, CameraMode.Free);

            if (Raylib.IsKeyPressed(KeyboardKey.R))
            {
                reloadRequested = true;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.T))
            {
                showWireframe = !showWireframe;
            }

            if (reloadRequested)
            {
                foreach (var models in mapModels)
                {
                    models.Dispose();
                }

                mapModels.Clear();

                mapModels = LoadMapAsModels("Content/level1.map");
                reloadRequested = false;
            }

            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.SkyBlue);

            Raylib.BeginMode3D(camera);

            foreach (var models in mapModels.ToArray())
            {
                if (!showWireframe)
                {
                    Rlgl.EnableBackfaceCulling();
                    Raylib.DrawModelEx(models.Model, Vector3.Zero, Vector3.UnitZ, 0, Vector3.One, Color.White);
                }
                else
                {
                    Rlgl.DisableBackfaceCulling();
                    Raylib.DrawModelWiresEx(models.Model, Vector3.Zero, Vector3.UnitZ, 0, Vector3.One, Color.Black);
                }
            }

            Raylib.EndMode3D();

            Raylib.DrawFPS(10, 10);
            Raylib.DrawText($"Meshes: {mapModels.Count}", 10, 30, 20, Color.Magenta);
            Raylib.DrawText($"Position: {camera.Position:F6}", 10, 50, 20, Color.Red);
            Raylib.DrawText($"TruePosition: {camera.Position * 39.3701f:F6}", 10, 70, 20, Color.Orange);

            Raylib.EndDrawing();
        }

        foreach (var models in mapModels.ToArray())
        {
            models.Dispose();
        }

        Raylib.CloseWindow();
    }

    public static List<MeshModel> LoadMapAsModels(string path)
    {
        MapFile map;
        var mapModels = new List<MeshModel>();
        QuakeMapFormat format = new QuakeMapFormat();

        if (File.Exists(path))
        {
            map = format.ReadFromFile(path);
        }
        else
        {
            return mapModels;
        }

        // Grouping faces by texture name
        var groups = new Dictionary<string, List<Face>>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in map.Worldspawn.Children)
        {
            if (child is Solid solid)
            {
                solid.ComputeVertices();

                foreach (var face in solid.Faces)
                {
                    if (!groups.TryGetValue(face.TextureName ?? "NULL", out var list))
                    {
                        list = new List<Face>();
                        groups[face.TextureName ?? "NULL"] = list;
                    }

                    list.Add(face);
                }
            }
        }

        foreach (var kvp in groups)
        {
            var faces = kvp.Value;
            string textureName = "Content/Textures/" + kvp.Key + ".png";
            Texture2D texture = LoadTextureOrFallback(textureName);

            var vertices = new List<float>();
            var indices = new List<ushort>();
            var normals = new List<float>();
            var texCoords = new List<float>();

            int vertexStartIndex = 0;

            foreach (var face in faces)
            {
                // Getting rotated UVs
                Matrix4x4 rotation = Matrix4x4.CreateFromAxisAngle(GetRotationAxis(face.Plane.Normal), face.Rotation * Raylib.DEG2RAD);
                Vector3 rotatedU = Vector3.Transform(face.UAxis / face.XScale, rotation);
                Vector3 rotatedV = Vector3.Transform(face.VAxis / face.YScale, rotation);

                int faceVerticesCount = face.Vertices.Count;

                if (faceVerticesCount < 3) { continue; }

                var faceVertices = face.Vertices;
                Vector3 facePlaneNormal = face.Plane.Normal;

                // Getting normals from vertex positions
                Vector3 normal = Vector3.Zero;

                for (int i = 0; i < faceVertices.Count; i++)
                {
                    var v0 = new Vector3(faceVertices[i].X, faceVertices[i].Y, faceVertices[i].Z);
                    var v1 = new Vector3(
                        faceVertices[(i + 1) % faceVertices.Count].X,
                        faceVertices[(i + 1) % faceVertices.Count].Y,
                        faceVertices[(i + 1) % faceVertices.Count].Z
                    );

                    normal += Vector3.Cross(v0, v1);
                }

                normal = Vector3.Normalize(normal);

                // Reverse if normal is opposite
                bool reverse = Vector3.Dot(normal, facePlaneNormal) < 0;

                for (int i = 0; i < faceVerticesCount - 2; i++)
                {
                    if (!reverse)
                    {
                        indices.Add((ushort)(vertexStartIndex + 0));
                        indices.Add((ushort)(vertexStartIndex + i + 1));
                        indices.Add((ushort)(vertexStartIndex + i + 2));
                    }
                    else
                    {
                        indices.Add((ushort)(vertexStartIndex + 0));
                        indices.Add((ushort)(vertexStartIndex + i + 2));
                        indices.Add((ushort)(vertexStartIndex + i + 1));
                    }
                }

                for (int i = 0; i < faceVerticesCount; i++)
                {
                    var faceVertices2 = face.Vertices[i]; // Very original variable name
                    var vertex = new Vector3(faceVertices2.X, faceVertices2.Y, faceVertices2.Z) * worldScale;

                    // UV calculation
                    Vector2 uv = new Vector2()
                    {
                        X = faceVertices2.X * rotatedU.X + faceVertices2.Y * rotatedU.Y + faceVertices2.Z * rotatedU.Z,
                        Y = faceVertices2.X * rotatedV.X + faceVertices2.Y * rotatedV.Y + faceVertices2.Z * rotatedV.Z,
                    };

                    uv.X = (uv.X + face.XShift) / texture.Width;
                    uv.Y = (uv.Y + face.YShift) / texture.Height;

                    var facePlaneNormal2 = face.Plane.Normal; // Also very original, lmao

                    vertices.Add(vertex.X);
                    vertices.Add(vertex.Y);
                    vertices.Add(vertex.Z);

                    normals.Add(facePlaneNormal2.X);
                    normals.Add(facePlaneNormal2.Y);
                    normals.Add(facePlaneNormal2.Z);

                    texCoords.Add(uv.X);
                    texCoords.Add(uv.Y);

                    vertexStartIndex++;
                }
            }

            if (vertices.Count == 0 || indices.Count == 0) { continue; }

            Model model;
            ushort[] indexArray = indices.ToArray(); // Raylib really wants ushorts for indices

            IntPtr verticesPtr = Marshal.AllocHGlobal(sizeof(float) * vertices.Count);
            IntPtr indicesPtr = Marshal.AllocHGlobal(sizeof(ushort) * indexArray.Length);
            IntPtr normalsPtr = Marshal.AllocHGlobal(sizeof(float) * normals.Count);
            IntPtr texCoordsPtr = Marshal.AllocHGlobal(sizeof(float) * texCoords.Count);

            Marshal.Copy(vertices.ToArray(), 0, verticesPtr, vertices.Count);
            Marshal.Copy(normals.ToArray(), 0, normalsPtr, normals.Count);
            Marshal.Copy(texCoords.ToArray(), 0, texCoordsPtr, texCoords.Count);

            unsafe
            {
                // Prevents the triangles from imploding
                fixed (ushort* src = indexArray)
                {
                    Buffer.MemoryCopy(src, (void*)indicesPtr, sizeof(ushort) * indexArray.Length, sizeof(ushort) * indexArray.Length);
                }

                Raylib_cs.Mesh mesh = new Raylib_cs.Mesh();
                mesh.VertexCount = vertices.Count / 3;
                mesh.TriangleCount = indices.Count / 3;
                mesh.Vertices = (float*)verticesPtr;
                mesh.Indices = (ushort*)indicesPtr;
                mesh.Normals = (float*)normalsPtr;
                mesh.TexCoords = (float*)texCoordsPtr;

                Raylib.UploadMesh(ref mesh, false);

                model = Raylib.LoadModelFromMesh(mesh);
                model.Materials[0].Maps[(int)MaterialMapIndex.Albedo].Texture = texture;

                // Raylib.GenTextureMipmaps(ref model.Materials[0].Maps[(int)MaterialMapIndex.Albedo].Texture);
            }

            mapModels.Add(new MeshModel(model, texture, verticesPtr, indicesPtr, normalsPtr, texCoordsPtr));
        }

        return mapModels;
    }

    public static Texture2D LoadTextureOrFallback(string path)
    {
        Texture2D texture;

        if (File.Exists(path))
        {
            texture = Raylib.LoadTexture(path);

            if (texture.Id == 0)
            {
                Image image = Raylib.GenImageChecked(64, 64, 16, 16, Color.Magenta, Color.Black);
                texture = Raylib.LoadTextureFromImage(image);
                Raylib.UnloadImage(image);
            }
        }
        else
        {
            Image image = Raylib.GenImageChecked(64, 64, 16, 16, Color.Magenta, Color.Black);
            texture = Raylib.LoadTextureFromImage(image);
            Raylib.UnloadImage(image);
        }

        return texture;
    }

    public static Vector3 GetRotationAxis(Vector3 normal)
    {
        var abs = new Vector3(Math.Abs(normal.X), Math.Abs(normal.Y), Math.Abs(normal.Z));

        if (abs.Y > abs.Z)
        {
            return Vector3.UnitY;
        }
        else if (abs.X > abs.Y && abs.X > abs.Z)
        {
            return Vector3.UnitX;
        }
        else
        {
            return Vector3.UnitZ;
        }
    }
}