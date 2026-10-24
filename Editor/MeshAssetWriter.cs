using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// Saves a mesh as a .asset. An existing Mesh asset at the path is overwritten in place, so its GUID and every renderer
    /// reference to it survive; anything else at the path is refused rather than replaced. The overwrite saves the whole
    /// file, sub-assets included.
    /// </summary>
    internal static class MeshAssetWriter
    {
        /// <param name="mesh">On an overwrite it is destroyed once its data has been copied; use the returned mesh.</param>
        /// <returns>The asset instance: <paramref name="mesh"/> itself on a first write, the pre-existing asset on an overwrite.</returns>
        public static Mesh WriteOrOverwrite(Mesh mesh, string assetPath)
        {
            var mainType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (mainType != null && mainType != typeof(Mesh))
                throw new InvalidOperationException($"{assetPath} holds a {mainType.Name}, not a Mesh; refusing to overwrite it.");

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, assetPath);
                return mesh;
            }

            var users = RenderersUsing(existing);
            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
            Object.DestroyImmediate(mesh);
            return Reload(existing, assetPath, users);
        }

        /// <summary>Loaded-scene renderers drawing the mesh, each with its blend-shape weights by name (the old shape layout).</summary>
        private static List<(SkinnedMeshRenderer renderer, Dictionary<string, float> weights)> RenderersUsing(Mesh mesh)
        {
            var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
            return Object.FindObjectsOfType<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh == mesh)
                .Select(r => (r, names.Select((n, i) => (n, w: r.GetBlendShapeWeight(i))).Where(x => x.w != 0f).ToDictionary(x => x.n, x => x.w)))
                .ToList();
        }

        /// <summary>
        /// A renderer that already drew the mesh keeps the GPU copy of the old blend-shape deltas after CopySerialized: shapes
        /// whose frames moved stop deforming on screen while BakeMesh still moves them. Only a native reload refreshes that, and
        /// only while no renderer holds the mesh, so the renderers let go, the asset is unloaded and reloaded, and they get it back
        /// with their weights matched by name.
        /// </summary>
        private static Mesh Reload(Mesh existing, string assetPath, List<(SkinnedMeshRenderer renderer, Dictionary<string, float> weights)> users)
        {
            foreach (var (renderer, _) in users) renderer.sharedMesh = null;
            Resources.UnloadAsset(existing);
            var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            foreach (var (renderer, weights) in users)
            {
                renderer.sharedMesh = reloaded;
                // detaching does not clear a renderer's weights; the old indices would land on whatever shape sits there now
                for (var i = 0; i < reloaded.blendShapeCount; i++) renderer.SetBlendShapeWeight(i, 0f);
                foreach (var (name, weight) in weights)
                {
                    var index = reloaded.GetBlendShapeIndex(name);
                    if (index >= 0) renderer.SetBlendShapeWeight(index, weight);
                }
            }
            return reloaded;
        }
    }
}
