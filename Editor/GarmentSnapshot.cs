using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>A garment in the snapshot pose, read from its source mesh (never a generated one) with the renderer's bones and current weights.</summary>
    internal sealed class GarmentSnapshot
    {
        public readonly SkinnedMeshRenderer Renderer;
        public readonly Mesh Source;
        public readonly SkinSpace Skin;
        /// <summary>The renderer's weights of the source's shapes (by name) when the snapshot was taken, or the recorded fit weights given, by source shape index.</summary>
        public readonly float[] Weights;
        /// <summary>Source vertices with those weights, in mesh space.</summary>
        public readonly Vector3[] MeshPositions;
        public readonly Vector3[] Positions;
        public readonly Topology Topology;
        public readonly BoneData Bones;
        /// <summary>Body bone index per garment bone, -1 where none matched.</summary>
        public readonly int[] BoneToBody;
        public readonly int[][] BodyBones;
        public readonly List<string> UnmatchedBones = new List<string>();

        public GarmentSnapshot(SkinnedMeshRenderer renderer, Mesh source, BodySurface body, IEnumerable<BaseWeight> fit = null)
        {
            Renderer = renderer;
            Source = source;
            Skin = SkinSpace.Of(renderer, source);
            Weights = ShapeMath.WeightsOf(renderer, source, fit);
            MeshPositions = ShapeMath.PositionsWithWeights(source, name => Weights[source.GetBlendShapeIndex(name)]);
            Positions = Skin.WorldPoints(MeshPositions);
            Topology = new Topology(Positions, source.triangles);
            Bones = new BoneData(source, renderer.bones);
            BoneToBody = BoneMap.Map(renderer.bones, body.Renderer.bones, UnmatchedBones);
            BodyBones = BoneMap.VertexBodyBones(Bones, BoneToBody);
        }
    }
}
