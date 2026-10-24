using System.Linq;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class OutputWriterTests
    {
        private const string Avatar = "BT Writer Test";
        private const string Temp = "Assets/BlendshapeTransferTests_tmp";
        private Rig rig;
        private bool rootExisted;

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

        private static Mesh SourceWithShapes()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(0f, 0.1f, 3), MeshFactory.Range(0f, 0.1f, 3), 0f);
            MeshFactory.AddShape(mesh, "Bump", _ => Vector3.right * 0.001f);
            MeshFactory.AddShape(mesh, "VendorBump", _ => Vector3.up * 0.001f);
            AssetDatabase.CreateAsset(mesh, $"{Temp}/Source.asset");
            return mesh;
        }

        private static OutputWriter.WrittenShape Shape(string name, int count, params float[] weights) => new OutputWriter.WrittenShape
        {
            Name = name,
            Weights = weights,
            Deltas = weights.Select(w => Enumerable.Repeat(Vector3.forward * w * 0.0001f, count).ToArray()).ToArray(),
            Normals = weights.Select(_ => new Vector3[count]).ToArray(),
        };

        [Test]
        public void BuildMesh_AppendsNewShapesWithAllFramesAndReplacesInPlace()
        {
            var source = SourceWithShapes();
            var shapes = new[] { Shape("Bump", 9, 100f), Shape("Multi", 9, 50f, 100f) };

            var mesh = OutputWriter.BuildMesh(source, shapes);

            Assert.That(Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName), Is.EqualTo(new[] { "Bump", "VendorBump", "Multi" }));
            var deltas = new Vector3[9];
            mesh.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
            Assert.That(deltas[0], Is.EqualTo(Vector3.forward * 0.01f), "Bump is ours now");
            Assert.That(mesh.GetBlendShapeFrameCount(2), Is.EqualTo(2));
            Assert.That(mesh.GetBlendShapeFrameWeight(2, 0), Is.EqualTo(50f));
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void BuildMesh_AddsTheBaseOffsetToPositionsAndNormals()
        {
            var source = SourceWithShapes();
            var shape = Shape("Belly", 9, 100f);
            shape.BaseDelta = Enumerable.Range(0, 9).Select(v => new Vector3(0f, 0f, -0.001f * v)).ToArray();
            shape.BaseNormals = Enumerable.Range(0, 9).Select(v => new Vector3(0.01f * v, 0f, 0f)).ToArray();
            var mesh = OutputWriter.BuildMesh(source, new[] { shape });

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var sourceVertices = source.vertices;
            var sourceNormals = source.normals;
            for (var v = 0; v < 9; v++)
            {
                Assert.That((vertices[v] - sourceVertices[v] - shape.BaseDelta[v]).magnitude, Is.LessThan(1e-7f));
                Assert.That((normals[v] - sourceNormals[v] - shape.BaseNormals[v]).magnitude, Is.LessThan(1e-7f));
            }
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void Write_KeepsTheGuidAndTheDataAcrossRewrites()
        {
            var source = SourceWithShapes();
            var data = ScriptableObject.CreateInstance<TransferData>();
            data.sourceMesh = source;
            var path = OutputWriter.OutputPath(Avatar, "Outfit/Top", source);

            var first = OutputWriter.Write(OutputWriter.BuildMesh(source, new[] { Shape("Belly", 9, 100f) }), path, data);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var second = OutputWriter.Write(OutputWriter.BuildMesh(source, new[] { Shape("Belly", 9, 100f), Shape("Hips", 9, 100f) }), path, data);

            Assert.That(path, Is.EqualTo($"{OutputWriter.Root}/{Avatar}/Outfit_Top.asset"));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(second.GetInstanceID(), Is.EqualTo(first.GetInstanceID()));
            Assert.That(second.blendShapeCount, Is.EqualTo(4));
            Assert.That(OutputWriter.DataAt(path), Is.EqualTo(data));
            Assert.That(OutputWriter.OutputPath(Avatar, "Outfit/Top", source), Is.EqualTo(path), "same source, same file");
        }

        [Test]
        public void Write_ReplacesAStaleDataInTheFile()
        {
            var source = SourceWithShapes();
            var a = ScriptableObject.CreateInstance<TransferData>();
            a.sourceMesh = source;
            var path = OutputWriter.OutputPath(Avatar, "Outfit/Top", source);
            OutputWriter.Write(OutputWriter.BuildMesh(source, new[] { Shape("Belly", 9, 100f) }), path, a);

            var b = ScriptableObject.CreateInstance<TransferData>();
            b.sourceMesh = source;
            OutputWriter.Write(OutputWriter.BuildMesh(source, new[] { Shape("Belly", 9, 100f) }), path, b);

            Assert.That(OutputWriter.DataAt(path), Is.EqualTo(b));
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<TransferData>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void Write_RejectsDataFromAnotherFile()
        {
            var source = SourceWithShapes();
            var data = ScriptableObject.CreateInstance<TransferData>();
            data.sourceMesh = source;
            var path = OutputWriter.OutputPath(Avatar, "Outfit/Top", source);
            OutputWriter.Write(OutputWriter.BuildMesh(source, new[] { Shape("Belly", 9, 100f) }), path, data);

            Assert.Throws<System.InvalidOperationException>(() =>
                OutputWriter.Write(OutputWriter.BuildMesh(source, new[] { Shape("Belly", 9, 100f) }), $"{OutputWriter.Root}/{Avatar}/Other.asset", data));
        }

        [Test]
        public void SyncAndRestore_TouchOnlyTheToolsBindings()
        {
            var source = SourceWithShapes();
            var bodyMesh = MeshFactory.Grid(MeshFactory.Range(0f, 0.1f, 2), MeshFactory.Range(0f, 0.1f, 2), 0f);
            MeshFactory.AddShape(bodyMesh, "Bump", _ => Vector3.forward);
            var body = rig.AddRenderer("Body", bodyMesh, "Hips");
            var garment = rig.AddRenderer("Top", source, "Hips");
            var sync = garment.gameObject.AddComponent<ModularAvatarBlendshapeSync>();
            sync.Bindings.Add(new BlendshapeBinding { ReferenceMesh = new AvatarObjectReference(body.gameObject), Blendshape = "Bump", LocalBlendshape = "VendorBump" });
            var data = ScriptableObject.CreateInstance<TransferData>();
            data.sourceMesh = source;
            var output = OutputWriter.BuildMesh(source, new[] { Shape("Bump", 9, 100f) });

            OutputWriter.Assign(garment, output, body, new[] { "Bump" });
            OutputWriter.Sync(garment, body, new[] { "Bump" }, data);
            OutputWriter.Sync(garment, body, new[] { "Bump" }, data);

            Assert.That(garment.sharedMesh, Is.SameAs(output));
            Assert.That(sync.Bindings.Count(b => b.Blendshape == "Bump" && b.LocalBlendshape == "Bump"), Is.EqualTo(1), "added once, despite MA's Equals ignoring the local name");
            Assert.That(sync.Bindings.Count, Is.EqualTo(2));

            OutputWriter.Restore(garment, body, data);

            Assert.That(garment.sharedMesh, Is.SameAs(source));
            Assert.That(sync.Bindings.Single().LocalBlendshape, Is.EqualTo("VendorBump"));
            Assert.That(garment.GetComponent<ModularAvatarBlendshapeSync>(), Is.Not.Null, "the owner's component stays");
            Object.DestroyImmediate(output);
        }

        [Test]
        public void SyncAndRestore_UndoTheirRecordsWithTheSceneChanges()
        {
            var source = SourceWithShapes();
            var bodyMesh = MeshFactory.Grid(MeshFactory.Range(0f, 0.1f, 2), MeshFactory.Range(0f, 0.1f, 2), 0f);
            MeshFactory.AddShape(bodyMesh, "Bump", _ => Vector3.forward);
            var body = rig.AddRenderer("Body", bodyMesh, "Hips");
            var garment = rig.AddRenderer("Top", source, "Hips");
            var data = ScriptableObject.CreateInstance<TransferData>();
            data.sourceMesh = source;

            Undo.IncrementCurrentGroup();
            // never an empty group: PerformUndo would undo whatever came before it
            Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
            OutputWriter.Sync(garment, body, new[] { "Bump" }, data);
            // as at the end of the frame, before the owner could press Ctrl+Z
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.That(garment.GetComponent<ModularAvatarBlendshapeSync>(), Is.Null);
            Assert.That(data.addedBindings, Is.Empty, "undoing Sync forgets the binding it recorded");
            Assert.That(data.addedSyncComponent, Is.False);

            Undo.IncrementCurrentGroup();
            OutputWriter.Sync(garment, body, new[] { "Bump" }, data);
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
            Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
            OutputWriter.Restore(garment, body, data);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.That(garment.GetComponent<ModularAvatarBlendshapeSync>().Bindings.Count, Is.EqualTo(1));
            Assert.That(data.addedBindings, Is.EqualTo(new[] { "Bump" }), "undoing Restore brings the record back with the binding");
            Assert.That(data.addedSyncComponent, Is.True);
            Object.DestroyImmediate(data);
        }
    }
}
