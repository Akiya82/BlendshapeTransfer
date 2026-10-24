using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class AntiClipTests
    {
        private Rig rig;

        [SetUp] public void SetUp() => rig = new Rig();
        [TearDown] public void TearDown() => rig.Dispose();

        private SkinnedMeshRenderer BumpBody()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            return rig.AddRenderer("Body", mesh, "Hips");
        }

        private static TransferSettings Still()
        {
            var s = TransferSettings.Tight();
            s.smoothIterations = 0;
            s.falloffFull = 0.0005f;
            s.falloffZero = 0.001f; // the garment does not follow on its own; only anti-clip moves it
            return s;
        }

        private static float MinDistance(Vector3[] rest, Vector3[] delta, SurfaceQuery collider)
        {
            var min = float.MaxValue;
            for (var v = 0; v < rest.Length; v++)
            {
                var p = rest[v] + delta[v];
                Assert.That(collider.Closest(p, 1f, null, out var hit), Is.True);
                min = Mathf.Min(min, SurfaceQuery.Signed(p, hit));
            }
            return min;
        }

        [Test]
        public void Apply_KeepsClearanceAboveTheGrownSkin()
        {
            var body = BumpBody();
            var garment = rig.AddRenderer("Garment", MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f), "Hips");
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garment.sharedMesh, surface);
            var bodyCollider = new ColliderSurface(surface.Topology, surface.Positions, null);
            var bodyDelta = surface.WorldDelta(0, 0);
            var layers = new List<AntiClip.Layer>
                { new AntiClip.Layer { Collider = bodyCollider, Candidates = new ClipCandidates(snapshot.Positions, bodyCollider), Delta = bodyDelta, IsBody = true } };
            var stats = new ClipStats();

            var delta = TransferPipeline.AutoWorld(new Correspondence(surface, snapshot, Still()), surface, snapshot, bodyDelta, new HashSet<int>(), layers, 0.02f, ref stats);

            Assert.That(stats.Pushed, Is.GreaterThan(0));
            Assert.That(MinDistance(snapshot.Positions, delta, bodyCollider.Deformed(bodyDelta, 1f)), Is.GreaterThan(0.0009f));
        }

        [Test]
        public void Apply_KeepsAnOuterLayerOutsideAnOpenReversedInnerLayer()
        {
            var body = BumpBody();
            var inner = rig.AddRenderer("Inner", MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f, flip: true), "Hips");
            var outer = rig.AddRenderer("Outer", MeshFactory.Grid(MeshFactory.Range(-0.04f, 0.04f, 17), MeshFactory.Range(-0.04f, 0.04f, 17), 0.004f), "Hips");
            var surface = new BodySurface(body);
            var bodyCollider = new ColliderSurface(surface.Topology, surface.Positions, null);
            var bodyDelta = surface.WorldDelta(0, 0);
            var innerSnapshot = new GarmentSnapshot(inner, inner.sharedMesh, surface);
            var outerSnapshot = new GarmentSnapshot(outer, outer.sharedMesh, surface);
            var stats = new ClipStats();

            var tight = TransferSettings.Tight();
            tight.antiClip = false;
            var innerDelta = TransferPipeline.AutoWorld(new Correspondence(surface, innerSnapshot, tight), surface, innerSnapshot, bodyDelta, new HashSet<int>(), null, 0.02f, ref stats);
            var innerCollider = new ColliderSurface(innerSnapshot.Topology, innerSnapshot.Positions, surface);
            var layers = new List<AntiClip.Layer>
            {
                new AntiClip.Layer { Collider = bodyCollider, Candidates = new ClipCandidates(outerSnapshot.Positions, bodyCollider), Delta = bodyDelta, IsBody = true },
                new AntiClip.Layer { Collider = innerCollider, Candidates = new ClipCandidates(outerSnapshot.Positions, innerCollider), Delta = innerDelta },
            };
            var outerDelta = TransferPipeline.AutoWorld(new Correspondence(surface, outerSnapshot, Still()), surface, outerSnapshot, bodyDelta, new HashSet<int>(), layers, 0.02f, ref stats);

            Assert.That(MinDistance(outerSnapshot.Positions, outerDelta, innerCollider.Deformed(innerDelta, 1f)), Is.GreaterThan(0.0009f));
        }

        [Test]
        public void ColliderSurface_InnerLayerPartlyUnderTheSkinStillFacesAwayFromIt()
        {
            var body = BumpBody();
            // middle 0.5 mm under the skin, edges 1 mm above, wound to face away from the skin
            var innerMesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0f);
            innerMesh.vertices = innerMesh.vertices.Select(p => new Vector3(p.x, p.y, Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y)) < 0.0275f ? -0.0005f : 0.001f)).ToArray();
            var inner = rig.AddRenderer("Inner", innerMesh, "Hips");
            var outer = rig.AddRenderer("Outer", MeshFactory.Grid(MeshFactory.Range(-0.04f, 0.04f, 17), MeshFactory.Range(-0.04f, 0.04f, 17), 0.004f), "Hips");
            var surface = new BodySurface(body);
            var bodyCollider = new ColliderSurface(surface.Topology, surface.Positions, null);
            var bodyDelta = surface.WorldDelta(0, 0);
            var innerSnapshot = new GarmentSnapshot(inner, inner.sharedMesh, surface);
            var outerSnapshot = new GarmentSnapshot(outer, outer.sharedMesh, surface);
            var stats = new ClipStats();

            var innerCollider = new ColliderSurface(innerSnapshot.Topology, innerSnapshot.Positions, surface);

            for (var t = 0; t < innerSnapshot.Topology.Triangles.Length / 3; t++)
                Assert.That(innerCollider.RestQuery.FaceNormal(t).z, Is.GreaterThan(0f), $"triangle {t}");
            var tight = TransferSettings.Tight();
            tight.antiClip = false;
            var innerDelta = TransferPipeline.AutoWorld(new Correspondence(surface, innerSnapshot, tight), surface, innerSnapshot, bodyDelta, new HashSet<int>(), null, 0.02f, ref stats);
            var layers = new List<AntiClip.Layer>
            {
                new AntiClip.Layer { Collider = bodyCollider, Candidates = new ClipCandidates(outerSnapshot.Positions, bodyCollider), Delta = bodyDelta, IsBody = true },
                new AntiClip.Layer { Collider = innerCollider, Candidates = new ClipCandidates(outerSnapshot.Positions, innerCollider), Delta = innerDelta },
            };
            var outerDelta = TransferPipeline.AutoWorld(new Correspondence(surface, outerSnapshot, Still()), surface, outerSnapshot, bodyDelta, new HashSet<int>(), layers, 0.02f, ref stats);
            Assert.That(MinDistance(outerSnapshot.Positions, outerDelta, innerCollider.Deformed(innerDelta, 1f)), Is.GreaterThan(0.0009f));
        }

        private static ColliderSurface Quad(float z, bool facingDown = false)
        {
            var quad = new[] { new Vector3(-0.1f, -0.1f, z), new Vector3(0.1f, -0.1f, z), new Vector3(0.1f, 0.1f, z), new Vector3(-0.1f, 0.1f, z) };
            return new ColliderSurface(new Topology(quad, facingDown ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 }), quad, null);
        }

        private static Vector3[] Triangle(float z) => new[] { new Vector3(-0.01f, -0.01f, z), new Vector3(0.01f, -0.01f, z), new Vector3(0f, 0.01f, z) };

        [Test]
        public void FarMoves_AreStillFound()
        {
            // a garment triangle 3 mm above the skin that a shape moved 10 cm down through it
            var collider = Quad(0f);
            var rest = Triangle(0.003f);
            var candidates = new ClipCandidates(rest, collider);
            var delta = Enumerable.Repeat(new Vector3(0f, 0f, -0.1f), 3).ToArray();
            var layers = new List<AntiClip.Layer> { new AntiClip.Layer { Collider = collider, Candidates = candidates, IsBody = true } };
            var stats = new ClipStats();

            Assert.That(AntiClip.CountInside(rest, delta, collider, candidates, null, 0.02f), Is.EqualTo(3));
            AntiClip.Apply(new Topology(rest, new[] { 0, 1, 2 }), rest, delta, layers, 0.02f, 0.001f, new HashSet<int>(), ref stats);
            for (var v = 0; v < 3; v++) Assert.That(rest[v].z + delta[v].z, Is.GreaterThan(0.0009f), $"vertex {v}");
            Assert.That(stats.Unresolved, Is.EqualTo(0));
            // fabric 20 cm off the skin, as over a growing belly, is still a candidate
            Assert.That(new ClipCandidates(new[] { new Vector3(0f, 0f, 0.2f) }, collider).Vertices.Length, Is.EqualTo(1));
        }

        [Test]
        public void Apply_CountsWhatTheRoundsCannotResolve()
        {
            // the skin rises 2 mm under a garment that has 0.5 mm of room under a fixed layer facing down: every round pushes it into the other
            var floor = Quad(0f);
            var ceiling = Quad(0.0015f, facingDown: true);
            var rest = Triangle(0.001f);
            var layers = new List<AntiClip.Layer>
            {
                new AntiClip.Layer { Collider = floor, Candidates = new ClipCandidates(rest, floor), Delta = Enumerable.Repeat(new Vector3(0f, 0f, 0.002f), 4).ToArray(), IsBody = true },
                new AntiClip.Layer { Collider = ceiling, Candidates = new ClipCandidates(rest, ceiling) },
            };
            var stats = new ClipStats();

            AntiClip.Apply(new Topology(rest, new[] { 0, 1, 2 }), rest, new Vector3[3], layers, 0.02f, 0.001f, new HashSet<int>(), ref stats);

            Assert.That(stats.Unresolved, Is.EqualTo(3));
        }

        [Test]
        public void CountInside_SeesAPenetrationOnlyAtTheIntermediateWeight()
        {
            // a quad rotating 60° about the x axis; a garment point 3 mm above it that lags 3 cm along the surface at full weight
            var quad = new[] { new Vector3(-0.1f, -0.1f, 0f), new Vector3(0.1f, -0.1f, 0f), new Vector3(0.1f, 0.1f, 0f), new Vector3(-0.1f, 0.1f, 0f) };
            var topology = new Topology(quad, new[] { 0, 1, 2, 0, 2, 3 });
            var collider = new ColliderSurface(topology, quad, null);
            var rotation = Quaternion.AngleAxis(60f, Vector3.right);
            var colliderDelta = new Vector3[4];
            for (var v = 0; v < 4; v++) colliderDelta[v] = rotation * quad[v] - quad[v];
            var rest = new[] { new Vector3(0f, 0f, 0.003f) };
            var target = rotation * new Vector3(0f, 0f, 0.003f) + rotation * new Vector3(0f, -0.03f, 0f);
            var full = new[] { target - rest[0] };
            var half = new[] { full[0] * 0.5f };
            var halfCollider = new Vector3[4];
            for (var v = 0; v < 4; v++) halfCollider[v] = colliderDelta[v] * 0.5f;
            var candidates = new ClipCandidates(rest, collider);

            Assert.That(AntiClip.CountInside(rest, full, collider, candidates, colliderDelta, 0.02f), Is.EqualTo(0));
            Assert.That(AntiClip.CountInside(rest, half, collider, candidates, halfCollider, 0.02f), Is.EqualTo(1));
        }

        [Test]
        public void Apply_AnInnerHemOnDifferentSkinKeepsOneSide()
        {
            // a floor facing up and, 0.5 mm past the inner sheet's edge (y = 5 cm), a skin fold whose underside faces the hem
            const float sheet = 0.0015f;
            var floor = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 21), MeshFactory.Range(-0.1f, 0.1f, 21), 0f);
            var fold = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 21), MeshFactory.Range(0.0505f, 0.1f, 11), sheet, flip: true);
            var surface = new BodySurface(rig.AddRenderer("Body", MeshFactory.Merge(floor, fold), "Hips"));
            var innerMesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), sheet);
            var innerRest = innerMesh.vertices;
            var innerTopology = new Topology(innerRest, innerMesh.triangles);
            var inner = new ColliderSurface(innerTopology, innerRest, surface);
            // 15 mm above the sheet, 3.5 mm in from its edge
            var outer = new[] { new Vector3(0.0012f, 0.0465f, sheet + 0.015f) };

            // the edge row's closest skin is the fold, the next row's the floor: turning each vertex by its own skin mixes the triangle under the outer vertex
            var raw = innerTopology.Normals(innerRest);
            bool Flipped(int v) => surface.Query.Closest(innerRest[v], 1f, null, out var skin) && Vector3.Dot(raw[v], skin.Normal) < 0f;
            Assert.That(inner.RestQuery.Closest(outer[0], 1f, null, out var under), Is.True);
            var corners = Enumerable.Range(0, 3).Select(i => innerTopology.Triangles[3 * under.Triangle + i]);
            Assert.That(corners.Select(Flipped).Distinct().Count(), Is.EqualTo(2), "per-vertex orientation mixes on the triangle under the outer vertex");

            // the inner layer slides 2 mm away from the hem and stays 15 mm under the outer vertex
            var innerDelta = Enumerable.Repeat(new Vector3(0f, -0.002f, 0f), innerRest.Length).ToArray();
            var layers = new List<AntiClip.Layer> { new AntiClip.Layer { Collider = inner, Candidates = new ClipCandidates(outer, inner), Delta = innerDelta } };
            var stats = new ClipStats();
            AntiClip.Apply(new Topology(outer, new int[0]), outer, new Vector3[1], layers, 0.02f, 0.001f, new HashSet<int>(), ref stats);

            Assert.That(stats.MaxPush, Is.LessThan(0.002f));
        }

        [Test]
        public void Apply_IgnoresAnInnerSheetBesideTheVertex()
        {
            const float sheet = 0.002f;
            var surface = new BodySurface(rig.AddRenderer("Body", MeshFactory.Grid(MeshFactory.Range(-0.2f, 0.2f, 21), MeshFactory.Range(-0.2f, 0.2f, 21), 0f), "Hips"));
            var innerMesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), sheet);
            var innerRest = innerMesh.vertices;
            var inner = new ColliderSurface(new Topology(innerRest, innerMesh.triangles), innerRest, surface);
            var outer = new[]
            {
                new Vector3(0.0012f, 0.12f, sheet + 0.004f), // 7 cm past the open edge (y = 5 cm), 4 mm above the sheet's plane
                new Vector3(0.0012f, 0.0465f, sheet + 0.002f), // 2 mm above the sheet, which then slides out from under it
            };
            // the sheet tilts 10° about its edge, the side past the edge rising through both plane distances, and slides 3 cm back
            var edge = new Vector3(0f, 0.05f, sheet);
            var tilt = Quaternion.AngleAxis(10f, Vector3.right);
            var innerDelta = innerRest.Select(p => edge + tilt * (p - edge) + new Vector3(0f, -0.03f, 0f) - p).ToArray();
            var candidates = new ClipCandidates(outer, inner);
            var layers = new List<AntiClip.Layer> { new AntiClip.Layer { Collider = inner, Candidates = candidates, Delta = innerDelta } };
            var stats = new ClipStats();
            var delta = new Vector3[outer.Length];

            var inside = AntiClip.Apply(new Topology(outer, new int[0]), outer, delta, layers, 0.02f, 0.001f, new HashSet<int>(), ref stats);

            Assert.That(stats.MaxPush, Is.EqualTo(0f));
            Assert.That(inside[0], Is.EqualTo(0));
            Assert.That(AntiClip.CountInside(outer, delta, inner, candidates, innerDelta, 0.02f), Is.EqualTo(0));
            // the vertex beside the edge is no candidate; the one over the sheet is, and the check after the shape lets it go
            Assert.That(candidates.Vertices, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void Apply_IgnoresAHemRimStandingAcrossTheSkin()
        {
            // an inner sheet 12 mm over flat skin whose hem (y = 5 cm) bends down into a 1 cm rim, its lower edge 0.3 mm out for x ≤ 0
            // and 0.3 mm in beyond: the rim's faces stand across the skin (cos ±0.03), and that noise alone decides their outside
            const float sheet = 0.012f;
            var surface = new BodySurface(rig.AddRenderer("Body", MeshFactory.Grid(MeshFactory.Range(-0.2f, 0.2f, 21), MeshFactory.Range(-0.2f, 0.2f, 21), 0f), "Hips"));
            var xs = MeshFactory.Range(-0.05f, 0.05f, 21);
            var sheetMesh = MeshFactory.Grid(xs, xs, sheet);
            const int top = 20 * 21; // the sheet's row at y = 5 cm
            var bottom = sheetMesh.vertexCount;
            var innerRest = sheetMesh.vertices.Concat(xs.Select(x => new Vector3(x, 0.05f + (x <= 0f ? 0.0003f : -0.0003f), sheet - 0.01f))).ToArray();
            var triangles = sheetMesh.triangles.ToList();
            for (var i = 0; i < 20; i++) triangles.AddRange(new[] { top + i, bottom + i, top + i + 1, top + i + 1, bottom + i, bottom + i + 1 });
            var innerTopology = new Topology(innerRest, triangles.ToArray());
            var inner = new ColliderSurface(innerTopology, innerRest, surface);
            // 3 cm out from the rim along its normal; the tucked half of the hem then moves 2 mm towards it
            var outer = new[] { new Vector3(-0.002f, 0.08f, 0.005f) };
            var innerDelta = innerRest.Select(p => p.x > 1e-6f ? new Vector3(0f, 0.002f, 0f) : Vector3.zero).ToArray();

            // the closest face swaps between two rim faces sharing a corner whose side by the skin differs
            float SkinCos(int f)
            {
                Vector3 a = innerRest[innerTopology.Triangles[3 * f]], b = innerRest[innerTopology.Triangles[3 * f + 1]], c = innerRest[innerTopology.Triangles[3 * f + 2]];
                var n = Vector3.Cross(b - a, c - a);
                return n.z / n.magnitude;
            }
            int Closest(Vector3[] positions)
            {
                var query = new SurfaceQuery(positions, innerTopology.Normals(positions), innerTopology, rejectBoundary: true);
                Assert.That(query.Closest(outer[0], 1f, null, out var hit), Is.True);
                return hit.Triangle;
            }
            IEnumerable<int> Corners(int f) => Enumerable.Range(0, 3).Select(i => innerTopology.Group[innerTopology.Triangles[3 * f + i]]);
            var before = Closest(innerRest);
            var after = Closest(DeltaOps.Add(innerRest, innerDelta, 1f));
            Assert.That(Mathf.Abs(SkinCos(before)), Is.LessThan(ColliderSurface.RimCos));
            Assert.That(Mathf.Abs(SkinCos(after)), Is.LessThan(ColliderSurface.RimCos));
            Assert.That(Mathf.Sign(SkinCos(before)), Is.Not.EqualTo(Mathf.Sign(SkinCos(after))));
            Assert.That(Corners(before).Intersect(Corners(after)).Count(), Is.GreaterThan(0));

            var layers = new List<AntiClip.Layer> { new AntiClip.Layer { Collider = inner, Candidates = new ClipCandidates(outer, inner), Delta = innerDelta } };
            var stats = new ClipStats();
            AntiClip.Apply(new Topology(outer, new int[0]), outer, new Vector3[1], layers, 0.04f, 0.001f, new HashSet<int>(), ref stats);

            Assert.That(stats.MaxPush, Is.LessThan(0.002f));
        }
    }
}
