using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class SamplerTests
    {
        private Rig rig;

        [SetUp] public void SetUp() => rig = new Rig();
        [TearDown] public void TearDown() => rig.Dispose();

        private static TransferSettings Plain(TransferSettings s)
        {
            s.smoothIterations = 0;
            s.antiClip = false;
            s.falloffFull = 1f;
            s.falloffZero = 2f;
            return s;
        }

        private static Vector3[] Run(SkinnedMeshRenderer body, SkinnedMeshRenderer garment, TransferSettings settings, string shape, out Correspondence correspondence)
        {
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garment.sharedMesh, surface);
            correspondence = new Correspondence(surface, snapshot, settings);
            return TransferPipeline.Sampled(correspondence, surface, snapshot, surface.WorldDelta(body.sharedMesh.GetBlendShapeIndex(shape), 0));
        }

        private SkinnedMeshRenderer FlatBody(string shape, Func<Vector3, Vector3> delta, int columns = 41)
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, columns), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, shape, delta);
            return rig.AddRenderer("Body", mesh, "Hips");
        }

        [Test]
        public void Surface_FabricOverABumpFollowsIt()
        {
            var bump = MeshFactory.Bump(0.01f, 0.04f);
            var body = FlatBody("Bump", bump);
            var garmentMesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f);
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");

            var raw = Run(body, garment, Plain(TransferSettings.Tight()), "Bump", out _);

            var vertices = garmentMesh.vertices;
            for (var v = 0; v < raw.Length; v++)
                Assert.That(raw[v].z, Is.EqualTo(bump(vertices[v]).z).Within(0.0003f), $"vertex {v}");
        }

        [Test]
        public void Surface_OffsetRotatesWithTheSurface()
        {
            const float angle = 30f * Mathf.Deg2Rad;
            var bodyMesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 21), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(bodyMesh, "Hinge", p => p.y > 0f ? new Vector3(0f, p.y * Mathf.Cos(angle) - p.y, p.y * Mathf.Sin(angle)) : Vector3.zero);
            var body = rig.AddRenderer("Body", bodyMesh, "Hips");
            var garmentMesh = MeshFactory.Grid(MeshFactory.Range(-0.02f, 0.02f, 5), MeshFactory.Range(0.04f, 0.06f, 5), 0.003f);
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");

            var raw = Run(body, garment, Plain(TransferSettings.Tight()), "Hinge", out _);

            foreach (var (p, v) in garmentMesh.vertices.Select((p, v) => (p, v)))
            {
                var expected = new Vector3(p.x, p.y * Mathf.Cos(angle) - 0.003f * Mathf.Sin(angle), p.y * Mathf.Sin(angle) + 0.003f * Mathf.Cos(angle)) - p;
                Assert.That((raw[v] - expected).magnitude, Is.LessThan(0.0001f), $"vertex {v}");
            }
        }

        private (SkinnedMeshRenderer body, SkinnedMeshRenderer garment) Legs()
        {
            var bodyMesh = MeshFactory.Merge(MeshFactory.Cylinder(-0.1f, 0f, 0.06f, 0.6f, 1f, 32, 9), MeshFactory.Cylinder(0.1f, 0f, 0.06f, 0.6f, 1f, 32, 9));
            MeshFactory.AddShape(bodyMesh, "Thick", p =>
            {
                var cx = p.x < 0f ? -0.1f : 0.1f;
                return new Vector3(p.x - cx, 0f, p.z).normalized * 0.01f;
            });
            var body = rig.AddRenderer("Body", bodyMesh, (v, p) => new[] { (p.x < 0f ? "UpperLeg.R" : "UpperLeg.L", 1f) });
            var strip = MeshFactory.Grid(MeshFactory.Range(-0.03f, 0.03f, 13), MeshFactory.Range(0.7f, 0.9f, 5), 0f);
            return (body, rig.AddRenderer("Skirt", strip, "Hips"));
        }

        private static float MaxStep(Vector3[] raw, int row, int columns)
        {
            var max = 0f;
            for (var i = 0; i < columns - 1; i++) max = Mathf.Max(max, (raw[row * columns + i + 1] - raw[row * columns + i]).magnitude);
            return max;
        }

        [Test]
        public void Field_BetweenTwoLegsIsSymmetricAndHasNoSeam()
        {
            var (body, garment) = Legs();
            var field = Plain(new TransferSettings { mode = SamplerMode.Field });
            var fieldRaw = Run(body, garment, field, "Thick", out _);
            var surfaceRaw = Run(body, garment, Plain(TransferSettings.Tight()), "Thick", out _);

            Assert.That(Mathf.Abs(fieldRaw[2 * 13 + 6].x), Is.LessThan(0.001f), "middle of the strip");
            Assert.That(fieldRaw[2 * 13].x, Is.GreaterThan(0.001f), "next to the leg at x = -0.1 the fabric follows its inner side toward the midline");
            Assert.That(fieldRaw[2 * 13 + 12].x, Is.LessThan(-0.001f), "next to the leg at x = +0.1 likewise");
            Assert.That(MaxStep(fieldRaw, 2, 13), Is.LessThan(0.25f * MaxStep(surfaceRaw, 2, 13)), "Field has no midline jump");
        }

        [Test]
        public void Field_CountsAVertexWhoseBonesTakeNoCell()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            var body = rig.AddRenderer("Body", mesh, "Chest", "UpperLeg.L");
            var garment = rig.AddRenderer("Garment", MeshFactory.Grid(MeshFactory.Range(-0.01f, 0.01f, 3), MeshFactory.Range(-0.01f, 0.01f, 3), 0.01f), "UpperLeg.L");

            Run(body, garment, Plain(new TransferSettings { mode = SamplerMode.Field }), "Bump", out var correspondence);

            Assert.That(correspondence.FallbackCount, Is.EqualTo(9));
        }

        [Test]
        public void ZeroRadii_FinishWithoutNaN()
        {
            var body = FlatBody("Bump", MeshFactory.Bump(0.01f, 0.04f));
            // the middle vertex sits right above a body vertex, where a zero kernel radius divides 0 by 0
            var near = rig.AddRenderer("Near", MeshFactory.Grid(MeshFactory.Range(-0.02f, 0.02f, 5), MeshFactory.Range(-0.02f, 0.02f, 5), 0.003f), "Hips");
            var far = rig.AddRenderer("Far", MeshFactory.Grid(MeshFactory.Range(-0.02f, 0.02f, 5), MeshFactory.Range(-0.02f, 0.02f, 5), 0.03f), "Hips");
            var blend = Plain(TransferSettings.Loose());
            blend.blendRadius = 0f;
            var hybrid = Plain(TransferSettings.Skirt());
            hybrid.blendRadius = 0f;
            hybrid.fieldRadius = 0f;

            Assert.That(Run(body, near, blend, "Bump", out _).Count(d => float.IsNaN(d.x + d.y + d.z)), Is.EqualTo(0), "Blend");
            Assert.That(Run(body, far, hybrid, "Bump", out var correspondence).Count(d => float.IsNaN(d.x + d.y + d.z)), Is.EqualTo(0), "Hybrid");
            Assert.That(correspondence.Settings.fieldRadius, Is.EqualTo(0.001f));
        }

        [Test]
        public void CentimetresTypedAsMetres_AreClampedToSaneBounds()
        {
            var body = FlatBody("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = rig.AddRenderer("Garment", MeshFactory.Grid(MeshFactory.Range(-0.01f, 0.01f, 3), MeshFactory.Range(-0.01f, 0.01f, 3), 0.003f), "Hips");
            var settings = new TransferSettings
            {
                mode = SamplerMode.Hybrid, blendRadius = 4f, fieldRadius = 12f, hybridNear = 1.5f, hybridFar = 5f,
                falloffFull = 2f, falloffZero = 6f, clearance = 0.3f, rigidIslandSize = 1000f,
            };

            Run(body, garment, settings, "Bump", out var correspondence);

            var s = correspondence.Settings;
            Assert.That(new[] { s.blendRadius, s.fieldRadius }, Is.All.EqualTo(TransferSettings.MaxRadius));
            Assert.That(new[] { s.hybridNear, s.hybridFar, s.falloffFull, s.falloffZero }, Is.All.EqualTo(TransferSettings.MaxDistance));
            Assert.That(s.clearance, Is.EqualTo(TransferSettings.MaxClearance));
            Assert.That(s.rigidIslandSize, Is.EqualTo(1000f), "the Rigid preset's every-island value");
        }

        [Test]
        public void Blend_WeighsSamplesByArea_NotByVertexCount()
        {
            Func<Vector3, Vector3> ramp = p => new Vector3(0f, 0f, 0.05f * (p.x + 0.1f) / 0.2f);
            var even = FlatBody("Ramp", ramp);
            var unevenColumns = MeshFactory.Range(-0.1f, 0f, 81).Concat(MeshFactory.Range(0f, 0.1f, 21).Skip(1)).ToArray();
            var unevenMesh = MeshFactory.Grid(unevenColumns, MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(unevenMesh, "Ramp", ramp);
            var uneven = rig.AddRenderer("UnevenBody", unevenMesh, "Hips");
            var garment = rig.AddRenderer("Garment", MeshFactory.Grid(MeshFactory.Range(-0.005f, 0.005f, 3), MeshFactory.Range(-0.005f, 0.005f, 3), 0.003f), "Hips");
            var loose = Plain(TransferSettings.Loose());

            var a = Run(even, garment, loose, "Ramp", out _)[4];
            var b = Run(uneven, garment, loose, "Ramp", out _)[4];

            Assert.That(a.z, Is.EqualTo(0.025f).Within(0.0003f));
            Assert.That(b.z, Is.EqualTo(a.z).Within(0.0003f), "dense left half must not pull the mean");
        }

        [Test]
        public void BoneFilter_SleeveNextToTheTorsoTakesTheArm()
        {
            var bodyMesh = MeshFactory.Merge(
                MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 11), MeshFactory.Range(-0.05f, 0.05f, 11), 0f),
                MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 11), MeshFactory.Range(-0.05f, 0.05f, 11), 0.02f, flip: true));
            MeshFactory.AddShape(bodyMesh, "Breast", p => p.z < 0.01f ? new Vector3(0f, 0f, 0.01f) : Vector3.zero);
            var body = rig.AddRenderer("Body", bodyMesh, (v, p) => new[] { (v < 121 ? "Chest" : "UpperArm.L", 1f) }, "Shoulder.L");
            var garment = rig.AddRenderer("Sleeve", MeshFactory.Grid(MeshFactory.Range(-0.01f, 0.01f, 3), MeshFactory.Range(-0.01f, 0.01f, 3), 0.006f), "UpperArm.L");

            var filtered = Run(body, garment, Plain(TransferSettings.Tight()), "Breast", out _);
            var open = Plain(TransferSettings.Tight());
            open.boneFilter = false;
            var unfiltered = Run(body, garment, open, "Breast", out _);

            Assert.That(filtered.Max(d => d.magnitude), Is.LessThan(0.0001f));
            Assert.That(unfiltered.Min(d => d.z), Is.GreaterThan(0.009f));
        }

        [Test]
        public void BoneFilter_AcceptedSkinFarAwayFallsBackToTheNearSkin()
        {
            // the arm is 10 cm above the torso, so a sleeve 5 mm over the torso has no accepted skin nearby
            var bodyMesh = MeshFactory.Merge(
                MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 11), MeshFactory.Range(-0.05f, 0.05f, 11), 0f),
                MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 11), MeshFactory.Range(-0.05f, 0.05f, 11), 0.105f, flip: true));
            MeshFactory.AddShape(bodyMesh, "Breast", p => p.z < 0.05f ? new Vector3(0f, 0f, 0.01f) : Vector3.zero);
            var body = rig.AddRenderer("Body", bodyMesh, (v, p) => new[] { (v < 121 ? "Chest" : "UpperArm.L", 1f) }, "Shoulder.L");
            var garment = rig.AddRenderer("Sleeve", MeshFactory.Grid(MeshFactory.Range(-0.01f, 0.01f, 3), MeshFactory.Range(-0.01f, 0.01f, 3), 0.005f), "UpperArm.L");

            var raw = Run(body, garment, Plain(TransferSettings.Tight()), "Breast", out var correspondence);

            Assert.That(correspondence.FallbackCount, Is.EqualTo(9));
            for (var v = 0; v < raw.Length; v++) Assert.That(raw[v].z, Is.EqualTo(0.01f).Within(0.0001f), $"vertex {v}");
        }

        [Test]
        public void SideFilter_FabricInsideTheSkinBindsToTheSkinItClipsInto()
        {
            // a fold 1.5 cm above faces the fabric, which is 1 mm inside the lower skin
            var bump = MeshFactory.Bump(0.01f, 0.04f);
            var bodyMesh = MeshFactory.Merge(
                MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f),
                MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 21), MeshFactory.Range(-0.1f, 0.1f, 21), 0.015f, flip: true));
            MeshFactory.AddShape(bodyMesh, "Bump", p => p.z < 0.005f ? bump(p) : Vector3.zero);
            var body = rig.AddRenderer("Body", bodyMesh, "Hips");
            var garmentMesh = MeshFactory.Grid(MeshFactory.Range(-0.02f, 0.02f, 5), MeshFactory.Range(-0.02f, 0.02f, 5), -0.001f);
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");

            var raw = Run(body, garment, Plain(TransferSettings.Tight()), "Bump", out var correspondence);

            var vertices = garmentMesh.vertices;
            for (var v = 0; v < raw.Length; v++)
                Assert.That(raw[v].z, Is.EqualTo(bump(vertices[v]).z).Within(0.0003f), $"vertex {v}");
            Assert.That(correspondence.FallbackCount, Is.EqualTo(0));
        }

        [Test]
        public void SideFilter_SleeveAroundAThickeningArmGrows()
        {
            var bodyMesh = MeshFactory.Cylinder(0f, 0f, 0.03f, 0.7f, 1f, 48, 13);
            MeshFactory.AddShape(bodyMesh, "ArmThick", p => new Vector3(p.x, 0f, p.z).normalized * 0.005f);
            var body = rig.AddRenderer("Body", bodyMesh, "UpperArm.L");
            var sleeveMesh = MeshFactory.Cylinder(0f, 0f, 0.035f, 0.8f, 0.9f, 48, 5);
            var garment = rig.AddRenderer("Sleeve", sleeveMesh, "UpperArm.L");

            var raw = Run(body, garment, Plain(TransferSettings.Loose()), "ArmThick", out _);

            var vertices = sleeveMesh.vertices;
            for (var v = 0; v < raw.Length; v++)
                Assert.That(Vector3.Dot(raw[v], new Vector3(vertices[v].x, 0f, vertices[v].z).normalized), Is.GreaterThan(0.0046f), $"vertex {v}");
        }

        [Test]
        public void Hybrid_HasNoJumpAcrossTheBand()
        {
            var body = FlatBody("Bump", MeshFactory.Bump(0.005f, 0.08f));
            var strip = MeshFactory.Grid(MeshFactory.Range(-0.04f, 0.04f, 17), MeshFactory.Range(-0.005f, 0.005f, 2), 0f);
            strip.vertices = strip.vertices.Select(p => new Vector3(p.x, p.y, 0.005f + (p.x + 0.04f) / 0.08f * 0.075f)).ToArray();
            var garment = rig.AddRenderer("Garment", strip, "Hips");

            var raw = Run(body, garment, Plain(TransferSettings.Skirt()), "Bump", out _);

            Assert.That(MaxStep(raw, 0, 17), Is.LessThan(0.0015f), "a hard switch between the regimes would step by about 3 mm");
        }

        [Test]
        public void UnmovedSkin_GivesExactlyZero_OnATiltedBodyAMetreUp()
        {
            var body = FlatBody("Bump", MeshFactory.Bump(0.01f, 0.04f));
            // off the body's vertices, so closest points fall inside triangles; the button straddles x = 0
            var garmentMesh = MeshFactory.Merge(
                MeshFactory.Grid(MeshFactory.Range(-0.0885f, 0.0915f, 37), MeshFactory.Range(-0.0885f, 0.0915f, 37), 0.0023f),
                MeshFactory.Box(new Vector3(0.0005f, 0.0755f, 0.0061f), 0.004f));
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");
            rig.Bones["Hips"].SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.AngleAxis(20f, new Vector3(1f, 0f, 1f).normalized));
            var settings = Plain(TransferSettings.Tight());
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garmentMesh, surface);
            var correspondence = new Correspondence(surface, snapshot, settings);
            var rigid = DeltaOps.RigidIslands(snapshot.Topology, snapshot.Positions, settings.rigidIslandSize, new int[0]);
            var stats = new ClipStats();

            var delta = TransferPipeline.AutoWorld(correspondence, surface, snapshot, surface.WorldDelta(0, 0), rigid, null, 0f, ref stats);

            Assert.That(rigid.Count, Is.EqualTo(1), "the button");
            Assert.That(delta.Max(d => d.magnitude), Is.GreaterThan(0.005f), "the bump moves its part");
            var vertices = garmentMesh.vertices;
            var unmoved = 0;
            for (var v = 0; v < vertices.Length; v++)
            {
                // no skin moves within a body triangle and its neighbours of these
                if (new Vector2(vertices[v].x, vertices[v].y).magnitude < 0.055f) continue;
                unmoved++;
                Assert.That(delta[v].x == 0f && delta[v].y == 0f && delta[v].z == 0f, Is.True, $"vertex {v}: ({delta[v].x:E1}, {delta[v].y:E1}, {delta[v].z:E1})");
            }
            Assert.That(unmoved, Is.GreaterThan(100));
        }

        [Test]
        public void RigidIsland_OnTheCloth_RidesOnItsSmoothedMotion()
        {
            // smoothing carries the bump's motion along the cloth past the bump's edge, where a rivet sits 0.2 mm above it; the rivet
            // is an island of its own, so smoothing never reaches it and its own sample there is about zero
            var body = FlatBody("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garmentMesh = MeshFactory.Merge(
                MeshFactory.Grid(MeshFactory.Range(-0.0885f, 0.0915f, 37), MeshFactory.Range(-0.0885f, 0.0915f, 37), 0.0023f),
                MeshFactory.Box(new Vector3(0.0415f, 0.0015f, 0.0045f), 0.004f));
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");
            var settings = Plain(TransferSettings.Tight());
            settings.smoothIterations = 20;
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garmentMesh, surface);
            var correspondence = new Correspondence(surface, snapshot, settings);
            var rigid = DeltaOps.RigidIslands(snapshot.Topology, snapshot.Positions, settings.rigidIslandSize, new int[0]);
            var attached = new Attachments(snapshot.Topology, snapshot.Positions, rigid, correspondence.Distance);
            var stats = new ClipStats();

            var alone = TransferPipeline.AutoWorld(correspondence, surface, snapshot, surface.WorldDelta(0, 0), rigid, null, 0f, ref stats);
            var riding = TransferPipeline.AutoWorld(correspondence, surface, snapshot, surface.WorldDelta(0, 0), rigid, null, 0f, ref stats, attached: attached);

            // the cloth vertex under the rivet's centre
            const int under = 18 * 37 + 26;
            var rivet = Enumerable.Range(37 * 37, 8).ToArray();
            float MeanZ(Vector3[] d) => rivet.Average(v => d[v].z);
            Assert.That(attached.Islands.Count, Is.EqualTo(1), "the rivet");
            Assert.That(riding[under].z, Is.GreaterThan(0.0002f), "smoothing moves the cloth under the rivet");
            Assert.That(MeanZ(alone), Is.LessThan(0.5f * alone[under].z), "without riding the cloth swallows the rivet");
            Assert.That(MeanZ(riding), Is.EqualTo(riding[under].z).Within(0.1f * riding[under].z));
            for (var a = 0; a < rivet.Length; a++)
            for (var b = a + 1; b < rivet.Length; b++)
            {
                var p = snapshot.Positions;
                Assert.That((p[rivet[a]] + riding[rivet[a]] - p[rivet[b]] - riding[rivet[b]]).magnitude,
                    Is.EqualTo((p[rivet[a]] - p[rivet[b]]).magnitude).Within(1e-6f), "the rivet keeps its shape");
            }
        }

        [Test]
        public void Overlay_OnTheCloth_RidesVertexByVertex_ALiningAndAStrapOffTheClothDoNot()
        {
            // past the bump's edge, where only smoothing moves the cloth: a belt loop 35 mm long (too long to be rigid) lies 0.2 mm on
            // it, each loop vertex right above a cloth vertex; a lining of the same size lies 1 mm under it; a strap starts on it and
            // runs 2 cm past its edge
            var body = FlatBody("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var clothRows = MeshFactory.Range(-0.0885f, 0.0915f, 37);
            var loopRows = clothRows.Skip(15).Take(8).ToArray();
            var garmentMesh = MeshFactory.Merge(
                MeshFactory.Grid(clothRows, clothRows, 0.0023f),
                MeshFactory.Grid(new[] { 0.0415f, 0.0465f }, loopRows, 0.0025f),
                MeshFactory.Grid(new[] { -0.0465f, -0.0415f }, loopRows, 0.0013f),
                MeshFactory.Grid(new[] { 0.0115f, 0.0165f }, MeshFactory.Range(0.0715f, 0.1115f, 9), 0.0025f));
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");
            var settings = Plain(TransferSettings.Tight());
            settings.smoothIterations = 20;
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garmentMesh, surface);
            var correspondence = new Correspondence(surface, snapshot, settings);
            var rigid = DeltaOps.RigidIslands(snapshot.Topology, snapshot.Positions, settings.rigidIslandSize, new int[0]);
            var attached = new Attachments(snapshot.Topology, snapshot.Positions, rigid, correspondence.Distance);
            var stats = new ClipStats();

            var alone = TransferPipeline.AutoWorld(correspondence, surface, snapshot, surface.WorldDelta(0, 0), rigid, null, 0f, ref stats);
            var riding = TransferPipeline.AutoWorld(correspondence, surface, snapshot, surface.WorldDelta(0, 0), rigid, null, 0f, ref stats, attached: attached);

            const int cloth = 37 * 37;
            var loop = Enumerable.Range(cloth, 16).ToArray();
            int Under(int v) => (15 + (v - cloth) / 2) * 37 + 26 + (v - cloth) % 2;
            var island = snapshot.Topology.Island;
            Assert.That(rigid, Is.Empty, "nothing here is small enough to be rigid");
            Assert.That(attached.Overlays, Is.EqualTo(new[] { island[loop[0]] }), "only the loop rides: the lining is closer to the skin, the strap leaves the cloth");
            Assert.That(loop.Average(v => alone[Under(v)].z), Is.GreaterThan(0.00005f), "smoothing moves the cloth under the loop");
            Assert.That(loop.Average(v => alone[v].z), Is.GreaterThan(0.5f * loop.Average(v => alone[Under(v)].z)),
                "within LayerPairs.MaxGap of the cloth, smoothing links the loop to it, so it follows even without riding");
            foreach (var v in loop)
                Assert.That((riding[v] - riding[Under(v)]).magnitude, Is.LessThan(1e-6f), $"loop vertex {v - cloth} rides the cloth vertex under it");
        }

        [Test]
        public void FoldedStrip_TheOuterSheetStaysInFrontOfTheInner()
        {
            // a waistband folded back on itself: the outer sheet runs down into a panel, the inner sheet lies 1 mm under the outer and
            // ends at the strip's bottom. The skin rises 10 mm under the strip and less under the panel, so smoothing pulls the outer
            // sheet towards the panel while the inner sheet averages over itself
            var body = FlatBody("Belly", p => new Vector3(0f, 0f, 0.01f * Correspondence.Smoothstep(-0.015f, -0.002f, p.y)));
            const int columns = 13, panel = 12, strip = 5;
            var rows = new List<(float Y, float Z)>();
            for (var r = 0; r < panel + strip; r++) rows.Add((-0.0585f + 0.005f * r, 0.006f));
            for (var r = strip - 1; r >= 0; r--) rows.Add((0.0015f + 0.005f * r, 0.005f));
            var garmentMesh = MeshFactory.Grid(MeshFactory.Range(-0.0285f, 0.0315f, columns), MeshFactory.Range(0f, rows.Count - 1, rows.Count), 0f);
            garmentMesh.vertices = garmentMesh.vertices.Select(p => new Vector3(p.x, rows[Mathf.RoundToInt(p.y)].Y, rows[Mathf.RoundToInt(p.y)].Z)).ToArray();
            garmentMesh.RecalculateBounds();
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");
            var settings = TransferSettings.Tight();
            settings.smoothIterations = 20;
            settings.falloffFull = 1f;
            settings.falloffZero = 2f;
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garmentMesh, surface);
            var correspondence = new Correspondence(surface, snapshot, settings);
            var bodyDelta = surface.WorldDelta(0, 0);
            var bodyCollider = new ColliderSurface(surface.Topology, surface.Positions, null);
            var layers = new List<AntiClip.Layer>
                { new AntiClip.Layer { Collider = bodyCollider, Candidates = new ClipCandidates(snapshot.Positions, bodyCollider), Delta = bodyDelta, IsBody = true } };
            var stats = new ClipStats();

            var delta = TransferPipeline.AutoWorld(correspondence, surface, snapshot, bodyDelta, new HashSet<int>(), layers, 0.02f, ref stats);

            // each outer strip vertex lies right over the inner vertex of its column and height
            var p0 = snapshot.Positions;
            var gaps = new List<float>();
            for (var r = 0; r < strip; r++)
            for (var c = 0; c < columns; c++)
            {
                int outer = (panel + r) * columns + c, inner = (panel + 2 * strip - 1 - r) * columns + c;
                gaps.Add(p0[outer].z + delta[outer].z - p0[inner].z - delta[inner].z);
            }
            Assert.That(gaps.Min(), Is.GreaterThanOrEqualTo(LayerPairs.MinGap - 5e-5f),
                $"{gaps.Count(g => g < LayerPairs.MinGap - 5e-5f)} of {gaps.Count} outer vertices closer than {LayerPairs.MinGap * 1000f:0.0} mm in front of the inner sheet, nearest {gaps.Min() * 1000f:0.00} mm");
        }

        [Test]
        public void RigidIsland_OnLinedCloth_RidesTheClothWhereTheLayerOrderPushedIt()
        {
            // cloth 3 mm over the skin, a lining 1 mm under it and a rivet 0.2 mm on it. The skin moves 2 mm out; the falloff moves the
            // lining 2 mm and the cloth 1 mm, so the layer order pushes the cloth out to 0.5 mm in front of the lining, and the rivet
            // riding the cloth has to end there too
            var body = FlatBody("Out", p => new Vector3(0f, 0f, 0.002f));
            var clothRows = MeshFactory.Range(-0.0285f, 0.0315f, 13);
            var liningRows = MeshFactory.Range(-0.0185f, 0.0215f, 9);
            var garmentMesh = MeshFactory.Merge(
                MeshFactory.Grid(clothRows, clothRows, 0.003f),
                MeshFactory.Grid(liningRows, liningRows, 0.002f),
                MeshFactory.Box(new Vector3(0.0015f, 0.0015f, 0.0052f), 0.004f));
            var garment = rig.AddRenderer("Garment", garmentMesh, "Hips");
            var settings = Plain(TransferSettings.Tight());
            settings.falloffFull = 0.002f;
            settings.falloffZero = 0.004f;
            var surface = new BodySurface(body);
            var snapshot = new GarmentSnapshot(garment, garmentMesh, surface);
            var correspondence = new Correspondence(surface, snapshot, settings);
            var rigid = DeltaOps.RigidIslands(snapshot.Topology, snapshot.Positions, settings.rigidIslandSize, new int[0]);
            var attached = new Attachments(snapshot.Topology, snapshot.Positions, rigid, correspondence.Distance);
            var stats = new ClipStats();

            var delta = TransferPipeline.AutoWorld(correspondence, surface, snapshot, surface.WorldDelta(0, 0), rigid, null, 0f, ref stats, attached: attached);

            // the cloth and lining vertices under the rivet's centre, and the rivet's bottom face
            const int under = 6 * 13 + 6, lining = 13 * 13 + 4 * 9 + 4, rivet = 13 * 13 + 9 * 9;
            var rest = snapshot.Positions;
            float Z(int i) => rest[i].z + delta[i].z;
            Assert.That(attached.Islands, Is.EquivalentTo(new[] { snapshot.Topology.Island[rivet] }), "the rivet rides");
            Assert.That(delta[lining].z, Is.EqualTo(0.002f).Within(1e-6f), "the lining moves with the skin");
            Assert.That(Z(under) - Z(lining), Is.EqualTo(LayerPairs.MinGap).Within(1e-6f), "the layer order pushed the cloth in front of the lining");
            for (var v = rivet; v < rivet + 4; v++)
                Assert.That(Z(v), Is.GreaterThan(Z(under)), $"rivet vertex {v - rivet} is {(Z(under) - Z(v)) * 1000f:0.00} mm inside the cloth");
        }

        [Test]
        public void LayerPairs_Keep_PushesTheWholeOuterGroup_AndLeavesRigidAndUnmovedPairsAlone()
        {
            // over flat skin: an outer sheet whose centre vertex is split along a seam and a small rigid sheet, both 1 mm over an inner sheet
            var outerRows = MeshFactory.Range(0.0015f, 0.0115f, 3);
            var mesh = MeshFactory.Merge(
                MeshFactory.Grid(outerRows, outerRows, 0.003f),
                MeshFactory.Grid(MeshFactory.Range(0.0215f, 0.0265f, 2), MeshFactory.Range(0.0015f, 0.0065f, 2), 0.003f),
                MeshFactory.Grid(MeshFactory.Range(-0.005f, 0.03f, 8), MeshFactory.Range(-0.005f, 0.015f, 5), 0.002f));
            var positions = mesh.vertices.ToList();
            var triangles = mesh.triangles;
            positions.Add(positions[4]);
            var copy = positions.Count - 1;
            // the outer sheet's second row of quads takes the copy
            for (var i = 12; i < 24; i++)
                if (triangles[i] == 4) triangles[i] = copy;
            var rest = positions.ToArray();
            var topology = new Topology(rest, triangles);
            var skin = rest.Select(q => new SurfacePoint { Triangle = 0, Bary = new Vector3(1f, 0f, 0f), Point = new Vector3(q.x, q.y, 0f), Normal = Vector3.forward, Distance = q.z }).ToArray();
            var pairs = new LayerPairs(topology, rest, skin, Enumerable.Repeat(1f, rest.Length).ToArray());
            var rigid = new HashSet<int> { topology.Island[9] };
            var bodyTriangles = new[] { 0, 1, 2 };
            var outer = Enumerable.Range(0, 9).Concat(new[] { copy }).ToArray();
            Assert.That(topology.Group[copy], Is.EqualTo(topology.Group[4]), "the copy is welded to the centre vertex");

            // the inner sheet moved 2 mm out, the outer sheets stayed
            var delta = new Vector3[rest.Length];
            for (var v = 13; v < 53; v++) delta[v] = new Vector3(0f, 0f, 0.002f);
            pairs.Keep(topology, rest, delta, rigid, bodyTriangles, () => Enumerable.Repeat(Vector3.forward, 3).ToArray());

            foreach (var v in outer)
                Assert.That((delta[v] - new Vector3(0f, 0f, 0.002f + LayerPairs.MinGap - 0.001f)).magnitude, Is.LessThan(1e-6f), $"outer vertex {v}: {delta[v].z * 1000f:0.000} mm");
            for (var v = 9; v < 13; v++) Assert.That(DeltaOps.IsZero(delta[v]), Is.True, $"rigid vertex {v} stays");
            for (var v = 13; v < 53; v++) Assert.That(delta[v], Is.EqualTo(new Vector3(0f, 0f, 0.002f)), $"inner vertex {v} stays");

            // nothing moved, but the deformed skin normal tilted 70°: along it the rest gap is 0.34 mm, short of 0.5 mm
            var still = new Vector3[rest.Length];
            var tilted = new Vector3(Mathf.Sin(70f * Mathf.Deg2Rad), 0f, Mathf.Cos(70f * Mathf.Deg2Rad));
            pairs.Keep(topology, rest, still, rigid, bodyTriangles, () => Enumerable.Repeat(tilted, 3).ToArray());
            Assert.That(still.All(DeltaOps.IsZero), Is.True, "a pair none of whose vertices moved keeps exact zeros");
        }

        [Test]
        public void Weld_GivesDuplicatesEqualDeltas_AndFalloffZeroesFarVerticesAfterSmoothing()
        {
            var body = FlatBody("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var sheet = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 11), MeshFactory.Range(-0.02f, 0.02f, 5), 0f);
            sheet.vertices = sheet.vertices.Select(p => new Vector3(p.x, p.y, 0.003f + (p.x + 0.05f) * 0.6f)).ToArray();
            var doubleSided = MeshFactory.DoubleSided(sheet);
            var garment = rig.AddRenderer("Garment", doubleSided, "Hips");
            var settings = TransferSettings.Loose();
            settings.antiClip = false;
            settings.falloffFull = 0.01f;
            settings.falloffZero = 0.03f;

            var raw = Run(body, garment, settings, "Bump", out var correspondence);

            var n = sheet.vertexCount;
            for (var v = 0; v < n; v++) Assert.That(raw[v + n], Is.EqualTo(raw[v]), $"duplicate of {v}");
            Assert.That(raw.Any(d => d.sqrMagnitude > 1e-8f), Is.True, "something moved");
            for (var v = 0; v < raw.Length; v++)
                if (correspondence.Distance[v] > 0.03f) Assert.That(raw[v].sqrMagnitude, Is.EqualTo(0f), $"vertex {v} beyond falloffZero");
        }
    }
}
