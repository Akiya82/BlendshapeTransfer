using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Skin weights per vertex, the bones weighted at least MinWeight, and each renderer bone's nearest ancestor among the others.</summary>
    internal sealed class BoneData
    {
        public const float MinWeight = 0.1f;
        public readonly (int Bone, float Weight)[][] All;
        public readonly int[][] Significant;
        public readonly int[] Parents;

        public BoneData(Mesh mesh, Transform[] bones)
        {
            var count = mesh.vertexCount;
            All = new (int, float)[count][];
            Significant = new int[count][];
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var offset = 0;
            var significant = new List<int>();
            for (var v = 0; v < count; v++)
            {
                var n = perVertex.Length == count ? perVertex[v] : 0;
                All[v] = new (int, float)[n];
                significant.Clear();
                for (var k = 0; k < n; k++)
                {
                    var w = weights[offset + k];
                    All[v][k] = (w.boneIndex, w.weight);
                    if (w.weight >= MinWeight) significant.Add(w.boneIndex);
                }
                Significant[v] = significant.ToArray();
                offset += n;
            }

            Parents = new int[bones.Length];
            var index = new Dictionary<Transform, int>();
            for (var b = 0; b < bones.Length; b++)
                if (bones[b] != null && !index.ContainsKey(bones[b])) index[bones[b]] = b;
            for (var b = 0; b < bones.Length; b++)
            {
                Parents[b] = -1;
                for (var t = bones[b] == null ? null : bones[b].parent; t != null; t = t.parent)
                    if (index.TryGetValue(t, out var parent)) { Parents[b] = parent; break; }
            }
        }

        public bool Related(int a, int b) =>
            a >= 0 && b >= 0 && (a == b || (a < Parents.Length && Parents[a] == b) || (b < Parents.Length && Parents[b] == a));

        /// <summary>True when one of the vertex's significant bones is related to one of bones; an empty bones array filters nothing.</summary>
        public bool RelatedToAny(int vertex, int[] bones)
        {
            if (bones.Length == 0) return true;
            foreach (var a in Significant[vertex])
            foreach (var b in bones)
                if (Related(a, b)) return true;
            return false;
        }

        /// <summary>The bone with the largest weight, or -1 for an unweighted vertex.</summary>
        public int Dominant(int vertex)
        {
            var best = -1;
            var weight = 0f;
            foreach (var (bone, w) in All[vertex])
                if (w > weight) { best = bone; weight = w; }
            return best;
        }
    }
}
