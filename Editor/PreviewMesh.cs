using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// A renderer as the Edit preview draws it: a copy of its source mesh (submeshes, UVs, colours) with world positions, normals and
    /// tangents, so it draws with the identity matrix; its blend shapes are stripped: DrawMesh would not apply them, and they only cost memory. The vendor's own tangent deltas are not shown.
    /// </summary>
    internal sealed class PreviewMesh : IDisposable
    {
        public readonly Renderer Renderer;
        public readonly Mesh Mesh;
        private readonly int[] triangles;
        private readonly SkinSpace skin;
        private readonly Vector3[] rest;
        private readonly Vector3[] restNormals;
        /// <summary>The snapshot's normals in world space, set when an update has no normal offset.</summary>
        private readonly Vector3[] worldRestNormals;

        /// <summary>The world positions last set.</summary>
        public Vector3[] Positions { get; private set; }

        /// <param name="rest">The snapshot's world positions.</param>
        /// <param name="weights">The snapshot's weights of the source's shapes, by index: the normals start from them.</param>
        public PreviewMesh(Renderer renderer, Mesh source, SkinSpace skin, Vector3[] rest, float[] weights)
        {
            Renderer = renderer;
            this.skin = skin;
            this.rest = rest;
            Mesh = Object.Instantiate(source);
            Mesh.name = source.name + " (preview)";
            Mesh.hideFlags = HideFlags.HideAndDontSave;
            Mesh.ClearBlendShapes();
            triangles = source.triangles;
            restNormals = source.normals;
            if (restNormals.Length == rest.Length)
                for (var s = 0; s < source.blendShapeCount; s++)
                    if (weights[s] != 0f) restNormals = DeltaOps.Add(restNormals, ShapeMath.DeltaAt(source, s, weights[s], normals: true), 1f);
            Mesh.MarkDynamic();
            if (restNormals.Length == rest.Length)
            {
                worldRestNormals = new Vector3[rest.Length];
                for (var v = 0; v < rest.Length; v++) worldRestNormals[v] = skin.WorldVector(v, restNormals[v]).normalized;
            }
            var tangents = source.tangents;
            if (tangents.Length == rest.Length)
            {
                // no shape moves the tangents in the preview: the skinned snapshot tangents, set once
                var world = new Vector4[tangents.Length];
                for (var v = 0; v < tangents.Length; v++)
                {
                    var t = skin.WorldVector(v, (Vector3)tangents[v]).normalized;
                    world[v] = new Vector4(t.x, t.y, t.z, tangents[v].w);
                }
                Mesh.SetTangents(world);
            }
            Update(null, null);
        }

        /// <summary>World positions at rest + offset; the snapshot's normals (plus normalOffset, mesh space) through each vertex's skin matrix.</summary>
        public void Update(Vector3[] offset, Vector3[] normalOffset)
        {
            var n = rest.Length;
            var positions = new Vector3[n];
            for (var v = 0; v < n; v++) positions[v] = offset == null ? rest[v] : rest[v] + offset[v];
            Mesh.SetVertices(positions);
            if (worldRestNormals != null)
            {
                if (normalOffset == null) Mesh.SetNormals(worldRestNormals);
                else
                {
                    var normals = new Vector3[n];
                    for (var v = 0; v < n; v++) normals[v] = skin.WorldVector(v, restNormals[v] + normalOffset[v]).normalized;
                    Mesh.SetNormals(normals);
                }
            }
            Mesh.RecalculateBounds();
            Positions = positions;
        }

        /// <summary>
        /// Queues the mesh for the camera's next render with the renderer's materials, property blocks, layer, shadows and light probes;
        /// the draw calls made. block is scratch space: Unity copies it on every call.
        /// </summary>
        public int Draw(Camera camera, MaterialPropertyBlock block)
        {
            if (Mesh.subMeshCount == 0) return 0;
            var materials = Renderer.sharedMaterials;
            // this overload refuses a proxy volume without the volume itself; the plain probes are the closest look
            var probes = Renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume ? LightProbeUsage.BlendProbes : Renderer.lightProbeUsage;
            var count = 0;
            for (var i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                // material slots past the submeshes draw the last submesh again, as Unity does
                var submesh = Mathf.Min(i, Mesh.subMeshCount - 1);
                MaterialPropertyBlock properties = null;
                if (Renderer.HasPropertyBlock())
                {
                    Renderer.GetPropertyBlock(block, i);
                    if (block.isEmpty) Renderer.GetPropertyBlock(block);
                    properties = block;
                }
                Graphics.DrawMesh(Mesh, Matrix4x4.identity, materials[i], Renderer.gameObject.layer, camera, submesh, properties,
                    Renderer.shadowCastingMode, Renderer.receiveShadows, Renderer.probeAnchor, probes, null);
                count++;
            }
            return count;
        }

        /// <summary>The distance along the ray to the nearest triangle, either face, of the mesh as last set; false on a miss.</summary>
        public bool Raycast(Ray ray, out float distance)
        {
            distance = float.MaxValue;
            if (!Mesh.bounds.IntersectRay(ray)) return false;
            var p = Positions;
            for (var i = 0; i + 2 < triangles.Length; i += 3)
                if (Intersect(ray, p[triangles[i]], p[triangles[i + 1]], p[triangles[i + 2]], out var t) && t < distance) distance = t;
            return distance < float.MaxValue;
        }

        /// <summary>Möller–Trumbore, both faces.</summary>
        private static bool Intersect(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(ray.direction, e2);
            var det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            var inverse = 1f / det;
            var s = ray.origin - a;
            var u = Vector3.Dot(s, p) * inverse;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            var w = Vector3.Dot(ray.direction, q) * inverse;
            if (w < 0f || u + w > 1f) return false;
            t = Vector3.Dot(e2, q) * inverse;
            return t > 0f;
        }

        public void Dispose() => Object.DestroyImmediate(Mesh);
    }
}
