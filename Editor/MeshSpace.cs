using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Steps 7–8: world deltas into the garment's mesh space, hand edits on top, and normal deltas.</summary>
    internal static class MeshSpace
    {
        public static Vector3[] ToMesh(SkinSpace skin, Vector3[] world) => skin.MeshVectors(world);

        public static Vector3[] ToWorld(SkinSpace skin, Vector3[] mesh) => skin.WorldVectors(mesh);

        /// <summary>final = auto × mask + sculpt. A missing edit, or an array of another length, changes nothing.</summary>
        public static Vector3[] ApplyEdit(Vector3[] auto, ShapeEdit edit)
        {
            var final = (Vector3[])auto.Clone();
            if (edit == null) return final;
            if (edit.mask.Length == final.Length)
                for (var v = 0; v < final.Length; v++) final[v] *= edit.mask[v];
            if (edit.sculpt.Length == final.Length)
                for (var v = 0; v < final.Length; v++) final[v] += edit.sculpt[v];
            return final;
        }

        /// <summary>Recomputed normals of the moved mesh minus recomputed normals at rest, both in mesh space; exactly zero where nothing around a vertex moves.</summary>
        public static Vector3[] NormalDeltas(Topology topology, Vector3[] meshPositions, Vector3[] meshDelta)
        {
            var rest = topology.Normals(meshPositions);
            var moved = topology.Normals(DeltaOps.Add(meshPositions, meshDelta, 1f));
            for (var v = 0; v < rest.Length; v++) moved[v] -= rest[v];
            return moved;
        }
    }
}
