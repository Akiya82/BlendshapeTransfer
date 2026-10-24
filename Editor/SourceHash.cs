using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Identity of a source mesh for hand edits: positions quantized to 1e-5 mesh units (0.01 mm for a metre mesh) plus triangle indices (FNV-1a, 64 bit).</summary>
    internal static class SourceHash
    {
        public static string Of(Mesh mesh)
        {
            var hash = 14695981039346656037UL;

            void Mix(int value)
            {
                unchecked
                {
                    hash ^= (uint)value;
                    hash *= 1099511628211UL;
                }
            }

            foreach (var p in mesh.vertices)
            {
                Mix(Mathf.RoundToInt(p.x * 1e5f));
                Mix(Mathf.RoundToInt(p.y * 1e5f));
                Mix(Mathf.RoundToInt(p.z * 1e5f));
            }
            foreach (var index in mesh.triangles) Mix(index);
            return hash.ToString("x16");
        }
    }
}
