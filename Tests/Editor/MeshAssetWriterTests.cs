using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class MeshAssetWriterTests
    {
        private const string Temp = "Assets/BlendshapeTransferTests_tmp";
        private const string AssetPath = Temp + "/WriterTest.asset";
        // its scene is the active one while it lives, so the test renderer never lands in the owner's scene
        private Rig rig;

        [SetUp]
        public void SetUp()
        {
            rig = new Rig();
            if (!AssetDatabase.IsValidFolder(Temp)) AssetDatabase.CreateFolder("Assets", "BlendshapeTransferTests_tmp");
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            AssetDatabase.DeleteAsset(Temp);
        }

        [Test]
        public void WriteOrOverwrite_PathHoldsAnotherAssetType_Refuses()
        {
            AssetDatabase.CreateAsset(new AnimationClip(), AssetPath);
            var mesh = Polygon(3);
            try
            {
                Assert.Throws<InvalidOperationException>(() => MeshAssetWriter.WriteOrOverwrite(mesh, AssetPath));
                Assert.That(AssetDatabase.GetMainAssetTypeAtPath(AssetPath), Is.EqualTo(typeof(AnimationClip)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// A renderer that already drew the asset keeps the GPU copy of the old blend-shape deltas after an in-place overwrite,
        /// so the writer reloads the asset for every renderer using it; this pins what a test can see: the renderer still holds
        /// the asset and its weights follow the shape names across the new layout.
        /// </summary>
        [Test]
        public void WriteOrOverwrite_RenderersUsingTheAsset_KeepItAndTheirWeightsByName()
        {
            var go = new GameObject("WriterTestRenderer");
            try
            {
                var first = MeshAssetWriter.WriteOrOverwrite(WithShapes(Polygon(4), "A", "B"), AssetPath);
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = first;
                renderer.SetBlendShapeWeight(first.GetBlendShapeIndex("B"), 70f);

                var second = MeshAssetWriter.WriteOrOverwrite(WithShapes(Polygon(4), "New", "A", "B"), AssetPath);

                Assert.That(renderer.sharedMesh == null, Is.False, "renderer lost the mesh");
                Assert.That(AssetDatabase.GetAssetPath(renderer.sharedMesh), Is.EqualTo(AssetPath));
                Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(3));
                Assert.That(renderer.GetBlendShapeWeight(second.GetBlendShapeIndex("B")), Is.EqualTo(70f), "weight follows the shape name, not its old index");
                Assert.That(renderer.GetBlendShapeWeight(second.GetBlendShapeIndex("A")), Is.EqualTo(0f));
                Assert.That(renderer.GetBlendShapeWeight(second.GetBlendShapeIndex("New")), Is.EqualTo(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static Mesh WithShapes(Mesh mesh, params string[] names)
        {
            var n = mesh.vertexCount;
            var lift = Enumerable.Repeat(new Vector3(0f, 0f, 0.1f), n).ToArray();
            foreach (var name in names) mesh.AddBlendShapeFrame(name, 100f, lift, new Vector3[n], new Vector3[n]);
            return mesh;
        }

        private static Mesh Polygon(int corners)
        {
            var vertices = new Vector3[corners];
            for (var i = 0; i < corners; i++)
            {
                var angle = 2f * Mathf.PI * i / corners;
                vertices[i] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
            }
            var triangles = new int[(corners - 2) * 3];
            for (var i = 0; i < corners - 2; i++)
            {
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = i + 2;
            }
            return new Mesh { name = $"Polygon{corners}", vertices = vertices, triangles = triangles };
        }
    }
}
