using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Akiya82.BlendshapeTransfer.Tests
{
    /// <summary>
    /// An avatar (root with a descriptor, a small humanoid bone chain) in a temporary additive scene made active while it lives,
    /// so test objects never land in the owner's scene. Dispose closes the scene and restores the previous active scene.
    /// </summary>
    internal sealed class Rig : IDisposable
    {
        public readonly Scene Scene;
        public readonly GameObject Root;
        public readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>();
        private readonly Scene previous;

        public Rig(string name = "BT Test Avatar")
        {
            previous = SceneManager.GetActiveScene();
            Scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(Scene);
                Root = new GameObject(name);
                Root.AddComponent<VRCAvatarDescriptor>();
                var armature = new GameObject("Armature").transform;
                armature.SetParent(Root.transform, false);
                AddBone("Hips", armature, new Vector3(0f, 0.9f, 0f));
                AddBone("Spine", Bones["Hips"], new Vector3(0f, 0.1f, 0f));
                AddBone("Chest", Bones["Spine"], new Vector3(0f, 0.15f, 0f));
                AddBone("Shoulder.L", Bones["Chest"], new Vector3(0.05f, 0.15f, 0f));
                AddBone("UpperArm.L", Bones["Shoulder.L"], new Vector3(0.1f, 0f, 0f));
                AddBone("UpperLeg.L", Bones["Hips"], new Vector3(0.1f, -0.05f, 0f));
                AddBone("UpperLeg.R", Bones["Hips"], new Vector3(-0.1f, -0.05f, 0f));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Transform AddBone(string name, Transform parent, Vector3 localPosition)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent, false);
            bone.localPosition = localPosition;
            return Bones[name] = bone;
        }

        /// <summary>
        /// A skinned renderer directly under the root. weightOf gives each vertex's (bone, weight) pairs; bindposes are taken in the
        /// current pose, so at bind time world space equals the renderer's space. extraBones are added to the bone list unweighted.
        /// </summary>
        public SkinnedMeshRenderer AddRenderer(string name, Mesh mesh, Func<int, Vector3, (string Bone, float Weight)[]> weightOf, params string[] extraBones)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root.transform, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            var names = new List<string>();
            var vertices = mesh.vertices;
            var perVertex = new byte[vertices.Length];
            var all = new List<BoneWeight1>();
            for (var v = 0; v < vertices.Length; v++)
            {
                var weights = weightOf(v, vertices[v]).OrderByDescending(w => w.Weight).ToArray();
                perVertex[v] = (byte)weights.Length;
                foreach (var (bone, weight) in weights)
                {
                    var index = names.IndexOf(bone);
                    if (index < 0) { names.Add(bone); index = names.Count - 1; }
                    all.Add(new BoneWeight1 { boneIndex = index, weight = weight });
                }
            }
            names.AddRange(extraBones.Where(b => !names.Contains(b)));
            var bones = names.Select(n => Bones[n]).ToArray();
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * go.transform.localToWorldMatrix).ToArray();
            using (var counts = new NativeArray<byte>(perVertex, Allocator.Temp))
            using (var weightArray = new NativeArray<BoneWeight1>(all.ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(counts, weightArray);
            renderer.bones = bones;
            renderer.rootBone = Bones["Hips"];
            renderer.sharedMesh = mesh;
            return renderer;
        }

        public SkinnedMeshRenderer AddRenderer(string name, Mesh mesh, string bone, params string[] extraBones) =>
            AddRenderer(name, mesh, (_, __) => new[] { (bone, 1f) }, extraBones);

        public void Dispose()
        {
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(Scene, true);
        }
    }

    internal static class MeshFactory
    {
        public static float[] Range(float from, float to, int count) =>
            Enumerable.Range(0, count).Select(i => Mathf.Lerp(from, to, count == 1 ? 0f : i / (float)(count - 1))).ToArray();

        /// <summary>A grid in the XY plane at height z facing +Z (or -Z when flip), columns at xs, rows at ys.</summary>
        public static Mesh Grid(float[] xs, float[] ys, float z, bool flip = false)
        {
            var vertices = new List<Vector3>();
            foreach (var y in ys)
            foreach (var x in xs)
                vertices.Add(new Vector3(x, y, z));
            var triangles = new List<int>();
            var w = xs.Length;
            for (var j = 0; j < ys.Length - 1; j++)
            for (var i = 0; i < xs.Length - 1; i++)
            {
                int a = j * w + i, b = a + 1, c = a + w, d = c + 1;
                if (flip) triangles.AddRange(new[] { a, c, b, b, c, d });
                else triangles.AddRange(new[] { a, b, c, b, d, c });
            }
            return Build(vertices, triangles);
        }

        /// <summary>A tube around the vertical axis through (cx, cz) facing outward, open at both ends.</summary>
        public static Mesh Cylinder(float cx, float cz, float radius, float y0, float y1, int segments, int rings)
        {
            var vertices = new List<Vector3>();
            for (var r = 0; r < rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var angle = 2f * Mathf.PI * s / segments;
                vertices.Add(new Vector3(cx + radius * Mathf.Cos(angle), Mathf.Lerp(y0, y1, r / (float)(rings - 1)), cz + radius * Mathf.Sin(angle)));
            }
            var triangles = new List<int>();
            for (var r = 0; r < rings - 1; r++)
            for (var s = 0; s < segments; s++)
            {
                int a = r * segments + s, b = r * segments + (s + 1) % segments, c = a + segments, d = b + segments;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
            return Build(vertices, triangles);
        }

        /// <summary>An axis-aligned closed box facing outward (8 shared corners).</summary>
        public static Mesh Box(Vector3 center, float size)
        {
            var h = size / 2f;
            var vertices = new List<Vector3>();
            for (var i = 0; i < 8; i++) vertices.Add(center + new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h));
            var triangles = new List<int>
            {
                0, 2, 1, 1, 2, 3, // -z
                4, 5, 6, 5, 7, 6, // +z
                0, 1, 4, 1, 5, 4, // -y
                2, 6, 3, 3, 6, 7, // +y
                0, 4, 2, 2, 4, 6, // -x
                1, 3, 5, 3, 7, 5, // +x
            };
            return Build(vertices, triangles);
        }

        /// <summary>Appends a copy of every vertex and triangle with the winding reversed (a double-sided garment).</summary>
        public static Mesh DoubleSided(Mesh mesh)
        {
            var vertices = mesh.vertices.ToList();
            var n = vertices.Count;
            var triangles = mesh.triangles.ToList();
            vertices.AddRange(mesh.vertices);
            var front = mesh.triangles;
            for (var i = 0; i < front.Length; i += 3) triangles.AddRange(new[] { front[i] + n, front[i + 2] + n, front[i + 1] + n });
            return Build(vertices, triangles);
        }

        /// <summary>Combines meshes into one, vertices in order.</summary>
        public static Mesh Merge(params Mesh[] meshes)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var m in meshes)
            {
                var offset = vertices.Count;
                vertices.AddRange(m.vertices);
                triangles.AddRange(m.triangles.Select(t => t + offset));
            }
            return Build(vertices, triangles);
        }

        public static Mesh Build(List<Vector3> vertices, List<int> triangles)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static void AddShape(Mesh mesh, string name, Func<Vector3, Vector3> delta, float weight = 100f) =>
            mesh.AddBlendShapeFrame(name, weight, mesh.vertices.Select(delta).ToArray(), null, null);

        /// <summary>A compact bump: amplitude · (1 − r²/R²)² inside radius R of (x, y) = (0, 0), along +Z.</summary>
        public static Func<Vector3, Vector3> Bump(float amplitude, float radius) => p =>
        {
            var q = (p.x * p.x + p.y * p.y) / (radius * radius);
            return q >= 1f ? Vector3.zero : new Vector3(0f, 0f, amplitude * (1f - q) * (1f - q));
        };
    }
}
