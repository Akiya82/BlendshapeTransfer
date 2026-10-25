using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class SessionTests
    {
        private const string Avatar = "BT Session Test";
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

        private static Mesh OutputOf(GarmentState g) => OutputWriter.OutputAt(AssetDatabase.GetAssetPath(g.Data));

        private SkinnedMeshRenderer Body(string shape, System.Func<Vector3, Vector3> delta)
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, shape, delta);
            return rig.AddRenderer("Body", Saved(mesh, "Body"), "Hips");
        }

        private SkinnedMeshRenderer Garment(string name, float z, float half = 0.05f) =>
            rig.AddRenderer(name, Saved(MeshFactory.Grid(MeshFactory.Range(-half, half, 21), MeshFactory.Range(-half, half, 21), z), name), "Hips");

        private static void Quiet(TransferSettings s)
        {
            s.antiClip = false;
            s.smoothIterations = 0;
        }

        /// <summary>Positions, normals, then every shape frame's position and normal deltas, for exact comparison of two outputs.</summary>
        private static List<Vector3> Dump(Mesh mesh)
        {
            var all = mesh.vertices.Concat(mesh.normals).ToList();
            for (var s = 0; s < mesh.blendShapeCount; s++)
            for (var f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
            {
                var dv = new Vector3[mesh.vertexCount];
                var dn = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(s, f, dv, dn, null);
                all.AddRange(dv);
                all.AddRange(dn);
            }
            return all;
        }

        [Test]
        public void Open_TicksShapesThatReachTheGarment_AndOrdersLayersFromTheSkin()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var outer = Garment("Outer", 0.006f);
            var inner = Garment("Inner", 0.002f);

            var session = TransferSession.Open(body, new[] { outer, inner }, errors);

            Assert.That(errors, Is.Empty);
            Assert.That(session.Garments.Select(g => g.Renderer), Is.EqualTo(new[] { inner, outer }));
            Assert.That(session.Garments[0].Data.Entry("Bump").enabled, Is.True);
            Assert.That(session.Garments[0].Reach["Bump"], Is.EqualTo(0.01f).Within(0.0005f));
        }

        [Test]
        public void Open_RejectsAnEmptyGarmentList()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));

            Assert.That(TransferSession.Open(body, new SkinnedMeshRenderer[0], errors), Is.Null);
            Assert.That(errors.Select(e => e.Key), Is.EqualTo(new[] { Msg.NoGarments }));
        }

        [Test]
        public void Open_RefusesInPlayMode()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var playing = TransferSession.InPlayMode;
            TransferSession.InPlayMode = () => true;
            try
            {
                Assert.That(TransferSession.Open(body, new[] { garment }, errors), Is.Null);
                Assert.That(errors.Select(e => e.Key), Is.EqualTo(new[] { Msg.PlayMode }));
            }
            finally
            {
                TransferSession.InPlayMode = playing;
            }
        }

        [Test]
        public void Open_RejectsRenderersInAPrefabAsset()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var asset = PrefabUtility.SaveAsPrefabAsset(rig.Root, $"{Temp}/Avatar.prefab");
            var assetBody = asset.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            var assetGarment = asset.transform.Find("Top").GetComponent<SkinnedMeshRenderer>();

            Assert.That(TransferSession.Open(body, new[] { assetGarment }, errors), Is.Null);
            Assert.That(errors.Select(e => e.ToString()), Is.EqualTo(new[] { $"{Msg.InAsset}(Top)" }));
            Assert.That(TransferSession.Open(assetBody, new[] { garment }, errors), Is.Null);
            Assert.That(errors.Select(e => e.ToString()), Is.EqualTo(new[] { $"{Msg.InAsset}(Body)" }));
        }

        [Test]
        public void Transfer_KeepsHandEditsAcrossAPresetChange()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var state = session.Garments[0];
            Quiet(state.Data.settings);
            Assert.That(session.Transfer(null), Is.True);

            var n = garment.sharedMesh.vertexCount;
            var edit = ScriptableObject.CreateInstance<ShapeEdit>();
            edit.mask = Enumerable.Repeat(1f, n).ToArray();
            edit.mask[220] = 0f;
            edit.sculpt = new Vector3[n];
            edit.sculpt[221] = new Vector3(0f, 0f, 0.005f);
            state.Data.Entry("Bump").frames.Add(edit);
            state.Data.settings = TransferSettings.Loose();
            Quiet(state.Data.settings);
            Assert.That(session.Transfer(null), Is.True);

            var result = state.Results["Bump"];
            var auto = MeshSpace.ToMesh(state.Snapshot.Skin, result.AutoWorld[0]);
            Assert.That(result.FinalMesh[0][220], Is.EqualTo(Vector3.zero));
            Assert.That((result.FinalMesh[0][221] - auto[221] - new Vector3(0f, 0f, 0.005f)).magnitude, Is.LessThan(1e-7f));
            Assert.That(AssetDatabase.Contains(edit), Is.True, "the edit is stored with the output");
        }

        [Test]
        public void Open_DropsHandEditsWhenTheSourceMeshChanged()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var data = session.Garments[0].Data;
            data.Entry("Bump").frames.Add(ScriptableObject.CreateInstance<ShapeEdit>());
            session.Transfer(null);
            data.sourceHash = "stale";

            var reopened = TransferSession.Open(body, new[] { garment }, errors);

            Assert.That(reopened.Garments[0].Data, Is.EqualTo(data), "found again although never saved onto the renderer");
            Assert.That(data.Entry("Bump").frames, Is.Empty);
            Assert.That(reopened.Garments[0].Warnings.Any(w => w.Key == Msg.EditsDropped), Is.True);
        }

        [Test]
        public void PoseChanged_SeesARotatedBone()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var session = TransferSession.Open(body, new[] { Garment("Top", 0.002f) }, errors);
            Assert.That(session.PoseChanged(), Is.False);
            rig.Bones["Hips"].localRotation = Quaternion.Euler(0f, 1f, 0f);
            Assert.That(session.PoseChanged(), Is.True);
        }

        [Test]
        public void Save_GarmentInARotatedScaledMeshSpaceLandsOnTheWorldTarget()
        {
            var body = Body("Lift", _ => new Vector3(0f, 0f, 0.01f));
            var world = MeshFactory.Range(-0.03f, 0.03f, 7).SelectMany(y => MeshFactory.Range(-0.03f, 0.03f, 7).Select(x => new Vector3(x, y, 0.003f))).ToArray();
            var meshToWorld = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 90f, 30f), Vector3.one * 2f);
            var garmentMesh = MeshFactory.Grid(MeshFactory.Range(-0.03f, 0.03f, 7), MeshFactory.Range(-0.03f, 0.03f, 7), 0.003f);
            garmentMesh.vertices = world.Select(p => meshToWorld.inverse.MultiplyPoint3x4(p)).ToArray();
            var garment = rig.AddRenderer("Top", Saved(garmentMesh, "Top"), "Chest");
            garmentMesh.bindposes = new[] { rig.Bones["Chest"].worldToLocalMatrix * meshToWorld };
            var session = TransferSession.Open(body, new[] { garment }, errors);
            session.Garments[0].Data.settings = TransferSettings.Loose();
            Quiet(session.Garments[0].Data.settings);

            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);

            garment.SetBlendShapeWeight(garment.sharedMesh.GetBlendShapeIndex("Lift"), 100f);
            var baked = new Mesh();
            garment.BakeMesh(baked, true);
            var local = baked.vertices;
            for (var v = 0; v < world.Length; v++)
            {
                var actual = garment.transform.localToWorldMatrix.MultiplyPoint3x4(local[v]);
                Assert.That((actual - world[v] - new Vector3(0f, 0f, 0.01f)).magnitude, Is.LessThan(0.00005f), $"vertex {v}");
            }
            Object.DestroyImmediate(baked);
        }

        [Test]
        public void Transfer_DoesNotComputeAShapeTheGarmentKeeps()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var innerMesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f);
            MeshFactory.AddShape(innerMesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            var inner = rig.AddRenderer("Inner", Saved(innerMesh, "Inner"), "Hips");
            var outer = Garment("Outer", 0.006f);
            var session = TransferSession.Open(body, new[] { inner, outer }, errors);
            var state = session.Garments[0];
            Assert.That(state.Renderer, Is.EqualTo(inner));
            Assert.That(state.Data.Entry("Bump").enabled, Is.True);

            Assert.That(session.Transfer(null), Is.True);

            Assert.That(state.Results.ContainsKey("Bump"), Is.False, "the outer layer must clip against the vendor shape the inner one keeps");
            Assert.That(state.Report.Skipped.Any(s => s.name == "Bump" && s.reason == Msg.SkipKept), Is.True);
        }

        [Test]
        public void PoseChanged_SeesAChangedBodyWeight()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var session = TransferSession.Open(body, new[] { Garment("Top", 0.002f) }, errors);
            Assert.That(session.PoseChanged(), Is.False);
            body.SetBlendShapeWeight(0, 30f);
            Assert.That(session.PoseChanged(), Is.True);
        }

        [Test]
        public void Save_BodyWeightAtOpenShowsTheVendorMesh()
        {
            var body = Body("Lift", _ => new Vector3(0f, 0f, 0.01f));
            body.SetBlendShapeWeight(0, 40f);
            var garment = Garment("Top", 0.007f); // 3 mm above the skin at weight 40
            var source = garment.sharedMesh;
            var session = TransferSession.Open(body, new[] { garment }, errors);

            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);

            var output = garment.sharedMesh;
            Assert.That(output, Is.Not.SameAs(source));
            var deltas = new Vector3[output.vertexCount];
            output.GetBlendShapeFrameVertices(output.GetBlendShapeIndex("Lift"), 0, deltas, null, null);
            var vertices = output.vertices;
            var vendor = source.vertices;
            for (var v = 0; v < vertices.Length; v++)
            {
                Assert.That((vertices[v] - vendor[v] - new Vector3(0f, 0f, -0.004f)).magnitude, Is.LessThan(0.00001f), $"base of vertex {v}");
                Assert.That((deltas[v] - new Vector3(0f, 0f, 0.01f)).magnitude, Is.LessThan(0.00001f), $"frame of vertex {v}");
            }
        }

        [Test]
        public void Transfer_KeepsClearanceAtBodyWeightZeroWhenOpenedAtAnother()
        {
            // opened at 40 the skin lies 4 mm low; at 0 it rises into fabric that does not follow it by itself
            var body = Body("Sink", _ => new Vector3(0f, 0f, -0.01f));
            body.SetBlendShapeWeight(0, 40f);
            var garment = Garment("Top", -0.001f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var state = session.Garments[0];
            state.Data.settings.falloffFull = 0.0005f;
            state.Data.settings.falloffZero = 0.001f;

            Assert.That(session.Transfer(null), Is.True);

            foreach (var p in OutputOf(state).vertices) Assert.That(p.z, Is.GreaterThan(0.001f - 0.00001f), "the skin at weight 0 lies at z = 0, clearance 1 mm");
        }

        [Test]
        public void Open_ReportsAGeneratedMeshWhoseDataLostTheSource()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);
            session.Garments[0].Data.sourceMesh = null;

            Assert.That(TransferSession.Open(body, new[] { garment }, errors), Is.Null);
            Assert.That(errors.Any(e => e.Key == Msg.LostSource), Is.True, string.Join("\n", errors));
        }

        [Test]
        public void Open_ReportsTwoGarmentsWithTheSameData()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var mesh = Saved(MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f), "Top");
            var a = rig.AddRenderer("Top A", mesh, "Hips");
            var b = rig.AddRenderer("Top B", mesh, "Hips");
            Assert.That(TransferSession.Open(body, new[] { a }, errors).Transfer(null), Is.True);

            Assert.That(TransferSession.Open(body, new[] { a, b }, errors), Is.Null, "B on the same source finds A's data by scanning");
            Assert.That(errors.Any(e => e.Key == Msg.SameData && e.Args.Contains("Top A") && e.Args.Contains("Top B")), Is.True, string.Join("\n", errors));
        }

        [Test]
        public void Open_ReportsTwoGarmentsWithTheSamePath()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var a = Garment("Top", 0.002f);
            var b = Garment("Top B", 0.006f);
            b.gameObject.name = "Top";

            Assert.That(TransferSession.Open(body, new[] { a, b }, errors), Is.Null);
            Assert.That(errors.Any(e => e.Key == Msg.SamePath && e.Args.SequenceEqual(new object[] { "Top", "Top", "Top" })), Is.True, string.Join("\n", errors));
        }

        [Test]
        public void Save_LeavesACollisionOnlyGarmentOnItsSourceWithoutSync()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var source = garment.sharedMesh;
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var data = session.Garments[0].Data;
            foreach (var e in data.shapes) e.enabled = false;

            Assert.That(session.Transfer(null), Is.True);
            session.Save(true);
            OutputWriter.Sync(garment, body, new string[0], data);

            Assert.That(garment.sharedMesh, Is.SameAs(source));
            Assert.That(garment.GetComponent<ModularAvatarBlendshapeSync>(), Is.Null);
        }

        [Test]
        public void Reach_CountsOnlyGarmentVerticesWithinFalloffZero()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var session = TransferSession.Open(body, new[] { Garment("Top", 0.1f) }, errors);
            var state = session.Garments[0];
            Assert.That(state.Reach["Bump"], Is.EqualTo(0f), "10 cm from the skin, beyond falloffZero 6 cm");

            state.Data.settings.falloffZero = 0.2f;
            Assert.That(session.Transfer(null), Is.True);

            Assert.That(state.Reach["Bump"], Is.EqualTo(0.01f).Within(0.0005f));
        }

        [Test]
        public void Reopen_KeepsTheBodyWeightsTheGarmentWasFittedAt()
        {
            var body = Body("Belly", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);
            var first = Dump(garment.sharedMesh);

            body.SetBlendShapeWeight(0, 70f);
            var reopened = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(reopened.BodyWeight("Belly"), Is.EqualTo(0f), "the snapshot shows the body as the garment was fitted to it");
            Assert.That(reopened.PoseChanged(), Is.False);
            Assert.That(reopened.AllUpToDate(), Is.True, "nothing the output depends on changed");
            Assert.That(reopened.Transfer(true, null).Completed, Is.True);

            Assert.That(Dump(OutputOf(reopened.Garments[0])), Is.EqualTo(first), "same base, same frames");
            Assert.That(reopened.Garments[0].Data.baseWeights.Single().weight, Is.EqualTo(0f), "the record is kept");
            var info = reopened.BaseWeightInfo(reopened.Garments[0]).Single();
            Assert.That(info.Key, Is.EqualTo(Msg.FitWeight));
            Assert.That(info.Args, Is.EqualTo(new object[] { "Belly", 0f, 70f }));
        }

        [Test]
        public void Open_RejectsGarmentsFittedAtDifferentBodyWeights()
        {
            var body = Body("Belly", MeshFactory.Bump(0.01f, 0.04f));
            var top = Garment("Top", 0.002f);
            var skirt = Garment("Skirt", 0.012f);
            Assert.That(TransferSession.Open(body, new[] { top }, errors).Transfer(null), Is.True);
            body.SetBlendShapeWeight(0, 70f);
            Assert.That(TransferSession.Open(body, new[] { skirt }, errors).Transfer(null), Is.True);

            Assert.That(TransferSession.Open(body, new[] { top, skirt }, errors), Is.Null);
            Assert.That(errors.Any(e => e.Key == Msg.FitConflict && e.Args.Contains("Belly") && e.Args.Contains("Top") && e.Args.Contains("Skirt") && e.Args.Contains(0f) && e.Args.Contains(70f)), Is.True, string.Join("\n", errors));
        }

        [Test]
        public void Refit_LetsTheNextOpenTakeTheLiveBodyWeights()
        {
            var body = Body("Belly", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            Assert.That(TransferSession.Open(body, new[] { garment }, errors).Transfer(null), Is.True);
            body.SetBlendShapeWeight(0, 70f);
            var fitted = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(fitted.BodyWeight("Belly"), Is.EqualTo(0f));

            fitted.Refit(fitted.Garments[0]);
            var refitted = TransferSession.Open(body, new[] { garment }, errors);

            Assert.That(refitted.BodyWeight("Belly"), Is.EqualTo(70f));
            Assert.That(refitted.Transfer(null), Is.True);
            Assert.That(refitted.Garments[0].Data.baseWeights.Single().weight, Is.EqualTo(70f));
        }

        [Test]
        public void Transfer_RecordsTheFitWeightsForUndo()
        {
            var body = Body("Belly", MeshFactory.Bump(0.01f, 0.04f));
            var session = TransferSession.Open(body, new[] { Garment("Top", 0.002f) }, errors);
            var data = session.Garments[0].Data;

            Undo.IncrementCurrentGroup();
            // never an empty group: PerformUndo would undo whatever came before it
            Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
            Assert.That(session.Transfer(null), Is.True);
            Assert.That(data.baseWeights, Is.Not.Empty);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.That(data.baseWeights, Is.Empty);
        }

        [Test]
        public void Reorder_IsUndone_AndTheGarmentsFollowTheRestoredLayers()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            var session = TransferSession.Open(body, new[] { inner, outer }, errors);
            Assert.That(session.Garments.Select(g => g.Renderer), Is.EqualTo(new[] { inner, outer }));
            var data = session.Garments.Select(g => g.Data).ToArray();

            Undo.IncrementCurrentGroup();
            // never an empty group: PerformUndo would undo whatever came before it
            Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
            session.Reorder(new[] { outer, inner });
            Assert.That(data.Select(d => d.layerIndex), Is.EqualTo(new[] { 1, 0 }));
            Assert.That(session.Garments.Select(g => g.Renderer), Is.EqualTo(new[] { outer, inner }));
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.That(data.Select(d => d.layerIndex), Is.EqualTo(new[] { 0, 1 }), "the layer indices are back");
            Assert.That(session.FollowLayerIndices(), Is.True);
            Assert.That(session.Garments.Select(g => g.Renderer), Is.EqualTo(new[] { inner, outer }));
            Assert.That(session.FollowLayerIndices(), Is.False, "already in layer order");
        }

        [Test]
        public void Save_OverwrittenVendorShapeKeepsTheGarmentsWeightInTheBase()
        {
            var body = Body("Chest_Up", MeshFactory.Bump(0.01f, 0.04f));
            body.SetBlendShapeWeight(0, 50f);
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.008f);
            MeshFactory.AddShape(mesh, "Chest_Up", _ => new Vector3(0f, 0f, 0.002f));
            var garment = rig.AddRenderer("Bikini", Saved(mesh, "Bikini"), "Hips");
            garment.SetBlendShapeWeight(0, 50f);
            var source = mesh.vertices;
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var state = session.Garments[0];
            Quiet(state.Data.settings);
            state.Data.Entry("Chest_Up").overwrite = true;

            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);

            var output = garment.sharedMesh;
            var shape = output.GetBlendShapeIndex("Chest_Up");
            Assert.That(garment.GetBlendShapeWeight(shape), Is.EqualTo(50f));
            var frame = new Vector3[output.vertexCount];
            output.GetBlendShapeFrameVertices(shape, 0, frame, null, null);
            var vertices = output.vertices;
            var count = 0;
            for (var v = 0; v < vertices.Length; v++)
            {
                // the body shape moves no skin under these, so the transfer response is zero there
                if (new Vector2(source[v].x, source[v].y).magnitude < 0.055f) continue;
                count++;
                Assert.That((vertices[v] + frame[v] * 0.5f - source[v] - new Vector3(0f, 0f, 0.001f)).magnitude, Is.LessThan(1e-6f), $"vertex {v}: source + vendor(50)");
            }
            Assert.That(count, Is.GreaterThan(20));

            // the garment's weight now follows the body (MA Sync); a second transfer still starts from the weight it was fitted at
            var first = Dump(output);
            body.SetBlendShapeWeight(0, 80f);
            garment.SetBlendShapeWeight(shape, 80f);
            var reopened = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(reopened.AllUpToDate(), Is.True, "nothing the output depends on changed");
            Assert.That(reopened.Transfer(true, null).Completed, Is.True);
            Assert.That(Dump(OutputOf(reopened.Garments[0])), Is.EqualTo(first));
        }

        [Test]
        public void Open_OnADuplicatedAvatarStartsFromACopyOfTheData()
        {
            const string other = Avatar + " B";
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var original = session.Garments[0].Data;
            original.Entry("Bump").frames.Add(ScriptableObject.CreateInstance<ShapeEdit>());
            Assert.That(session.Transfer(null), Is.True);
            session.Save(true);
            var originalPath = AssetDatabase.GetAssetPath(original);
            var originalBytes = File.ReadAllBytes(originalPath);
            var copy = Object.Instantiate(rig.Root);
            copy.name = other;
            try
            {
                var reopened = TransferSession.Open(copy.transform.Find("Body").GetComponent<SkinnedMeshRenderer>(),
                    new[] { copy.transform.Find("Top").GetComponent<SkinnedMeshRenderer>() }, errors);
                Assert.That(errors, Is.Empty);
                var data = reopened.Garments[0].Data;
                Assert.That(data, Is.Not.SameAs(original), "the copy shows the original's generated mesh");
                Assert.That(data.addedBindings, Is.Empty, "the original's bindings are not this renderer's record");
                data.settings.smoothIterations = 7;

                Assert.That(reopened.Transfer(null), Is.True);

                var path = AssetDatabase.GetAssetPath(data);
                Assert.That(path, Does.StartWith($"{OutputWriter.Root}/{other}/"));
                Assert.That(AssetDatabase.GetAssetPath(data.Entry("Bump").frames[0]), Is.EqualTo(path), "the copy's edits live in its own file");
                Assert.That(File.ReadAllBytes(originalPath), Is.EqualTo(originalBytes), "the original asset is untouched");
            }
            finally
            {
                AssetDatabase.DeleteAsset($"{OutputWriter.Root}/{other}");
            }
        }

        [Test]
        public void Open_OnADuplicatedAvatarTransferredWithoutSave_UsesItsOwnData()
        {
            const string other = Avatar + " C";
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Top", 0.002f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(true);
            var copy = Object.Instantiate(rig.Root);
            copy.name = other;
            try
            {
                var copyBody = copy.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                var copyTop = copy.transform.Find("Top").GetComponent<SkinnedMeshRenderer>();
                var first = TransferSession.Open(copyBody, new[] { copyTop }, errors);
                Assert.That(errors, Is.Empty);
                first.Garments[0].Data.settings.smoothIterations = 7;
                // not saved: the copy still shows the original's generated mesh
                Assert.That(first.Transfer(null), Is.True);
                var path = AssetDatabase.GetAssetPath(first.Garments[0].Data);
                Assert.That(path, Does.StartWith($"{OutputWriter.Root}/{other}/"));

                // a new read, without the window's carried data
                var again = TransferSession.Open(copyBody, new[] { copyTop }, errors);
                Assert.That(errors, Is.Empty);
                var data = again.Garments[0].Data;
                Assert.That(AssetDatabase.GetAssetPath(data), Is.EqualTo(path), "its own data, not a new copy of the original's");
                Assert.That(data.settings.smoothIterations, Is.EqualTo(7));
                Assert.That(again.AllUpToDate(), Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset($"{OutputWriter.Root}/{other}");
            }
        }

        [Test]
        public void Save_WithSyncBindsAShapeTheGarmentKeeps()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f);
            MeshFactory.AddShape(mesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            var inner = rig.AddRenderer("Inner", Saved(mesh, "Inner"), "Hips");
            var session = TransferSession.Open(body, new[] { inner }, errors);
            var data = session.Garments[0].Data;
            Assert.That(data.Entry("Bump").enabled, Is.True);

            Assert.That(session.Transfer(null), Is.True);
            session.Save(true);

            var sync = inner.GetComponent<ModularAvatarBlendshapeSync>();
            Assert.That(sync, Is.Not.Null, "outer layers were fitted assuming the kept shape follows the body");
            Assert.That(sync.Bindings.Count(b => b.Blendshape == "Bump" && b.LocalBlendshape == "Bump"), Is.EqualTo(1));
            Assert.That(data.addedBindings, Is.EqualTo(new[] { "Bump" }));
            Assert.That(inner.sharedMesh, Is.SameAs(mesh), "nothing written, the garment keeps its mesh");

            // the kept shape now follows the body; a reopen still snapshots it at the weight the garment was fitted at
            body.SetBlendShapeWeight(0, 70f);
            inner.SetBlendShapeWeight(0, 70f);
            var reopened = TransferSession.Open(body, new[] { inner }, errors);
            Assert.That(reopened.Garments[0].Snapshot.Weights[0], Is.EqualTo(0f));
        }

        [Test]
        public void CanRestore_AGarmentThatKeepsItsMeshButGotBindings()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f);
            MeshFactory.AddShape(mesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            var inner = rig.AddRenderer("Inner", Saved(mesh, "Inner"), "Hips");
            var session = TransferSession.Open(body, new[] { inner }, errors);
            var state = session.Garments[0];
            Assert.That(session.CanRestore(state), Is.False, "no data on disk yet");

            Assert.That(session.Transfer(null), Is.True);
            session.Save(true);

            Assert.That(inner.sharedMesh, Is.SameAs(mesh));
            Assert.That(session.CanRestore(state), Is.True, "the binding Save added is Restore's to remove");
            session.Restore(state);
            Assert.That(inner.GetComponent<ModularAvatarBlendshapeSync>(), Is.Null);
            Assert.That(session.CanRestore(state), Is.False);
        }

        [Test]
        public void WithSavedData_FindsTheGarmentsTransferredOnThisAvatarOnly()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var top = Garment("Top", 0.002f);
            Garment("Skirt", 0.012f);
            Assert.That(TransferSession.Open(body, new[] { top }, errors).Transfer(null), Is.True);
            var copy = Object.Instantiate(rig.Root);
            copy.name = Avatar + " B";

            Assert.That(OutputWriter.WithSavedData(rig.Root.transform), Is.EqualTo(new[] { top }));
            Assert.That(OutputWriter.WithSavedData(copy.transform), Is.Empty, "the copy's data lives in the original avatar's folder");
        }

        [Test]
        public void Reopen_OnTheGeneratedMeshTransfersTheSameAndSyncsNoDuplicates()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            body.SetBlendShapeWeight(0, 40f);
            var garment = Garment("Top", 0.006f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(true);
            var output = garment.sharedMesh;
            var path = AssetDatabase.GetAssetPath(output);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var first = Dump(output);
            var sync = garment.GetComponent<ModularAvatarBlendshapeSync>();
            var bindings = sync.Bindings.Count;

            var reopened = TransferSession.Open(body, new[] { garment }, errors);
            Assert.That(errors, Is.Empty);
            var state = reopened.Garments[0];
            Assert.That(state.Data, Is.SameAs(session.Garments[0].Data));
            Assert.That(state.Snapshot.Source, Is.Not.SameAs(output), "starts from the source, not the generated mesh");
            Assert.That(reopened.AllUpToDate(), Is.True, "nothing the output depends on changed");
            Assert.That(reopened.Transfer(true, null).Completed, Is.True);
            reopened.Save(true);

            Assert.That(AssetDatabase.GetAssetPath(OutputOf(state)), Is.EqualTo(path));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(Dump(garment.sharedMesh), Is.EqualTo(first));
            Assert.That(sync.Bindings.Count, Is.EqualTo(bindings), "no duplicate bindings");
            Assert.That(state.Data.addedBindings, Is.EqualTo(new[] { "Bump" }));
        }

        [Test]
        public void Transfer_NeverWritesASeparatorShape_EvenTickedInOldData()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            // a separator made "from mix" in Blender carries deltas
            MeshFactory.AddShape(mesh, "===== Group =====", MeshFactory.Bump(0.01f, 0.04f));
            var body = rig.AddRenderer("Body", Saved(mesh, "Body"), "Hips");
            var session = TransferSession.Open(body, new[] { Garment("Top", 0.002f) }, errors);
            var data = session.Garments[0].Data;
            Assert.That(data.Entry("===== Group ====="), Is.Null, "not ticked by default");
            data.GetOrAddEntry("===== Group =====").enabled = true;

            Assert.That(session.Transfer(null), Is.True);

            Assert.That(session.Garments[0].Report.Written, Is.EqualTo(new[] { "Bump" }));
            Assert.That(data.baseWeights.Select(b => b.name), Does.Not.Contain("===== Group ====="), "no fit record for a separator");
        }

        [Test]
        public void Reach_CoversEveryFrame_AShapeWhoseLastFrameIsZeroIsTicked()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, "Two", MeshFactory.Bump(0.004f, 0.04f), 50f);
            MeshFactory.AddShape(mesh, "Two", _ => Vector3.zero, 100f);
            var body = rig.AddRenderer("Body", Saved(mesh, "Body"), "Hips");

            var session = TransferSession.Open(body, new[] { Garment("Top", 0.002f) }, errors);

            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            var g = session.Garments[0];
            Assert.That(g.Reach["Two"], Is.GreaterThan(0.0035f), "its middle frame moves the skin 4 mm; reach also sets the anti-clip band");
            Assert.That(g.Data.Entry("Two")?.enabled, Is.True, "ticked by default");
        }

        [Test]
        public void Reach_FromABodyWeightOtherThanZero_CoversWeightZeroAndTheFrame()
        {
            var body = Body("Lift", _ => new Vector3(0f, 0f, 0.01f));
            body.SetBlendShapeWeight(0, 70f);
            // 2 mm above the skin as the body shows it at 70
            var session = TransferSession.Open(body, new[] { Garment("Top", 0.009f) }, errors);
            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            Assert.That(session.Garments[0].Reach["Lift"], Is.EqualTo(0.007f).Within(0.0002f), "to weight 0 the skin moves 7 mm, to the frame 3 mm");
        }

        [Test]
        public void NotWornWith_LeavesTheListedGarmentOutOfTheColliders()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var session = TransferSession.Open(body, new[] { Garment("Shorts", 0.002f), Garment("Skirt", 0.006f) }, errors);
            var delta = session.Surface.WorldDelta(0, 0);
            Assert.That(session.Layers(1, "Bump", 0, delta).Count, Is.EqualTo(2), "the body and the shorts");

            session.Garments[1].Data.notWornWith.Add(session.Garments[0].Data.rendererPath);

            Assert.That(session.Layers(1, "Bump", 0, delta).Count, Is.EqualTo(1), "the body alone");
        }

        [Test]
        public void NotWornWith_IsMutual()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var session = TransferSession.Open(body, new[] { Garment("Shorts", 0.002f), Garment("Skirt", 0.006f) }, errors);
            var delta = session.Surface.WorldDelta(0, 0);

            session.Garments[0].Data.notWornWith.Add(session.Garments[1].Data.rendererPath);

            Assert.That(session.Layers(1, "Bump", 0, delta).Count, Is.EqualTo(1), "the body alone");
        }

        [Test]
        public void NotWornWith_FollowsARenamedGarment()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var shorts = Garment("Shorts", 0.002f);
            var skirt = Garment("Skirt", 0.006f);
            var session = TransferSession.Open(body, new[] { shorts, skirt }, errors);
            session.Garments[1].Data.notWornWith.Add(session.Garments[0].Data.rendererPath);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(false);

            shorts.gameObject.name = "Shorts 2";
            var reopened = TransferSession.Open(body, new[] { shorts, skirt }, errors);
            Assert.That(reopened, Is.Not.Null, string.Join("\n", errors));
            Assert.That(reopened.Garments[0].Renderer, Is.EqualTo(shorts));
            Assert.That(reopened.Garments[1].Data.notWornWith, Is.EqualTo(new[] { "Shorts 2" }));
            Assert.That(reopened.Collides(1, 0), Is.False);
        }

        [Test]
        public void Open_KeepsTheSettingsOfDataThatIsNotOnDisk_WhenItIsCarried()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garments = new[] { Garment("Shorts", 0.002f), Garment("Skirt", 0.006f) };
            var first = TransferSession.Open(body, garments, errors);
            first.Garments[1].Data.settings.smoothIterations = 11;
            first.Garments[1].Data.notWornWith.Add(first.Garments[0].Data.rendererPath);
            var carried = first.Garments.ToDictionary(g => g.Renderer, g => g.Data);

            var again = TransferSession.Open(body, garments, errors, carried);
            var fresh = TransferSession.Open(body, garments, errors);

            Assert.That(errors, Is.Empty);
            Assert.That(again.Garments[1].Data.settings.smoothIterations, Is.EqualTo(11));
            Assert.That(again.Garments[1].Data.notWornWith, Is.EqualTo(first.Garments[1].Data.notWornWith));
            Assert.That(fresh.Garments[1].Data.settings.smoothIterations, Is.Not.EqualTo(11), "without carrying, the settings are new");
        }

        [Test]
        public void Open_IgnoresCarriedDataOfAnotherMesh()
        {
            var body = Body("Bump", MeshFactory.Bump(0.01f, 0.04f));
            var garment = Garment("Skirt", 0.006f);
            var first = TransferSession.Open(body, new[] { garment }, errors);
            first.Garments[0].Data.settings.smoothIterations = 11;
            first.Garments[0].Data.sourceMesh = Garment("Other", 0.01f).sharedMesh;

            var again = TransferSession.Open(body, new[] { garment }, errors, new Dictionary<SkinnedMeshRenderer, TransferData> { [garment] = first.Garments[0].Data });

            Assert.That(again.Garments[0].Data.settings.smoothIterations, Is.Not.EqualTo(11));
        }

        /// <summary>A body grid 0.5 mm apart with a nub 3 mm in radius and 5 mm high at its centre, and fabric 5 mm apart 2 mm above it whose vertices nearest the nub sit 3.5 mm from its axis.</summary>
        private (SkinnedMeshRenderer Body, SkinnedMeshRenderer Garment) Nub()
        {
            var bodyMesh = MeshFactory.Grid(MeshFactory.Range(-0.02f, 0.02f, 81), MeshFactory.Range(-0.02f, 0.02f, 81), 0f);
            MeshFactory.AddShape(bodyMesh, "Nub", MeshFactory.Bump(0.005f, 0.003f));
            var body = rig.AddRenderer("Body", Saved(bodyMesh, "Body"), "Hips");
            var fabric = MeshFactory.Grid(MeshFactory.Range(-0.0175f, 0.0175f, 8), MeshFactory.Range(-0.0175f, 0.0175f, 8), 0.002f);
            return (body, rig.AddRenderer("Top", Saved(fabric, "Top"), "Hips"));
        }

        [Test]
        public void Transfer_LiftsSparseFabricOverANubBetweenItsVertices()
        {
            var (body, garment) = Nub();
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var g = session.Garments[0];
            g.Data.GetOrAddEntry("Nub").enabled = true;

            Assert.That(session.Transfer(null), Is.True);

            var bodyDelta = session.BodyOffset("Nub", 100f);
            var result = g.Results["Nub"];
            Assert.That(AntiClip.SkinOutside(session.Surface.Positions, bodyDelta, g.Collider, g.SkinCandidates, result.FinalWorld[0], AntiClip.MinBand), Is.Empty,
                "no skin ends outside the fabric");
            var tip = new Vector3(0f, 0f, 0.005f);
            Assert.That(g.Collider.Hit(g.Collider.Deformed(result.FinalWorld[0], 1f), tip, 0.05f, out var hit), Is.True);
            Assert.That(SurfaceQuery.Signed(tip, hit), Is.LessThan(-0.0009f), "the tip stays under the fabric by about the 1 mm clearance");
            Assert.That(g.Report.Clip.StillSkin, Is.EqualTo(0));
            Assert.That(g.Report.Clip.Pushed, Is.GreaterThan(0), "no fabric vertex is inside the skin, so the pushes are the skin side's");
        }

        [Test]
        public void Transfer_CountsSkinThroughFabricAntiClipLeftAlone()
        {
            var (body, garment) = Nub();
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var g = session.Garments[0];
            g.Data.settings.antiClip = false;

            Assert.That(session.Transfer(null), Is.True);

            Assert.That(g.Report.Clip.StillSkin, Is.GreaterThan(0), "the nub ends outside the fabric at full weight");
            Assert.That(g.Report.Clip.Intermediate, Is.GreaterThan(0), "and halfway, 2.5 mm over fabric 2 mm up");
            Assert.That(g.Report.Clip.Pushed, Is.EqualTo(0));
        }

        [Test]
        public void Transfer_KeepsFabricThatTouchedTheSkin_AFloorOverSkinAShapeLifts()
        {
            var bodyMesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(bodyMesh, "Rise", _ => new Vector3(0f, 0f, 0.005f));
            var body = rig.AddRenderer("Body", Saved(bodyMesh, "Body"), "Hips");
            var garment = Garment("Bikini", 0.0001f);
            var session = TransferSession.Open(body, new[] { garment }, errors);
            var g = session.Garments[0];

            Assert.That(session.Transfer(null), Is.True);

            var centre = new Vector3(0f, 0f, 0.005f);
            Assert.That(g.Collider.Hit(g.Collider.Deformed(g.Results["Rise"].FinalWorld[0], 1f), centre, 0.05f, out var hit), Is.True);
            Assert.That(SurfaceQuery.Signed(centre, hit), Is.LessThan(-0.00019f), "0.1 mm at rest, at least 0.2 mm over the lifted skin");
        }

        [Test]
        public void Reach_TakesTheSkinUnderTheGarment_SoANubBetweenItsVerticesIsTicked()
        {
            var (body, garment) = Nub();

            var session = TransferSession.Open(body, new[] { garment }, errors);

            Assert.That(session.Garments[0].Reach["Nub"], Is.EqualTo(0.005f).Within(0.0002f));
            Assert.That(session.Garments[0].Data.Entry("Nub")?.enabled, Is.True, "ticked at the first read");
        }
    }
}
