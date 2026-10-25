using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    /// <summary>The session's side of the Edit preview: one shape computed on demand, result stamps, offsets and clipping at any weight.</summary>
    public sealed class PreviewSessionTests
    {
        private const string Avatar = "BT Preview Session Test";
        private const string Temp = "Assets/BlendshapeTransferTests_tmp";
        private Rig rig;
        private bool rootExisted;
        private readonly List<Message> errors = new List<Message>();

        [SetUp]
        public void SetUp()
        {
            rig = new Rig(Avatar);
            rootExisted = AssetDatabase.IsValidFolder(OutputWriter.Root);
            if (!AssetDatabase.IsValidFolder(Temp)) AssetDatabase.CreateFolder("Assets", "BlendshapeTransferTests_tmp");
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            AssetDatabase.DeleteAsset(Temp);
            AssetDatabase.DeleteAsset(rootExisted ? $"{OutputWriter.Root}/{Avatar}" : OutputWriter.Root);
        }

        private static Mesh Saved(Mesh mesh, string name)
        {
            AssetDatabase.CreateAsset(mesh, $"{Temp}/{name}.asset");
            return mesh;
        }

        /// <summary>A flat body with a 1 cm bump under each named shape ("Bump" when none is named).</summary>
        private SkinnedMeshRenderer Body(params string[] shapes)
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            foreach (var shape in shapes.DefaultIfEmpty("Bump")) MeshFactory.AddShape(mesh, shape, MeshFactory.Bump(0.01f, 0.04f));
            return rig.AddRenderer("Body", Saved(mesh, "Body"), "Hips");
        }

        private SkinnedMeshRenderer Garment(string name, float z) =>
            rig.AddRenderer(name, Saved(MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), z), name), "Hips");

        private TransferSession Read(SkinnedMeshRenderer body, params SkinnedMeshRenderer[] garments)
        {
            var session = TransferSession.Open(body, garments, errors);
            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            return session;
        }

        private static void AssertSame(ShapeResult actual, ShapeResult expected, string what)
        {
            Assert.That(actual.BaseWorld, Is.EqualTo(expected.BaseWorld), what + ": base");
            Assert.That(actual.FinalMesh.Length, Is.EqualTo(expected.FinalMesh.Length), what + ": frames");
            for (var f = 0; f < expected.FinalMesh.Length; f++)
            {
                Assert.That(actual.FinalMesh[f], Is.EqualTo(expected.FinalMesh[f]), $"{what}: frame {f}");
                Assert.That(actual.NormalMesh[f], Is.EqualTo(expected.NormalMesh[f]), $"{what}: normals of frame {f}");
            }
        }

        [Test]
        public void EnsureShape_ComputesWhatTransferComputes_LowerGarmentFirst_RecordingNothing()
        {
            var body = Body();
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            var transferred = Read(body, inner, outer);
            // the outer garment stays put beyond its falloff while the inner one rises into it, so anti-clip against the inner result
            // shapes it (Clamped keeps zero at or above full, so full goes down first)
            transferred.Garments[1].Data.settings.falloffFull = 0.002f;
            transferred.Garments[1].Data.settings.falloffZero = 0.004f;
            Assert.That(transferred.Transfer(null), Is.True);
            var session = Read(body, inner, outer);
            Assert.That(session.Garments.Select(g => g.Results.Count), Is.All.EqualTo(0), "a new read holds no results");
            var files = session.Garments.Select(g => File.ReadAllBytes(AssetDatabase.GetAssetPath(g.Data))).ToList();
            var records = session.Garments.Select(g => g.Data.baseWeights.Count + g.Data.garmentWeights.Count).ToList();
            var entries = session.Garments.Select(g => g.Data.shapes.Count).ToList();

            session.EnsureShape("Bump");

            var f = session.Fingerprints();
            for (var k = 0; k < 2; k++)
            {
                var g = session.Garments[k];
                Assert.That(g.Results["Bump"].Fingerprint, Is.EqualTo(f[k]), g.Renderer.name + ": stamped with its current fingerprint");
                // the outer garment equals Transfer's only if it clipped against the inner one's result, so the inner one came first;
                // the settings came from disk with the outputs
                AssertSame(g.Results["Bump"], transferred.Garments[k].Results["Bump"], g.Renderer.name);
            }
            Assert.That(session.Garments.Select(g => File.ReadAllBytes(AssetDatabase.GetAssetPath(g.Data))), Is.EqualTo(files), "no file written");
            Assert.That(session.Garments.Select(g => g.Data.baseWeights.Count + g.Data.garmentWeights.Count), Is.EqualTo(records), "no fit record added");
            Assert.That(session.Garments.Select(g => g.Data.shapes.Count), Is.EqualTo(entries), "no shape entry added");
        }

        [Test]
        public void EnsureShape_AfterAFalloffZeroChange_ComputesWhatTransferThenWrites()
        {
            var body = Body();
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            Assert.That(Read(body, inner, outer).Transfer(null), Is.True);
            var session = Read(body, inner, outer);
            // the reach, and with it the anti-clip band, follows falloffZero; 4 mm leaves the outer garment beyond it (Clamped keeps
            // zero at or above full, so full goes down first)
            var settings = session.Garments[1].Data.settings;
            settings.falloffFull = 0.002f;
            settings.falloffZero = 0.004f;

            session.EnsureShape("Bump");
            var preview = session.Garments[1].Results["Bump"];
            var run = session.Transfer(false, null);

            Assert.That(run.Written, Is.EqualTo(new[] { session.Garments[1] }));
            Assert.That(session.Garments[1].Results["Bump"], Is.Not.SameAs(preview), "Transfer computed it again");
            AssertSame(session.Garments[1].Results["Bump"], preview, "Outer");
        }

        [Test]
        public void AChangedSetting_MakesTheShapeStaleOnItAndAbove_NotBelow_AndSoDoesItsUndo()
        {
            var session = Read(Body(), Garment("Low", 0.002f), Garment("Mid", 0.004f), Garment("High", 0.006f));
            session.EnsureShape("Bump");
            var mid = session.Garments[1];
            Assert.That(mid.Renderer.name, Is.EqualTo("Mid"));

            Undo.IncrementCurrentGroup();
            // never an empty group: PerformUndo would undo whatever came before it
            Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
            Undo.RecordObject(mid.Data, "Smoothing");
            mid.Data.settings.smoothIterations = 7;
            Undo.FlushUndoRecordObjects();
            session.DropStale(session.Fingerprints());

            Assert.That(session.Garments.Select(g => g.Results.ContainsKey("Bump")), Is.EqualTo(new[] { true, false, false }));

            session.EnsureShape("Bump");
            Undo.PerformUndo();
            Assert.That(mid.Data.settings.smoothIterations, Is.Not.EqualTo(7), "undone");
            session.DropStale(session.Fingerprints());

            Assert.That(session.Garments.Select(g => g.Results.ContainsKey("Bump")), Is.EqualTo(new[] { true, false, false }));
        }

        [Test]
        public void AfterSingleShapes_TransferStillComputesALowerGarmentAsACollider()
        {
            var body = Body("Bump", "Bump2");
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            Assert.That(Read(body, inner, outer).Transfer(null), Is.True);
            var session = Read(body, inner, outer);
            session.EnsureShape("Bump");
            Assert.That(session.Garments[0].Results.Keys, Is.EquivalentTo(new[] { "Bump" }));
            session.Garments[1].Data.settings.smoothIterations = 7;

            var run = session.Transfer(false, null);

            Assert.That(run.Written, Is.EqualTo(new[] { session.Garments[1] }), "the inner garment is not written");
            Assert.That(session.Garments[0].Results.Keys, Is.EquivalentTo(new[] { "Bump", "Bump2" }), "but computed whole as a collider");
            Assert.That(session.HasCurrentResults(session.Garments[0], session.Fingerprints()[0]), Is.True);
        }

        [Test]
        public void AShapeTheLowerGarmentStopsTicking_IsNoLongerUsedAboveIt()
        {
            var body = Body();
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            var session = Read(body, inner, outer);
            session.EnsureShape("Bump");
            session.Garments[0].Data.Entry("Bump").enabled = false;

            session.EnsureShape("Bump");

            Assert.That(session.Garments[0].Results.ContainsKey("Bump"), Is.False, "dropped: the inner garment no longer writes it");
            var reference = Read(body, inner, outer);
            reference.Garments[0].Data.Entry("Bump").enabled = false;
            reference.EnsureShape("Bump");
            AssertSame(session.Garments[1].Results["Bump"], reference.Garments[1].Results["Bump"], "Outer clips against nothing there");
        }

        [Test]
        public void ClippingAt_SeesALowerLayerOnlyAtTheIntermediateWeight()
        {
            var session = Read(Body(), Garment("Inner", 0.002f), Garment("Outer", 0.006f));
            var lower = session.Garments[0];
            var n = lower.Snapshot.Positions.Length;
            // the inner sheet rises 8 mm by weight 50 and is back by 100: the outer one, 4 mm above it, is inside it only in between
            var up = Enumerable.Repeat(new Vector3(0f, 0f, 0.008f), n).ToArray();
            lower.Results["Bump"] = new ShapeResult
            {
                Name = "Bump", FrameWeights = new[] { 50f, 100f }, BaseWorld = new Vector3[n],
                FinalWorld = new[] { up, new Vector3[n] }, NormalMesh = new[] { new Vector3[n], new Vector3[n] },
                Fingerprint = session.Fingerprints()[0],
            };

            Assert.That(session.ClippingAt(1, "Bump", 0f).Layer, Is.Empty);
            Assert.That(session.ClippingAt(1, "Bump", 50f).Layer, Is.Not.Empty);
            Assert.That(session.ClippingAt(1, "Bump", 100f).Layer, Is.Empty);
            Assert.That(session.ClippingAt(1, "Bump", 50f).Skin, Is.Empty, "the skin rises 5 mm at 50, under the outer sheet at 6 mm");
            // every candidate sits 4 mm under the risen sheet at 50: the dots are those vertices, not candidate indices
            Assert.That(session.ClippingAt(1, "Bump", 50f).Layer, Is.EquivalentTo(session.Garments[1].LayerCandidates[lower].Vertices));
            Assert.That(session.ClippingAt(1, "Bump", 100f).Skin, Is.Not.Empty, "the skin rises 10 mm at 100, through the outer sheet at 6 mm");
        }

        private static Mesh Grid(float z, float half) =>
            MeshFactory.Grid(MeshFactory.Range(-half, half, 21), MeshFactory.Range(-half, half, 21), z);

        /// <summary>UVs from x and y, then tangents from them, so the preview's tangents are checked too.</summary>
        private static Mesh WithTangents(Mesh mesh)
        {
            mesh.uv = mesh.vertices.Select(p => new Vector2(p.x, p.y)).ToArray();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>A shape frame with the normal change its positions make, as an imported shape with calculated normals has.</summary>
        private static void AddShapeWithNormals(Mesh mesh, string name, System.Func<Vector3, Vector3> delta, float weight = 100f)
        {
            var moved = Object.Instantiate(mesh);
            try
            {
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var dv = vertices.Select(delta).ToArray();
                moved.vertices = vertices.Select((p, i) => p + dv[i]).ToArray();
                moved.RecalculateNormals();
                var dn = moved.normals.Select((m, i) => m - normals[i]).ToArray();
                mesh.AddBlendShapeFrame(name, weight, dv, dn, null);
            }
            finally
            {
                Object.DestroyImmediate(moved);
            }
        }

        private static void AssertMatchesBake(SkinnedMeshRenderer renderer, PreviewMesh preview, string what)
        {
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked, true);
                var m = renderer.transform.localToWorldMatrix;
                var vertices = baked.vertices;
                var normals = baked.normals;
                var tangents = baked.tangents;
                var pv = preview.Mesh.vertices;
                var pn = preview.Mesh.normals;
                var pt = preview.Mesh.tangents;
                Assert.That(pv.Length, Is.EqualTo(vertices.Length), what);
                for (var v = 0; v < vertices.Length; v++)
                {
                    Assert.That((m.MultiplyPoint3x4(vertices[v]) - pv[v]).magnitude, Is.LessThan(1e-5f), $"{what}: position {v}");
                    Assert.That((m.MultiplyVector(normals[v]).normalized - pn[v]).magnitude, Is.LessThan(1e-5f), $"{what}: normal {v}");
                    Assert.That((m.MultiplyVector(tangents[v]).normalized - (Vector3)pt[v]).magnitude, Is.LessThan(1e-5f), $"{what}: tangent {v}");
                    Assert.That(pt[v].w, Is.EqualTo(tangents[v].w), $"{what}: tangent sign {v}");
                }
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        /// <summary>
        /// Sets the shape to w on the body and on every saved garment (MA Sync does that in game), then compares each preview mesh at
        /// w with the renderer's own skinning.
        /// </summary>
        private static void AssertPreviewAt(TransferSession session, string shape, float w)
        {
            var body = session.Body;
            body.SetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex(shape), w);
            using (var preview = new PreviewMesh(body, session.Surface.Mesh, session.Surface.Skin, session.Surface.Positions, session.Surface.Weights))
            {
                preview.Update(session.BodyOffset(shape, w), session.BodyNormalOffset(shape, w));
                AssertMatchesBake(body, preview, $"Body at {w}");
            }
            foreach (var g in session.Garments)
            {
                var index = g.Renderer.sharedMesh.GetBlendShapeIndex(shape);
                if (index >= 0) g.Renderer.SetBlendShapeWeight(index, w);
                using (var preview = new PreviewMesh(g.Renderer, g.Snapshot.Source, g.Snapshot.Skin, g.Snapshot.Positions, g.Snapshot.Weights))
                {
                    preview.Update(session.GarmentOffset(g, shape, w), session.GarmentNormalOffset(g, shape, w));
                    AssertMatchesBake(g.Renderer, preview, $"{g.Renderer.name} at {w}");
                }
            }
        }

        [Test]
        public void Preview_MatchesTheSavedOutput_ForABodyOpenedAtAnotherWeight()
        {
            var bodyMesh = WithTangents(Grid(0f, 0.1f));
            AddShapeWithNormals(bodyMesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            var body = rig.AddRenderer("Body", Saved(bodyMesh, "Body"), "Hips");
            body.SetBlendShapeWeight(0, 30f);
            var top = rig.AddRenderer("Top", Saved(WithTangents(Grid(0.004f, 0.05f)), "Top"), "Hips");
            // a placed, turned avatar: world space no longer equals mesh space
            rig.Root.transform.SetPositionAndRotation(new Vector3(0.3f, 1f, -0.2f), Quaternion.Euler(10f, 40f, 0f));
            var session = Read(body, top);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);

            AssertPreviewAt(session, "Bump", 70f);
        }

        [Test]
        public void Preview_MatchesTheSavedOutput_ForATwoFrameShape()
        {
            var bodyMesh = WithTangents(Grid(0f, 0.1f));
            var bump = MeshFactory.Bump(0.01f, 0.04f);
            MeshFactory.AddShape(bodyMesh, "Two", bump, 50f);
            MeshFactory.AddShape(bodyMesh, "Two", p => bump(p) * 0.4f, 100f);
            var body = rig.AddRenderer("Body", Saved(bodyMesh, "Body"), "Hips");
            var top = rig.AddRenderer("Top", Saved(WithTangents(Grid(0.004f, 0.05f)), "Top"), "Hips");
            var session = Read(body, top);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);

            AssertPreviewAt(session, "Two", 25f);
            AssertPreviewAt(session, "Two", 75f);
        }

        [Test]
        public void Preview_MatchesTheSavedOutput_ForAnOverwrittenVendorShape()
        {
            var bodyMesh = WithTangents(Grid(0f, 0.1f));
            AddShapeWithNormals(bodyMesh, "Chest_Up", MeshFactory.Bump(0.01f, 0.04f));
            var body = rig.AddRenderer("Body", Saved(bodyMesh, "Body"), "Hips");
            body.SetBlendShapeWeight(0, 50f);
            var bikiniMesh = WithTangents(Grid(0.008f, 0.05f));
            // a vendor shape that turns the normals too, kept in the output's base at the garment's weight
            var count = bikiniMesh.vertexCount;
            bikiniMesh.AddBlendShapeFrame("Chest_Up", 100f, Enumerable.Repeat(new Vector3(0f, 0f, 0.002f), count).ToArray(), Enumerable.Repeat(new Vector3(0.2f, 0f, 0f), count).ToArray(), null);
            var bikini = rig.AddRenderer("Bikini", Saved(bikiniMesh, "Bikini"), "Hips");
            bikini.SetBlendShapeWeight(0, 50f);
            var session = Read(body, bikini);
            session.Garments[0].Data.Entry("Chest_Up").overwrite = true;
            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);

            AssertPreviewAt(session, "Chest_Up", 80f);
        }
    }
}
