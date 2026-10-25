using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    internal static class TransferPipeline
    {
        /// <summary>Steps 1–4 for one body shape frame: sample, weld, smooth, falloff. World space.</summary>
        /// <param name="rigidIslands">Islands whose layer pairs smoothing does not link; null for none.</param>
        public static Vector3[] Sampled(Correspondence c, BodySurface body, GarmentSnapshot garment, Vector3[] bodyDelta, HashSet<int> rigidIslands = null)
        {
            var delta = Samplers.Evaluate(c, body, bodyDelta, garment.Positions);
            DeltaOps.Weld(garment.Topology, delta);
            var iterations = c.Settings.smoothIterations;
            DeltaOps.Smooth(garment.Topology, delta, iterations, iterations > 0 ? c.Pairs.Neighbours(garment.Topology, rigidIslands) : null);
            DeltaOps.Scale(delta, c.Falloff);
            return delta;
        }

        /// <summary>
        /// Steps 1–6 for one body shape frame: sample, weld, smooth, falloff, rigid islands, anti-clip; then the garment's own layers
        /// keep their order and the attached islands ride on the cloth where it ended. World space.
        /// </summary>
        /// <param name="inside">When anti-clip runs, overwritten with its count of candidates still inside each layer at the result.</param>
        /// <param name="self">The garment's own collider; with skin, the skin under it is kept under it too.</param>
        public static Vector3[] AutoWorld(Correspondence c, BodySurface body, GarmentSnapshot garment, Vector3[] bodyDelta,
            HashSet<int> rigidIslands, IReadOnlyList<AntiClip.Layer> layers, float band, ref ClipStats stats, int[] inside = null, Attachments attached = null,
            ColliderSurface self = null, SkinCandidates skin = null)
        {
            var delta = Sampled(c, body, garment, bodyDelta, rigidIslands);
            DeltaOps.Rigid(garment.Topology, garment.Positions, delta, rigidIslands);
            AntiClip.Layer bodyLayer = null;
            if (c.Settings.antiClip && layers != null)
            {
                var counts = AntiClip.Apply(garment.Topology, garment.Positions, delta, layers, band, c.Settings.clearance, rigidIslands, ref stats);
                if (inside != null) Array.Copy(counts, inside, counts.Length);
                // skin passing between the garment's vertices, which the vertex check cannot see
                if (self != null && skin != null)
                    AntiClip.LiftOverSkin(garment.Topology, delta, self, skin, body.Positions, bodyDelta, band, c.Settings.clearance, rigidIslands, ref stats);
                bodyLayer = layers.FirstOrDefault(l => l.IsBody && l.Delta == bodyDelta);
            }
            // the deformed body collider holds the deformed body's normals; anti-clip usually built it already, otherwise it is built
            // here, and without anti-clip the normals are recomputed
            c.Pairs.Keep(garment.Topology, garment.Positions, delta, rigidIslands, body.Topology.Triangles,
                () => bodyLayer != null ? bodyLayer.Deformed.Normals : body.Topology.Normals(DeltaOps.Add(body.Positions, bodyDelta, 1f)));
            // after the layer order, so riders follow the cloth where it was pushed
            attached?.Apply(garment.Topology, garment.Positions, delta);
            return delta;
        }
    }
}
