using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// Maps garment bones to body bones by world position, not by name, so Merge Armature and Armature Link garments work
    /// without prefix rules. A garment-only bone (skirt, strap) takes its nearest matched ancestor.
    /// </summary>
    internal static class BoneMap
    {
        public const float MatchDistance = 0.01f;
        private const float Tie = 1e-4f;

        /// <summary>Body bone index per garment bone, -1 where nothing matched; names of unmatched bones go to unmatched.</summary>
        public static int[] Map(Transform[] garmentBones, Transform[] bodyBones, ICollection<string> unmatched)
        {
            var result = new int[garmentBones.Length];
            for (var i = 0; i < garmentBones.Length; i++)
            {
                result[i] = -1;
                for (var t = garmentBones[i]; t != null && result[i] < 0; t = t.parent) result[i] = Match(t, bodyBones);
                if (result[i] < 0 && garmentBones[i] != null) unmatched.Add(garmentBones[i].name);
            }
            return result;
        }

        /// <summary>Body bones under each garment vertex: its significant garment bones mapped, duplicates and misses dropped.</summary>
        public static int[][] VertexBodyBones(BoneData garment, int[] map)
        {
            var result = new int[garment.Significant.Length][];
            var list = new List<int>();
            for (var v = 0; v < result.Length; v++)
            {
                list.Clear();
                foreach (var b in garment.Significant[v])
                {
                    var m = b < map.Length ? map[b] : -1;
                    if (m >= 0 && !list.Contains(m)) list.Add(m);
                }
                result[v] = list.ToArray();
            }
            return result;
        }

        private static int Match(Transform t, Transform[] bodyBones)
        {
            var best = -1;
            var bestDistance = MatchDistance;
            for (var b = 0; b < bodyBones.Length; b++)
            {
                if (bodyBones[b] == null) continue;
                if (bodyBones[b] == t) return b;
                var d = Vector3.Distance(t.position, bodyBones[b].position);
                if (d > MatchDistance) continue;
                var closer = best < 0 || d < bestDistance - Tie;
                var sameButNamed = best >= 0 && Mathf.Abs(d - bestDistance) <= Tie && bodyBones[b].name == t.name && bodyBones[best].name != t.name;
                if (closer || sameButNamed)
                {
                    best = b;
                    bestDistance = d;
                }
            }
            return best;
        }
    }
}
