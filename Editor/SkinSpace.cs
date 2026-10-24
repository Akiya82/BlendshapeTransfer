using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// Per-vertex skin matrices in the current pose (Σ wᵢ · boneᵢ.localToWorld · bindposeᵢ) and conversions between mesh and
    /// world space through them. The renderer's own transform plays no part unless the mesh has no bone weights.
    /// </summary>
    internal sealed class SkinSpace
    {
        public readonly Matrix4x4[] Skin;
        public readonly Matrix4x4[] InverseLinear;
        public readonly bool[] Singular;
        public readonly int SingularCount;

        private SkinSpace(Matrix4x4[] skin)
        {
            Skin = skin;
            InverseLinear = new Matrix4x4[skin.Length];
            Singular = new bool[skin.Length];
            for (var v = 0; v < skin.Length; v++)
            {
                var linear = skin[v];
                linear.m03 = linear.m13 = linear.m23 = 0f;
                var scale = (((Vector3)linear.GetColumn(0)).magnitude + ((Vector3)linear.GetColumn(1)).magnitude + ((Vector3)linear.GetColumn(2)).magnitude) / 3f;
                // Relative to the matrix's own scale, so a centimetre mesh under a 0.01 armature is not "singular".
                if (scale < 1e-12f || Mathf.Abs(linear.determinant) < 1e-6f * scale * scale * scale)
                {
                    Singular[v] = true;
                    SingularCount++;
                    continue;
                }
                InverseLinear[v] = linear.inverse;
            }
        }

        public static SkinSpace Of(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var count = mesh.vertexCount;
            var skin = new Matrix4x4[count];
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var boneMatrices = new Matrix4x4[bindposes.Length];
            var present = new bool[bindposes.Length];
            for (var b = 0; b < bindposes.Length; b++)
            {
                if (b >= bones.Length || bones[b] == null) continue;
                boneMatrices[b] = bones[b].localToWorldMatrix * bindposes[b];
                present[b] = true;
            }
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var fallback = renderer.transform.localToWorldMatrix;
            var offset = 0;
            for (var v = 0; v < count; v++)
            {
                var n = perVertex.Length == count ? perVertex[v] : 0;
                var m = new Matrix4x4();
                var total = 0f;
                for (var k = 0; k < n; k++)
                {
                    var w = weights[offset + k];
                    if (w.boneIndex >= present.Length || !present[w.boneIndex]) continue;
                    Accumulate(ref m, boneMatrices[w.boneIndex], w.weight);
                    total += w.weight;
                }
                offset += n;
                if (total <= 0f)
                {
                    skin[v] = fallback;
                    continue;
                }
                Scale(ref m, 1f / total);
                m.m33 = 1f;
                skin[v] = m;
            }
            return new SkinSpace(skin);
        }

        public Vector3 WorldPoint(int v, Vector3 p) => Skin[v].MultiplyPoint3x4(p);
        public Vector3 WorldVector(int v, Vector3 d) => Skin[v].MultiplyVector(d);
        /// <summary>Zero for a vertex with singular skinning.</summary>
        public Vector3 MeshVector(int v, Vector3 d) => Singular[v] ? Vector3.zero : InverseLinear[v].MultiplyVector(d);

        public Vector3[] WorldPoints(Vector3[] meshPoints)
        {
            var result = new Vector3[meshPoints.Length];
            for (var v = 0; v < result.Length; v++) result[v] = WorldPoint(v, meshPoints[v]);
            return result;
        }

        public Vector3[] WorldVectors(Vector3[] meshVectors)
        {
            var result = new Vector3[meshVectors.Length];
            for (var v = 0; v < result.Length; v++) result[v] = WorldVector(v, meshVectors[v]);
            return result;
        }

        public Vector3[] MeshVectors(Vector3[] worldVectors)
        {
            var result = new Vector3[worldVectors.Length];
            for (var v = 0; v < result.Length; v++) result[v] = MeshVector(v, worldVectors[v]);
            return result;
        }

        /// <summary>True when the matrices map one of the unit points more than tolerance apart (position, rotation and scale alike).</summary>
        public static bool Differs(Matrix4x4 a, Matrix4x4 b, float tolerance)
        {
            var sqr = tolerance * tolerance;
            return (a.MultiplyPoint3x4(Vector3.zero) - b.MultiplyPoint3x4(Vector3.zero)).sqrMagnitude > sqr
                   || (a.MultiplyPoint3x4(Vector3.right) - b.MultiplyPoint3x4(Vector3.right)).sqrMagnitude > sqr
                   || (a.MultiplyPoint3x4(Vector3.up) - b.MultiplyPoint3x4(Vector3.up)).sqrMagnitude > sqr
                   || (a.MultiplyPoint3x4(Vector3.forward) - b.MultiplyPoint3x4(Vector3.forward)).sqrMagnitude > sqr;
        }

        private static void Accumulate(ref Matrix4x4 sum, Matrix4x4 m, float w)
        {
            sum.m00 += m.m00 * w; sum.m01 += m.m01 * w; sum.m02 += m.m02 * w; sum.m03 += m.m03 * w;
            sum.m10 += m.m10 * w; sum.m11 += m.m11 * w; sum.m12 += m.m12 * w; sum.m13 += m.m13 * w;
            sum.m20 += m.m20 * w; sum.m21 += m.m21 * w; sum.m22 += m.m22 * w; sum.m23 += m.m23 * w;
        }

        private static void Scale(ref Matrix4x4 m, float k)
        {
            m.m00 *= k; m.m01 *= k; m.m02 *= k; m.m03 *= k;
            m.m10 *= k; m.m11 *= k; m.m12 *= k; m.m13 *= k;
            m.m20 *= k; m.m21 *= k; m.m22 *= k; m.m23 *= k;
        }
    }
}
