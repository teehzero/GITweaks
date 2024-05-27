using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using Atlassing = System.Collections.Generic.Dictionary<UnityEngine.Component, (int lightmapIndex, UnityEngine.Vector4 lightmapST)>;

namespace GITweaks
{
    [InitializeOnLoad]
    public static class GITweaksPostBakeOperations
    {
        static GITweaksPostBakeOperations()
        {
            Lightmapping.bakeCompleted -= BakeFinished;
            Lightmapping.bakeCompleted += BakeFinished;
        }

        private static void BakeFinished()
        {
            var sharedLODs = Object.FindObjectsByType<GITweaksSharedLOD>(FindObjectsSortMode.None);
            foreach (var sharedLOD in sharedLODs)
            {
                var lods = sharedLOD.GetComponent<LODGroup>().GetLODs();
                if (lods.Length == 0) continue;
                var lod0 = lods[0].renderers.FirstOrDefault(x => x is MeshRenderer) as MeshRenderer;
                if (lod0 == null) continue;

                var mrs = sharedLOD.RenderersToLightmap;
                GITweaksLightingDataAssetEditor.CopyAtlasSettingsToRenderers(Lightmapping.lightingDataAsset, lod0, mrs);
            }

            RepackAtlasses();


            GITweaksLightingDataAssetEditor.RefreshLDA();

            //var lightmapDatas = LightmapSettings.lightmaps;
            //lightmapDatas[0].lightmapColor = GetRWCopy(lightmapDatas[0].lightmapColor, 512, 256);
            //lightmapDatas[0].lightmapDir = GetRWCopy(lightmapDatas[0].lightmapDir, 512, 256);
            //LightmapSettings.lightmaps = lightmapDatas;
        }

        class AtlassingCache
        {
            public Dictionary<Component, int> AtlasIndices;
            public List<Vector2Int> AtlasSizes;
            public List<HashSet<Component>> RenderersPerAtlas;
            public Dictionary<Component, Rect> PixelRectsFractional;
            public Dictionary<Component, RectInt> PixelRects;
            public Dictionary<Component, Vector2Int> RendererScale; // TODO: This isn't needed

            private AtlassingCache() { }

            public AtlassingCache(Atlassing atlassing, List<Vector2Int> atlasSizes)
            {
                AtlasIndices = atlassing.ToDictionary(x => x.Key, x => x.Value.lightmapIndex);
                AtlasSizes = atlasSizes;
                PixelRectsFractional = new Dictionary<Component, Rect>();
                PixelRects = new Dictionary<Component, RectInt>();
                RendererScale = new Dictionary<Component, Vector2Int>();

                RenderersPerAtlas = new List<HashSet<Component>>();
                for (int i = 0; i < atlasSizes.Count; i++)
                {
                    RenderersPerAtlas.Add(new HashSet<Component>());
                }

                foreach ((Component c, (int idx, Vector4 st)) in atlassing)
                {
                    var stRect = new Rect(st.z, st.w, st.x, st.y);
                    var pixelRect = stRect;

                    if (c is MeshRenderer mr)
                    {
                        pixelRect = GITweaksUtils.STRectToPixelRect(mr, stRect);
                    }

                    var fractionalPixelRect = new Rect(atlasSizes[idx] * pixelRect.position, atlasSizes[idx] * pixelRect.size);
                    PixelRectsFractional[c] = fractionalPixelRect;
                    PixelRects[c] = fractionalPixelRect.ToRectInt();
                    RendererScale[c] = Vector2Int.one;
                    RenderersPerAtlas[idx].Add(c);
                }
            }

            public AtlassingCache Copy()
            {
                var copy = new AtlassingCache();
                copy.AtlasIndices = new Dictionary<Component, int>(AtlasIndices);
                copy.AtlasSizes = new List<Vector2Int>(AtlasSizes);
                copy.RenderersPerAtlas = RenderersPerAtlas.Select(x => new HashSet<Component>(x)).ToList();
                copy.PixelRectsFractional = new Dictionary<Component, Rect>(PixelRectsFractional);
                copy.PixelRects = new Dictionary<Component, RectInt>(PixelRects);
                copy.RendererScale = new Dictionary<Component, Vector2Int>(RendererScale);
                return copy;
            }
        }

        private static float GetCoveragePercentage(AtlassingCache atlassing, int lightmapIndex)
        {
            Vector2Int lightmapSize = atlassing.AtlasSizes[lightmapIndex];
            int lightmapArea = lightmapSize.x * lightmapSize.y;

            float pixelRectsArea = atlassing.AtlasIndices
                .Where(x => x.Value == lightmapIndex)
                .Select(x => atlassing.PixelRects[x.Key].size)
                .Select(x => x.x * x.y)
                .Sum();

            return pixelRectsArea / lightmapArea;
        }

        private static bool Pack<K>(
            int width,
            int height,
            int padding,
            IEnumerable<(K key, RectInt rect)> rects,
            out HashSet<(K key, RectInt rect)> packedRects,
            out HashSet<(K key, RectInt rect)> remainder)
        {
            GITweaksTexturePacker packer = new GITweaksTexturePacker(width, height, padding, 0);
            packedRects = new HashSet<(K key, RectInt size)>();
            remainder = rects.ToHashSet();
            var sorted = rects.OrderByDescending(x => x.rect.width * x.rect.height);
            foreach (var instance in sorted)
            {
                if (!packer.Pack(instance.rect.width, instance.rect.height, out var frame))
                    return false;

                packedRects.Add((instance.key, frame));
                remainder.Remove(instance);
            }
            return true;
        }

        // To include bilinear neighborhood
        private static Rect DilateRect(Rect rect)
        {
            rect.position -= Vector2.one*2;
            rect.size += Vector2.one * 2*2;
            return rect;
        }

        private static Vector2Int DilatePosition(Vector2Int pos)
        {
            pos -= Vector2Int.one*2;
            return pos;
        }

        private static void CopyImporterSettingsAndReimport(Texture2D template, string dstPath)
        {
            var srcImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(template));
            var dstImporter = AssetImporter.GetAtPath(dstPath);

            var srcImporterObj = new SerializedObject(srcImporter);
            var dstImporterObj = new SerializedObject(dstImporter);

            var srcIter = srcImporterObj.GetIterator();

            while (srcIter.Next(true))
            {
                dstImporterObj.CopyFromSerializedProperty(srcIter);
            }

            dstImporterObj.ApplyModifiedProperties();
            dstImporter.SaveAndReimport();
        }

        private static void RepackAtlasses()
        {
            // Settings
            float minCoveragePercent = 0.8f;
            int minLightmapSize = 64;
            bool allowNonSquareLightmaps = false;
            int padding = Mathf.Max(3, Lightmapping.lightingSettings.lightmapPadding);

            var lda = Lightmapping.lightingDataAsset;
            var initialLightmaps = LightmapSettings.lightmaps;
            var initialAtlassing = GITweaksLightingDataAssetEditor.GetAtlassing(lda);

            List<Vector2Int> atlasSizes = new List<Vector2Int>();
            for (int i = 0; i < initialLightmaps.Length; i++)
                atlasSizes.Add(new Vector2Int(initialLightmaps[i].lightmapColor.width, initialLightmaps[i].lightmapColor.height));
            var initialAtlassingCache = new AtlassingCache(initialAtlassing, atlasSizes);
            var atlassingCache = initialAtlassingCache.Copy();

            List<int> atlassesToRepack = new List<int>();
            for (int i = 0; i < atlassingCache.AtlasSizes.Count; i++)
            {
                // TODO: Just shrinking (think shadowmask)
                // TODO: Halving

                float coverage = GetCoveragePercentage(atlassingCache, i);
                if (coverage < minCoveragePercent)
                {
                    // Get quadrant size, check it is big enough
                    var splitLightmapSize = atlassingCache.AtlasSizes[i] / 2;
                    if (splitLightmapSize.x < minLightmapSize || splitLightmapSize.y < minLightmapSize)
                        continue;

                    // Get the renderers to repack
                    var renderers = atlassingCache.RenderersPerAtlas[i];
                    var rectsToPack = renderers.Select(x => (x, atlassingCache.PixelRects[x]));

                    // Try to repack into 3 quadrants. If we require 4, there is no point.
                    Pack(splitLightmapSize.x, splitLightmapSize.y, padding, rectsToPack, out var packedRectsA, out var remainderA);
                    Pack(splitLightmapSize.x, splitLightmapSize.y, padding, remainderA, out var packedRectsB, out var remainderB);
                    if (!Pack(splitLightmapSize.x, splitLightmapSize.y, padding, remainderB, out var packedRectsC, out var remainderC))
                        continue;

                    // If we succeeded, we need to update the cache with the new atlases
                    int splitCount = (packedRectsA.Any() ? 1 : 0) + (packedRectsB.Any() ? 1 : 0) + (packedRectsC.Any() ? 1 : 0);

                    atlassingCache.AtlasSizes.RemoveAt(i);
                    atlassingCache.AtlasSizes.InsertRange(i, Enumerable.Range(0, splitCount).Select(_ => splitLightmapSize));

                    HashSet<Component>[] newRenderersPerAtlas =
                    {
                        packedRectsA.Select(x => x.key).ToHashSet(),
                        packedRectsB.Select(x => x.key).ToHashSet(),
                        packedRectsC.Select(x => x.key).ToHashSet(),
                    };
                    atlassingCache.RenderersPerAtlas.RemoveAt(i);
                    atlassingCache.RenderersPerAtlas.InsertRange(i, newRenderersPerAtlas.Take(splitCount));

                    foreach (var renderer in packedRectsA)
                    {
                        atlassingCache.AtlasIndices[renderer.key] = i + 0;
                        atlassingCache.PixelRects[renderer.key] = renderer.rect;
                        atlassingCache.PixelRectsFractional[renderer.key] = renderer.rect.ToRect();
                        atlassingCache.RendererScale[renderer.key] *= 2;
                    }
                    foreach (var renderer in packedRectsB)
                    {
                        atlassingCache.AtlasIndices[renderer.key] = i + 1;
                        atlassingCache.PixelRects[renderer.key] = renderer.rect;
                        atlassingCache.PixelRectsFractional[renderer.key] = renderer.rect.ToRect();
                        atlassingCache.RendererScale[renderer.key] *= 2;
                    }
                    foreach (var renderer in packedRectsC)
                    {
                        atlassingCache.AtlasIndices[renderer.key] = i + 2;
                        atlassingCache.PixelRects[renderer.key] = renderer.rect;
                        atlassingCache.PixelRectsFractional[renderer.key] = renderer.rect.ToRect();
                        atlassingCache.RendererScale[renderer.key] *= 2;
                    }

                    // We just replaced an atlas, now we want to re-visit the results of that
                    i--;
                }
            }

            // Create new lightmap textures to render into
            var newLightmapRTs = new (RenderTexture light, RenderTexture dir, RenderTexture shadow)[atlassingCache.AtlasSizes.Count];
            for (int i = 0; i < newLightmapRTs.Length; i++)
            {
                var size = atlassingCache.AtlasSizes[i];
                newLightmapRTs[i] = (
                    new RenderTexture(new RenderTextureDescriptor(size.x, size.y) { graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, }),
                    new RenderTexture(new RenderTextureDescriptor(size.x, size.y) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm, enableRandomWrite = true, }),
                    new RenderTexture(new RenderTextureDescriptor(size.x, size.y) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm, enableRandomWrite = true, }));
            }

            // Copy renderers over, update their atlassing data
            foreach ((var renderer, int lightmapIndex) in atlassingCache.AtlasIndices)
            {
                var oldAtlasData = initialAtlassing[renderer];
                var newAtlasData = oldAtlasData;

                // Create new atlas data
                var lmSize = atlassingCache.AtlasSizes[lightmapIndex];
                var pixelRectScaled = atlassingCache.PixelRects[renderer];

                Vector2 scale = atlassingCache.RendererScale[renderer];
                newAtlasData.lightmapST = new Vector4(
                    newAtlasData.lightmapST.x * scale.x,
                    newAtlasData.lightmapST.y * scale.y, // TODO: Bad
                    (float)pixelRectScaled.position.x / lmSize.x, // TODO: Why is this off?
                    (float)pixelRectScaled.position.y / lmSize.y);
                newAtlasData.lightmapIndex = lightmapIndex;

                if (renderer is MeshRenderer mr)
                {
                    // TODO: CACHE UV BOUNDS!!
                    GITweaksUtils.OffsetLightmapSTByPixelRectOffset(mr, ref newAtlasData.lightmapST);
                }


                // Copy renderer
                int oldLightmapIndex = oldAtlasData.lightmapIndex;
                Texture2D oldLightmap = initialLightmaps[oldLightmapIndex].lightmapColor;
                RenderTexture newLightmap = newLightmapRTs[lightmapIndex].light;
                Texture2D oldDir = initialLightmaps[oldLightmapIndex].lightmapDir;
                RenderTexture newDir = newLightmapRTs[lightmapIndex].dir;

                bool gammaToLinear = PlayerSettings.colorSpace == ColorSpace.Gamma;
                var oldRect = DilateRect(initialAtlassingCache.PixelRectsFractional[renderer]);
                var newPosition = DilatePosition(atlassingCache.PixelRects[renderer].position);
                GITweaksUtils.CopyFractional(oldLightmap, oldRect, newLightmap, newPosition, gammaToLinear); 
                GITweaksUtils.CopyFractional(oldDir, oldRect, newDir, newPosition, gammaToLinear);

                initialAtlassing[renderer] = newAtlasData;
            }

            // Convert to texture2D and import the new lightmaps
            // TODO: Shadowmask
            var newLightmaps = new LightmapData[newLightmapRTs.Length];
            for (int i = 0; i < newLightmapRTs.Length; i++)
            {
                newLightmaps[i] = new LightmapData();

                var newPair = newLightmapRTs[i];
                var newColor = GITweaksUtils.RenderTextureToTexture2D(newPair.light);
                var newDir = GITweaksUtils.RenderTextureToTexture2D(newPair.dir);

                string scenePath = Path.ChangeExtension(SceneManager.GetActiveScene().path, null);

                string lmPath = Path.Combine(scenePath, $"Lightmap-{i}_comp_light.exr");
                File.WriteAllBytes(lmPath, newColor.EncodeToEXR());
                AssetDatabase.ImportAsset(lmPath, ImportAssetOptions.ForceSynchronousImport);
                CopyImporterSettingsAndReimport(initialLightmaps[0].lightmapColor, lmPath);
                newLightmaps[i].lightmapColor = AssetDatabase.LoadAssetAtPath<Texture2D>(lmPath);

                string dirPath = Path.Combine(scenePath, $"Lightmap-{i}_comp_dir.png");
                File.WriteAllBytes(dirPath, newDir.EncodeToPNG());
                AssetDatabase.ImportAsset(dirPath, ImportAssetOptions.ForceSynchronousImport);
                CopyImporterSettingsAndReimport(initialLightmaps[0].lightmapDir, dirPath);
                newLightmaps[i].lightmapDir = AssetDatabase.LoadAssetAtPath<Texture2D>(dirPath);
            }

            GITweaksLightingDataAssetEditor.UpdateAtlassing(lda, initialAtlassing);
            GITweaksLightingDataAssetEditor.UpdateLightmaps(lda, newLightmaps);
        }



    }
}