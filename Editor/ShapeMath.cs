using System;
using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    internal static class ShapeMath
    {
        public static float[] FrameWeights(Mesh mesh, int shape)
        {
            var weights = new float[mesh.GetBlendShapeFrameCount(shape)];
            for (var f = 0; f < weights.Length; f++) weights[f] = mesh.GetBlendShapeFrameWeight(shape, f);
            return weights;
        }

        /// <summary>Position deltas (or normal deltas) of every frame of a shape, in mesh space.</summary>
        public static Vector3[][] FrameDeltas(Mesh mesh, int shape, bool normals = false)
        {
            var frames = new Vector3[mesh.GetBlendShapeFrameCount(shape)][];
            var positions = normals ? new Vector3[mesh.vertexCount] : null;
            for (var f = 0; f < frames.Length; f++)
            {
                frames[f] = new Vector3[mesh.vertexCount];
                if (normals) mesh.GetBlendShapeFrameVertices(shape, f, positions, frames[f], null);
                else mesh.GetBlendShapeFrameVertices(shape, f, frames[f], null, null);
            }
            return frames;
        }

        /// <summary>A shape's position (or normal) deltas at a weight, as Unity interpolates its frames.</summary>
        public static Vector3[] DeltaAt(Mesh mesh, int shape, float weight, bool normals = false)
        {
            var result = new Vector3[mesh.vertexCount];
            DeltaAt(FrameWeights(mesh, shape), FrameDeltas(mesh, shape, normals), weight, result);
            return result;
        }

        /// <summary>Unity's frame interpolation: from zero to the first frame, linear between frames, the last segment extended past the last frame.</summary>
        public static void DeltaAt(float[] weights, Vector3[][] frames, float weight, Vector3[] output)
        {
            if (weights.Length == 0 || weight == 0f) { Array.Clear(output, 0, output.Length); return; }
            if (weights.Length == 1 || weight <= weights[0])
            {
                var t = weight / weights[0];
                for (var i = 0; i < output.Length; i++) output[i] = frames[0][i] * t;
                return;
            }
            var k = 0;
            while (k < weights.Length - 2 && weight > weights[k + 1]) k++;
            var s = (weight - weights[k]) / (weights[k + 1] - weights[k]);
            for (var i = 0; i < output.Length; i++) output[i] = Vector3.LerpUnclamped(frames[k][i], frames[k + 1][i], s);
        }

        /// <summary>Mesh-space vertex positions with every shape at the weight weightOf gives for its name.</summary>
        public static Vector3[] PositionsWithWeights(Mesh mesh, Func<string, float> weightOf)
        {
            var positions = mesh.vertices;
            var scratch = new Vector3[positions.Length];
            for (var s = 0; s < mesh.blendShapeCount; s++)
            {
                var weight = weightOf(mesh.GetBlendShapeName(s));
                if (weight == 0f) continue;
                DeltaAt(FrameWeights(mesh, s), FrameDeltas(mesh, s), weight, scratch);
                for (var i = 0; i < positions.Length; i++) positions[i] += scratch[i];
            }
            return positions;
        }

        /// <summary>The renderer's current weight of each shape of mesh, by name (0 where the renderer's mesh has no such shape); a recorded weight replaces it.</summary>
        public static float[] WeightsOf(SkinnedMeshRenderer renderer, Mesh mesh, IEnumerable<BaseWeight> recorded = null)
        {
            var shown = renderer.sharedMesh;
            var weights = new float[mesh.blendShapeCount];
            for (var s = 0; s < weights.Length; s++)
            {
                var index = shown == null ? -1 : shown.GetBlendShapeIndex(mesh.GetBlendShapeName(s));
                weights[s] = index < 0 ? 0f : renderer.GetBlendShapeWeight(index);
            }
            if (recorded != null)
                foreach (var r in recorded)
                {
                    var index = mesh.GetBlendShapeIndex(r.name);
                    if (index >= 0) weights[index] = r.weight;
                }
            return weights;
        }
    }
}
