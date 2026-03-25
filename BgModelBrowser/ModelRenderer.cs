using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Lumina;
using Lumina.Data;
using Lumina.Models.Models;

namespace BgModelBrowser;

public record struct MeshGeomData(
    Vector3[] Positions,
    Vector3[] Normals,
    Vector2[] UVs,
    int[] Indices);

public record struct TexturePixels(byte[] BgraData, int Width, int Height);

public record struct ModelLoadResult(MeshGeomData Geometry, TexturePixels? Texture);

public static class ModelRenderer
{
    /// <summary>
    /// Load model geometry + resolve and decode diffuse texture.
    /// Call from a background thread.
    /// </summary>
    public static ModelLoadResult? LoadModel(GameData gameData, string modelPath)
    {
        try
        {
            var model = new Lumina.Models.Models.Model(gameData, modelPath);

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();

            foreach (var mesh in model.Meshes)
            {
                var baseVertex = positions.Count;
                foreach (var v in mesh.Vertices)
                {
                    var pos4 = v.Position ?? Vector4.Zero;
                    var nrm = v.Normal ?? Vector3.UnitY;
                    positions.Add(new Vector3(pos4.X, pos4.Y, pos4.Z));
                    normals.Add(nrm);

                    // UV coordinates
                    var uv4 = v.UV ?? Vector4.Zero;
                    uvs.Add(new Vector2(uv4.X, uv4.Y));
                }

                foreach (var idx in mesh.Indices)
                    indices.Add(baseVertex + idx);
            }

            if (positions.Count < 3 || indices.Count < 3) return null;

            // Validate: remove NaN/Infinity, clamp indices
            var posArr = positions.ToArray();
            var nrmArr = normals.ToArray();
            var uvArr = uvs.ToArray();

            for (int i = 0; i < posArr.Length; i++)
            {
                if (!IsFinite(posArr[i])) posArr[i] = Vector3.Zero;
                if (!IsFinite(nrmArr[i])) nrmArr[i] = Vector3.UnitY;
                if (!IsFinite(uvArr[i])) uvArr[i] = Vector2.Zero;
            }

            // Validate indices are in range
            var validIndices = new List<int>();
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                var i0 = indices[i]; var i1 = indices[i + 1]; var i2 = indices[i + 2];
                if (i0 >= 0 && i0 < posArr.Length &&
                    i1 >= 0 && i1 < posArr.Length &&
                    i2 >= 0 && i2 < posArr.Length)
                {
                    validIndices.Add(i0);
                    validIndices.Add(i1);
                    validIndices.Add(i2);
                }
            }

            if (validIndices.Count < 3) return null;

            // Limit to 100K vertices to avoid overwhelming WPF renderer
            if (posArr.Length > 100_000) return null;

            var geometry = new MeshGeomData(posArr, nrmArr, uvArr, validIndices.ToArray());

            // Extract material path strings from Lumina Material objects
            var materialPaths = model.Materials?
                .Select(m => m.ResolvedPath ?? m.MaterialPath ?? "")
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray() ?? Array.Empty<string>();

            // Resolve diffuse texture
            var texture = ResolveDiffuseTexture(gameData, modelPath, materialPaths);

            return new ModelLoadResult(geometry, texture);
        }
        catch
        {
            return null;
        }
    }

    private static TexturePixels? ResolveDiffuseTexture(
        GameData gameData, string modelPath, string[] materials)
    {
        // Approach 1: Naming convention — bgparts/name.mdl → texture/name_d.tex
        var texPath = TryConventionPath(gameData, modelPath);
        if (texPath != null) return LoadAndDecodeTexture(gameData, texPath);

        // Approach 2: Scan each material file for _d.tex references
        if (materials != null)
        {
            foreach (var mtrlRef in materials)
            {
                var fullMtrlPath = ResolveMaterialPath(mtrlRef, modelPath);
                if (fullMtrlPath == null || !gameData.FileExists(fullMtrlPath)) continue;

                try
                {
                    var mtrlFile = gameData.GetFile<FileResource>(fullMtrlPath);
                    if (mtrlFile?.Data == null) continue;

                    var texPaths = ScanForGamePaths(mtrlFile.Data, ".tex");
                    var diffuse = texPaths.FirstOrDefault(p =>
                        p.Contains("_d.tex", StringComparison.OrdinalIgnoreCase));
                    diffuse ??= texPaths.FirstOrDefault();

                    if (diffuse != null && gameData.FileExists(diffuse))
                    {
                        var result = LoadAndDecodeTexture(gameData, diffuse);
                        if (result != null) return result;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    private static string? TryConventionPath(GameData gameData, string modelPath)
    {
        if (modelPath.Contains("/bgparts/"))
        {
            var tex = modelPath.Replace("/bgparts/", "/texture/").Replace(".mdl", "_d.tex");
            if (gameData.FileExists(tex)) return tex;
        }
        return null;
    }

    private static string? ResolveMaterialPath(string mtrlRef, string modelPath)
    {
        if (string.IsNullOrEmpty(mtrlRef)) return null;

        // Already a full game path
        if (mtrlRef.StartsWith("bg/") || mtrlRef.StartsWith("bgcommon/"))
            return mtrlRef;

        // Relative path like /mt_name.mtrl — resolve from model's parent directories
        var modelDir = modelPath[..modelPath.LastIndexOf('/')];

        if (mtrlRef.StartsWith('/'))
        {
            // Try: zone_root + mtrlRef
            var zoneRoot = modelDir;
            if (zoneRoot.Contains("/bgparts"))
                zoneRoot = zoneRoot[..zoneRoot.IndexOf("/bgparts")];

            return zoneRoot + mtrlRef;
        }

        // Plain filename
        return modelDir + "/" + mtrlRef;
    }

    private static TexturePixels? LoadAndDecodeTexture(GameData gameData, string texPath)
    {
        try
        {
            var file = gameData.GetFile<FileResource>(texPath);
            if (file?.Data == null) return null;

            var pixels = TexDecoder.DecodeTexFile(file.Data, out var w, out var h);
            if (pixels == null || w <= 0 || h <= 0) return null;

            return new TexturePixels(pixels, w, h);
        }
        catch
        {
            return null;
        }
    }

    internal static List<string> ScanForGamePaths(byte[] data, string extension)
    {
        var results = new List<string>();
        for (int i = 0; i < data.Length - 8; i++)
        {
            // Match bg/, bgcommon/, or vfx/ prefixes
            var isBg = data[i] == (byte)'b' && data[i + 1] == (byte)'g'
                       && (data[i + 2] == (byte)'/' || data[i + 2] == (byte)'c');
            var isVfx = i + 3 < data.Length
                        && data[i] == (byte)'v' && data[i + 1] == (byte)'f'
                        && data[i + 2] == (byte)'x' && data[i + 3] == (byte)'/';
            if (!isBg && !isVfx) continue;

            var end = i;
            while (end < data.Length && data[end] != 0 && data[end] >= 0x20 && data[end] <= 0x7E)
                end++;

            var len = end - i;
            if (len < 8 || len > 512 || end >= data.Length || data[end] != 0) continue;

            var path = Encoding.ASCII.GetString(data, i, len);
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                results.Add(path);
        }
        return results;
    }

    public static BitmapSource? LoadVfxPreview(GameData gameData, string avfxPath, int size = 128)
    {
        try
        {
            var file = gameData.GetFile<FileResource>(avfxPath);
            if (file?.Data == null) return null;

            // Scan AVFX binary for .atex texture paths
            var texPaths = ScanForGamePaths(file.Data, ".atex");
            if (texPaths.Count == 0) return null;

            // Try each texture path until one decodes
            foreach (var texPath in texPaths)
            {
                if (!gameData.FileExists(texPath)) continue;

                var texFile = gameData.GetFile<FileResource>(texPath);
                if (texFile?.Data == null) continue;

                var pixels = TexDecoder.DecodeTexFile(texFile.Data, out var w, out var h);
                if (pixels == null || w <= 0 || h <= 0) continue;

                // Create BitmapSource from decoded texture
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                bmp.Freeze();

                // Scale to thumbnail size
                var scale = Math.Min((double)size / w, (double)size / h);
                if (scale < 1.0)
                {
                    var scaled = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                    scaled.Freeze();
                    return scaled;
                }

                return bmp;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Render textured model to a WPF BitmapSource.
    /// Must be called from an STA thread (UI thread).
    /// </summary>
    public static BitmapSource? RenderPreview(ModelLoadResult data, int size = 128)
    {
        var geom = data.Geometry;
        if (geom.Positions.Length < 3 || geom.Indices.Length < 3) return null;

        // Pre-validate bounds to avoid feeding degenerate data to the native renderer
        var (camPos, lookDir) = ComputeCamera(geom.Positions);
        if (!double.IsFinite(camPos.X) || !double.IsFinite(camPos.Y) || !double.IsFinite(camPos.Z))
            return null;
        if (lookDir.Length < 0.001)
            return null;

        try
        {
            var meshGeom = new MeshGeometry3D
            {
                Positions = new Point3DCollection(
                    geom.Positions.Select(p => new Point3D(p.X, p.Y, p.Z))),
                TriangleIndices = new Int32Collection(geom.Indices)
            };

            if (geom.Normals.Length == geom.Positions.Length)
            {
                meshGeom.Normals = new Vector3DCollection(
                    geom.Normals.Select(n => new Vector3D(n.X, n.Y, n.Z)));
            }

            if (geom.UVs.Length == geom.Positions.Length)
            {
                meshGeom.TextureCoordinates = new PointCollection(
                    geom.UVs.Select(uv => new Point(uv.X, uv.Y)));
            }

            // Build material — textured if we have a diffuse map, flat gray otherwise
            Material frontMaterial;
            Material backMaterial;

            if (data.Texture is { } tex && tex.Width > 0 && tex.Height > 0)
            {
                var bmp = BitmapSource.Create(
                    tex.Width, tex.Height, 96, 96,
                    PixelFormats.Bgra32, null, tex.BgraData, tex.Width * 4);
                bmp.Freeze();

                var brush = new ImageBrush(bmp)
                {
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1)
                };

                var matGroup = new MaterialGroup();
                matGroup.Children.Add(new DiffuseMaterial(brush));
                matGroup.Children.Add(new SpecularMaterial(
                    new SolidColorBrush(Color.FromRgb(60, 60, 70)), 15));
                frontMaterial = matGroup;
                backMaterial = new DiffuseMaterial(brush);
            }
            else
            {
                var matGroup = new MaterialGroup();
                matGroup.Children.Add(new DiffuseMaterial(
                    new SolidColorBrush(Color.FromRgb(170, 175, 190))));
                matGroup.Children.Add(new SpecularMaterial(
                    new SolidColorBrush(Color.FromRgb(100, 100, 120)), 20));
                frontMaterial = matGroup;
                backMaterial = new DiffuseMaterial(
                    new SolidColorBrush(Color.FromRgb(130, 135, 150)));
            }

            var geoModel = new GeometryModel3D(meshGeom, frontMaterial)
            {
                BackMaterial = backMaterial
            };

            // Scene with lighting
            var group = new Model3DGroup();
            group.Children.Add(geoModel);
            group.Children.Add(new AmbientLight(Color.FromRgb(80, 80, 90)));
            group.Children.Add(new DirectionalLight(
                Color.FromRgb(220, 220, 235), new Vector3D(-1, -1, -2)));
            group.Children.Add(new DirectionalLight(
                Color.FromRgb(60, 60, 80), new Vector3D(1, 0.5, 1)));

            // Camera already computed and validated above
            var viewport = new Viewport3D
            {
                Camera = new PerspectiveCamera(camPos, lookDir, new Vector3D(0, 1, 0), 45),
                Width = size,
                Height = size
            };
            viewport.Children.Add(new ModelVisual3D { Content = group });

            viewport.Measure(new Size(size, size));
            viewport.Arrange(new Rect(0, 0, size, size));
            viewport.UpdateLayout();

            // Render with dark background
            var bgVisual = new DrawingVisual();
            using (var dc = bgVisual.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x1b)),
                    null, new Rect(0, 0, size, size));

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(bgVisual);
            rtb.Render(viewport);
            rtb.Freeze();

            return rtb;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Vector2 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y);

    private static (Point3D Position, Vector3D LookDirection) ComputeCamera(Vector3[] positions)
    {
        var minX = float.MaxValue; var maxX = float.MinValue;
        var minY = float.MaxValue; var maxY = float.MinValue;
        var minZ = float.MaxValue; var maxZ = float.MinValue;

        foreach (var p in positions)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }

        var center = new Point3D(
            (minX + maxX) / 2.0, (minY + maxY) / 2.0, (minZ + maxZ) / 2.0);

        var extent = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        if (extent < 0.001) extent = 1;

        var dist = extent * 1.6;
        var camPos = center + new Vector3D(dist * 0.6, dist * 0.4, dist * 0.7);
        var lookDir = center - camPos;

        return (camPos, lookDir);
    }
}
