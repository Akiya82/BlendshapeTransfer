using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>The body in the snapshot pose with every shape at its current weight: world triangles, normals, areas, bones, and shape deltas in world space.</summary>
    internal sealed class BodySurface
    {
        public readonly SkinnedMeshRenderer Renderer;
        public readonly Mesh Mesh;
        /// <summary>The shape weights the snapshot shows, by shape index: the fit weights given, else the renderer's when the snapshot was taken.</summary>
        public readonly float[] Weights;
        public readonly SkinSpace Skin;
        public readonly Topology Topology;
        public readonly Vector3[] Positions;
        public readonly Vector3[] Normals;
        public readonly float[] Areas;
        public readonly BoneData Bones;
        public readonly SurfaceQuery Query;
        private readonly Dictionary<(int, int), Vector3[]> deltas = new Dictionary<(int, int), Vector3[]>();

        public BodySurface(SkinnedMeshRenderer renderer, IEnumerable<BaseWeight> fit = null)
        {
            Renderer = renderer;
            Mesh = renderer.sharedMesh;
            Weights = ShapeMath.WeightsOf(renderer, Mesh, fit);
            Skin = SkinSpace.Of(renderer, Mesh);
            Positions = Skin.WorldPoints(ShapeMath.PositionsWithWeights(Mesh, name => Weights[Mesh.GetBlendShapeIndex(name)]));
            Topology = new Topology(Positions, Mesh.triangles);
            Normals = Topology.Normals(Positions);
            Areas = Topology.VertexAreas(Positions);
            Bones = new BoneData(Mesh, renderer.bones);
            // a coarse cell: Blend's Near(blendRadius) runs up to 5× slower on a density cell, far fabric in Correspondence 1.5×
            // (ponytail: Closest pays for it on dense skin, 40 µs a query on a dense avatar body against 33 µs on a density cell; a second, density
            // grid for Closest alone if Open is too slow)
            Query = new SurfaceQuery(Positions, Normals, Topology, rejectBoundary: false, cell: 0.02f);
        }

        /// <summary>World-space delta of one frame of a body shape (cached).</summary>
        public Vector3[] WorldDelta(int shape, int frame)
        {
            if (deltas.TryGetValue((shape, frame), out var cached)) return cached;
            var d = new Vector3[Mesh.vertexCount];
            WorldDelta(shape, frame, d);
            return deltas[(shape, frame)] = d;
        }

        /// <summary>World-space delta of one frame of a body shape into output (vertex count long), not cached.</summary>
        public void WorldDelta(int shape, int frame, Vector3[] output)
        {
            Mesh.GetBlendShapeFrameVertices(shape, frame, output, null, null);
            for (var v = 0; v < output.Length; v++) output[v] = Skin.WorldVector(v, output[v]);
        }

        /// <summary>A per-vertex body field (a delta) at a surface point: barycentric blend of its triangle's corners.</summary>
        public Vector3 At(in SurfacePoint point, Vector3[] field)
        {
            var t = Topology.Triangles;
            var i = point.Triangle * 3;
            return field[t[i]] * point.Bary.x + field[t[i + 1]] * point.Bary.y + field[t[i + 2]] * point.Bary.z;
        }
    }
}
