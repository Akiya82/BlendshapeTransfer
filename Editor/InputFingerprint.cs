using System;
using Unity.Collections;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>64-bit FNV-1a over 32-bit words; floats as raw bits with −0 as +0.</summary>
    internal struct Fnv
    {
        public ulong Value;

        public static Fnv New() => new Fnv { Value = 14695981039346656037UL };

        public void Add(int word)
        {
            unchecked
            {
                Value ^= (uint)word;
                Value *= 1099511628211UL;
            }
        }

        public void Add(float value) => Add(BitConverter.SingleToInt32Bits(value == 0f ? 0f : value));
        public void Add(bool value) => Add(value ? 1 : 0);
        public void Add(ulong value)
        {
            Add((int)value);
            Add((int)(value >> 32));
        }

        public void Add(Vector3 v)
        {
            Add(v.x);
            Add(v.y);
            Add(v.z);
        }

        public void Add(string s)
        {
            if (s == null)
            {
                Add(-1);
                return;
            }
            Add(s.Length);
            foreach (var c in s) Add(c);
        }

        public void Add(NativeArray<byte> bytes)
        {
            var words = bytes.Length / 4;
            if (words > 0)
                foreach (var w in bytes.GetSubArray(0, words * 4).Reinterpret<int>(1)) Add(w);
            for (var i = words * 4; i < bytes.Length; i++) Add(bytes[i]);
        }
    }

    /// <summary>The parts of a garment's input fingerprint that are hashed once per read (see TransferSession.Fingerprints).</summary>
    internal static class InputFingerprint
    {
        /// <summary>Raised whenever a code change changes transfer results, so older outputs become stale; tests replace it.</summary>
        internal static int PipelineVersion = 4;

        public static string Hex(ulong fingerprint) => fingerprint.ToString("x16");

        /// <summary>
        /// Everything an output copies from a mesh and everything the pipeline reads from it: the vertex layout and every vertex stream's
        /// bytes, the index format, submeshes and indices, bone weights, bindposes, bounds, and every blend shape frame (name, weight, deltas).
        /// </summary>
        public static ulong MeshContent(Mesh mesh)
        {
            var h = Fnv.New();
            foreach (var a in mesh.GetVertexAttributes())
            {
                h.Add((int)a.attribute);
                h.Add((int)a.format);
                h.Add(a.dimension);
                h.Add(a.stream);
            }
            using (var all = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                var data = all[0];
                h.Add(data.vertexCount);
                for (var s = 0; s < data.vertexBufferCount; s++) h.Add(data.GetVertexData<byte>(s));
                h.Add((int)data.indexFormat);
                h.Add(data.subMeshCount);
                for (var i = 0; i < data.subMeshCount; i++)
                {
                    var d = data.GetSubMesh(i);
                    h.Add(d.indexStart);
                    h.Add(d.indexCount);
                    h.Add(d.baseVertex);
                    h.Add((int)d.topology);
                }
                h.Add(data.GetIndexData<byte>());
            }
            foreach (var count in mesh.GetBonesPerVertex()) h.Add(count);
            foreach (var w in mesh.GetAllBoneWeights())
            {
                h.Add(w.boneIndex);
                h.Add(w.weight);
            }
            foreach (var m in mesh.bindposes)
                for (var i = 0; i < 16; i++) h.Add(m[i]);
            h.Add(mesh.bounds.center);
            h.Add(mesh.bounds.extents);
            var n = mesh.vertexCount;
            var dv = new Vector3[n];
            var dn = new Vector3[n];
            var dt = new Vector3[n];
            for (var s = 0; s < mesh.blendShapeCount; s++)
            {
                h.Add(mesh.GetBlendShapeName(s));
                for (var f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                {
                    h.Add(mesh.GetBlendShapeFrameWeight(s, f));
                    mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    foreach (var v in dv) h.Add(v);
                    foreach (var v in dn) h.Add(v);
                    foreach (var v in dt) h.Add(v);
                }
            }
            return h.Value;
        }

        /// <summary>
        /// The snapshot's pose and weights: for each bone slot, and for the renderer, the local position, rotation and scale of it and
        /// of every parent up to the avatar root; the root's scale; the shape weights by name. Local values are exact and do not
        /// change when the whole avatar moves or turns; scaling it changes the root's scale (distances are in world metres). The root's
        /// scale is the product of its own and its parents' local scales: lossyScale is read back from the world matrix and picks up
        /// rounding once the avatar turns. The product assumes uniform scale above the avatar: a turned parent with non-uniform scale
        /// skews the avatar, which it does not see.
        /// </summary>
        public static ulong SnapshotState(Transform avatar, SkinnedMeshRenderer renderer, Mesh mesh, float[] weights)
        {
            var h = Fnv.New();
            var scale = Vector3.one;
            for (var t = avatar; t != null; t = t.parent) scale = Vector3.Scale(scale, t.localScale);
            h.Add(scale);
            void Chain(Transform t)
            {
                for (; t != null && t != avatar; t = t.parent)
                {
                    h.Add(t.localPosition);
                    var r = t.localRotation;
                    h.Add(r.x);
                    h.Add(r.y);
                    h.Add(r.z);
                    h.Add(r.w);
                    h.Add(t.localScale);
                }
                h.Add(-4);
            }
            foreach (var bone in renderer.bones)
            {
                if (bone == null) h.Add(-1);
                else Chain(bone);
            }
            Chain(renderer.transform);
            for (var s = 0; s < mesh.blendShapeCount; s++)
            {
                h.Add(mesh.GetBlendShapeName(s));
                h.Add(weights[s]);
            }
            return h.Value;
        }

        /// <summary>A frame's hand edits (none hashes as -1).</summary>
        public static void Add(ref Fnv h, ShapeEdit edit)
        {
            if (edit == null)
            {
                h.Add(-1);
                return;
            }
            h.Add(edit.mask.Length);
            foreach (var m in edit.mask) h.Add(m);
            h.Add(edit.sculpt.Length);
            foreach (var v in edit.sculpt) h.Add(v);
        }
    }
}
