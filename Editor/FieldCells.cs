using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>The body coarsened for Field sampling: one sample per (grid cell, dominant bone), area-weighted.</summary>
    internal sealed class FieldCells
    {
        public readonly Vector3[] Positions;
        public readonly float[] Area;
        public readonly int[] Bone;
        private readonly int[][] members;
        private readonly float[][] memberWeights;
        private readonly Geometry.SpatialGrid grid;
        private readonly float cellSize;
        private readonly List<int> scratch = new List<int>();

        public FieldCells(BodySurface body, float cellSize)
        {
            this.cellSize = cellSize;
            var cells = new Dictionary<(int, int, int, int), List<int>>();
            for (var v = 0; v < body.Positions.Length; v++)
            {
                var p = body.Positions[v];
                var key = (Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.y / cellSize), Mathf.FloorToInt(p.z / cellSize), body.Bones.Dominant(v));
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                list.Add(v);
            }
            Positions = new Vector3[cells.Count];
            Area = new float[cells.Count];
            Bone = new int[cells.Count];
            members = new int[cells.Count][];
            memberWeights = new float[cells.Count][];
            var c = 0;
            foreach (var kv in cells)
            {
                var list = kv.Value;
                var area = list.Sum(v => body.Areas[v]);
                var position = Vector3.zero;
                foreach (var v in list) position += body.Positions[v] * body.Areas[v];
                Positions[c] = area > 0f ? position / area : body.Positions[list[0]];
                Area[c] = area;
                Bone[c] = kv.Key.Item4;
                members[c] = list.ToArray();
                memberWeights[c] = list.Select(v => area > 0f ? body.Areas[v] / area : 1f / list.Count).ToArray();
                c++;
            }
            grid = new Geometry.SpatialGrid(Positions, cellSize * 2f);
        }

        /// <summary>
        /// Kernel samples around p within radius plus the distance to the nearest cell, so far fabric always has some;
        /// fellBack when accept took none of them and all were used instead.
        /// </summary>
        public Sample[] Samples(Vector3 p, float radius, Func<int, bool> accept, out bool fellBack)
        {
            fellBack = false;
            var nearest = float.MaxValue;
            for (var r = Mathf.Max(cellSize, 0.001f); nearest == float.MaxValue && r < 10f; r *= 2f)
            {
                grid.Query(p, r, scratch);
                foreach (var c in scratch) nearest = Mathf.Min(nearest, (Positions[c] - p).magnitude);
            }
            if (nearest == float.MaxValue) return new Sample[0];
            var reach = radius + nearest;
            grid.Query(p, reach, scratch);
            var samples = Samplers.Kernel(Positions, Area, p, reach, scratch, accept);
            fellBack = samples.Length == 0;
            return fellBack ? Samplers.Kernel(Positions, Area, p, reach, scratch, null) : samples;
        }

        /// <summary>Area-weighted mean of a body delta per cell.</summary>
        public Vector3[] Deltas(Vector3[] bodyDelta)
        {
            var result = new Vector3[Positions.Length];
            for (var c = 0; c < result.Length; c++)
            {
                var sum = Vector3.zero;
                for (var i = 0; i < members[c].Length; i++) sum += bodyDelta[members[c][i]] * memberWeights[c][i];
                result[c] = sum;
            }
            return result;
        }
    }
}
