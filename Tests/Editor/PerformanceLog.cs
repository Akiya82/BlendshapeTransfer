using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class PerformanceLog
    {
        [Test]
        public void Transfer_25kBody_30kGarment_20Shapes_Logged()
        {
            using (var rig = new Rig())
            {
                var bodyMesh = MeshFactory.Grid(MeshFactory.Range(-0.2f, 0.2f, 158), MeshFactory.Range(-0.2f, 0.2f, 158), 0f);
                for (var s = 0; s < 20; s++)
                {
                    var centre = new Vector3(-0.15f + 0.075f * (s % 5), -0.15f + 0.1f * (s / 5), 0f);
                    var bump = MeshFactory.Bump(0.01f, 0.05f);
                    MeshFactory.AddShape(bodyMesh, $"Shape{s}", p => bump(p - centre));
                }
                var body = rig.AddRenderer("Body", bodyMesh, "Hips");
                var garment = rig.AddRenderer("Garment", MeshFactory.Grid(MeshFactory.Range(-0.18f, 0.18f, 173), MeshFactory.Range(-0.18f, 0.18f, 173), 0.003f), "Hips");
                // away from the origin and tilted, so world coordinates round as on an avatar
                rig.Bones["Hips"].SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.Euler(20f, 0f, 0f));

                foreach (var (name, settings) in new[] { ("Tight", TransferSettings.Tight()), ("Skirt", TransferSettings.Skirt()) })
                {
                    Measure(name, settings, bodyMesh, body, garment);
                    MeasureSession(name, settings, body, garment);
                }
            }
        }

        static void Measure(string name, TransferSettings settings, Mesh bodyMesh, SkinnedMeshRenderer body, SkinnedMeshRenderer garment)
        {
            var clock = Stopwatch.StartNew();
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garment.sharedMesh, surface);
            var snapshots = clock.Elapsed.TotalSeconds;
            var correspondence = new Correspondence(surface, snapshot, settings);
            var correspondenceTime = clock.Elapsed.TotalSeconds - snapshots;
            var bodyCollider = new ColliderSurface(surface.Topology, surface.Positions, null);
            var candidates = new ClipCandidates(snapshot.Positions, bodyCollider);
            var setup = clock.Elapsed.TotalSeconds;

            var stats = new ClipStats();
            clock.Restart();
            for (var s = 0; s < 20; s++)
            {
                var delta = surface.WorldDelta(s, 0);
                var layers = new List<AntiClip.Layer> { new AntiClip.Layer { Collider = bodyCollider, Candidates = candidates, Delta = delta, IsBody = true } };
                TransferPipeline.AutoWorld(correspondence, surface, snapshot, delta, new HashSet<int>(), layers, 0.02f, ref stats);
            }
            var shapes = clock.Elapsed.TotalSeconds;

            clock.Restart();
            for (var s = 0; s < 20; s++)
                TransferPipeline.Sampled(correspondence, surface, snapshot, surface.WorldDelta(s, 0));
            var sampled = clock.Elapsed.TotalSeconds;

            Debug.Log($"[Blendshape Transfer] {name}: {bodyMesh.vertexCount} body / {garment.sharedMesh.vertexCount} garment vertices: setup {setup:0.00} s, 20 shapes {shapes:0.00} s");
            Debug.Log($"[Blendshape Transfer] {name} split: snapshots {snapshots:0.00} s, correspondence {correspondenceTime:0.00} s, sampled (Evaluate+weld+smooth+falloff) 20x {sampled:0.00} s, anti-clip (remainder) 20x {shapes - sampled:0.00} s");
        }

        /// <summary>The whole per-shape path of a session: anti-clip, the full-weight and midpoint checks, mesh space and normals.</summary>
        static void MeasureSession(string name, TransferSettings settings, SkinnedMeshRenderer body, SkinnedMeshRenderer garment)
        {
            var clock = Stopwatch.StartNew();
            var session = TransferSession.Open(body, new[] { garment }, new List<Message>());
            var open = clock.Elapsed.TotalSeconds;
            var g = session.Garments[0];
            g.Data.settings = settings;
            g.Correspondence = new Correspondence(session.Surface, g.Snapshot, settings);
            clock.Restart();
            for (var s = 0; s < 20; s++)
            {
                var clip = new ClipStats();
                session.ComputeShape(0, $"Shape{s}", new HashSet<int>(), null, ref clip);
            }
            var shapes = clock.Elapsed.TotalSeconds;
            // the Edit preview after a settings change: the correspondence again, then the one shape shown
            session.EnsureShape("Shape0");
            g.Data.settings.smoothIterations++;
            clock.Restart();
            session.EnsureShape("Shape0");
            var live = clock.Elapsed.TotalSeconds;
            Object.DestroyImmediate(g.Data);
            Debug.Log($"[Blendshape Transfer] {name} session: open {open:0.00} s, 20 shapes (ComputeShape: anti-clip, both checks, mesh space, normals) {shapes:0.00} s, one shape after a settings change {live:0.00} s");
        }
    }
}
