using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Writes a garment's output mesh with its TransferData, assigns it, and manages the Blendshape Sync bindings the tool adds.</summary>
    internal static class OutputWriter
    {
        public const string Root = "Assets/BlendshapeTransferGenerated";

        internal sealed class WrittenShape
        {
            public string Name;
            public float[] Weights;
            public Vector3[][] Deltas;
            public Vector3[][] Normals;
            /// <summary>Added to the base positions and normals (the garment at body weight 0, mesh space); null for none.</summary>
            public Vector3[] BaseDelta;
            public Vector3[] BaseNormals;
        }

        public static TransferData DataAt(string path) =>
            string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAllAssetsAtPath(path).OfType<TransferData>().FirstOrDefault();

        /// <summary>The output state in the file at path, or null.</summary>
        public static TransferOutput StateAt(string path) =>
            string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAllAssetsAtPath(path).OfType<TransferOutput>().FirstOrDefault();

        /// <summary>The generated mesh at path, or null.</summary>
        public static Mesh OutputAt(string path) => string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path) as Mesh;

        /// <summary>Records what the output at path was written from; called after the mesh write succeeded. Never Undo-recorded.</summary>
        public static void WriteState(string path, string fingerprint, GarmentReport report)
        {
            var state = StateAt(path);
            if (state == null)
            {
                state = ScriptableObject.CreateInstance<TransferOutput>();
                state.name = "Transfer Output";
                AssetDatabase.AddObjectToAsset(state, path);
            }
            state.fingerprint = fingerprint;
            state.report = report;
            EditorUtility.SetDirty(state);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Root/avatar/renderer-path.asset; a file that holds anything but this source's data gets a numbered sibling.</summary>
        public static string OutputPath(string avatarName, string rendererPath, Mesh source)
        {
            var folder = $"{Root}/{Sanitize(avatarName)}";
            var stem = Sanitize(rendererPath.Replace('/', '_'));
            for (var i = 0; ; i++)
            {
                var path = $"{folder}/{stem}{(i == 0 ? "" : " " + i)}.asset";
                if (AssetDatabase.GetMainAssetTypeAtPath(path) == null) return path;
                var data = DataAt(path);
                if (data != null && data.sourceMesh == source) return path;
            }
        }

        /// <summary>A copy of source with the shapes added after the vendor's (or replacing one of the same name in place), base offset by each shape's BaseDelta.</summary>
        public static Mesh BuildMesh(Mesh source, IReadOnlyList<WrittenShape> shapes)
        {
            var mesh = Object.Instantiate(source);
            mesh.name = source.name + " (Transfer)";
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var hasNormals = normals.Length == vertices.Length;
            foreach (var shape in shapes.Where(s => s.BaseDelta != null))
            {
                for (var v = 0; v < vertices.Length; v++) vertices[v] += shape.BaseDelta[v];
                if (!hasNormals || shape.BaseNormals == null) continue;
                for (var v = 0; v < normals.Length; v++) normals[v] += shape.BaseNormals[v];
            }
            mesh.vertices = vertices;
            if (hasNormals) mesh.normals = normals;

            var replaced = shapes.Where(s => source.GetBlendShapeIndex(s.Name) >= 0).ToDictionary(s => s.Name);
            if (replaced.Count > 0)
            {
                mesh.ClearBlendShapes();
                for (var s = 0; s < source.blendShapeCount; s++)
                {
                    var name = source.GetBlendShapeName(s);
                    if (replaced.TryGetValue(name, out var mine))
                    {
                        Add(mesh, mine);
                        continue;
                    }
                    for (var f = 0; f < source.GetBlendShapeFrameCount(s); f++)
                    {
                        var dv = new Vector3[vertices.Length];
                        var dn = new Vector3[vertices.Length];
                        var dt = new Vector3[vertices.Length];
                        source.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                        mesh.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(s, f), dv, dn, dt);
                    }
                }
            }
            foreach (var shape in shapes)
                if (!replaced.ContainsKey(shape.Name)) Add(mesh, shape);
            return mesh;
        }

        /// <summary>
        /// Writes or overwrites the mesh (GUID kept), stores data and its ShapeEdits as sub-assets, drops edits no longer referenced.
        /// The stored output state matches no fingerprint until WriteState.
        /// </summary>
        public static Mesh Write(Mesh mesh, string path, TransferData data)
        {
            var dataPath = AssetDatabase.GetAssetPath(data);
            if (!string.IsNullOrEmpty(dataPath) && dataPath != path)
                throw new System.InvalidOperationException($"TransferData lives in '{dataPath}' and must be written to that file, not to '{path}'.");
            EnsureFolder(path.Substring(0, path.LastIndexOf('/')));
            // Unity warns on every save of a native asset whose main object is named unlike its file.
            mesh.name = Path.GetFileNameWithoutExtension(path);
            // The new mesh must never sit on disk next to the old mesh's fingerprint: after a crash before WriteState, inputs set back
            // to the old ones would match it. The mesh's save writes the whole file, the cleared fingerprint with it.
            var state = StateAt(path);
            if (state != null)
            {
                state.fingerprint = "";
                EditorUtility.SetDirty(state);
            }
            var asset = MeshAssetWriter.WriteOrOverwrite(mesh, path);
            if (!AssetDatabase.Contains(data))
            {
                data.name = "Transfer Data";
                AssetDatabase.AddObjectToAsset(data, path);
            }
            // A stale recipe must never shadow the current one (DataAt returns the first).
            foreach (var other in AssetDatabase.LoadAllAssetsAtPath(path).OfType<TransferData>().Where(d => d != data).ToList())
            {
                AssetDatabase.RemoveObjectFromAsset(other);
                Object.DestroyImmediate(other, true);
            }
            var live = new HashSet<ShapeEdit>(data.shapes.SelectMany(s => s.frames).Where(e => e != null));
            foreach (var edit in live)
                if (!AssetDatabase.Contains(edit)) AssetDatabase.AddObjectToAsset(edit, path);
            foreach (var stale in AssetDatabase.LoadAllAssetsAtPath(path).OfType<ShapeEdit>().Where(e => !live.Contains(e)).ToList())
            {
                AssetDatabase.RemoveObjectFromAsset(stale);
                Object.DestroyImmediate(stale, true);
            }
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return asset;
        }

        /// <summary>Puts output on the renderer (Undo); weights move by name, transferred shapes take the body's current weights.</summary>
        public static void Assign(SkinnedMeshRenderer garment, Mesh output, SkinnedMeshRenderer body, IEnumerable<string> transferred)
        {
            var weights = WeightsByName(garment);
            Undo.RecordObject(garment, Localization.Get("undo.transfer"));
            garment.sharedMesh = output;
            SetWeights(garment, weights);
            foreach (var name in transferred)
            {
                var local = output.GetBlendShapeIndex(name);
                var remote = body.sharedMesh.GetBlendShapeIndex(name);
                if (local >= 0 && remote >= 0) garment.SetBlendShapeWeight(local, body.GetBlendShapeWeight(remote));
            }
            PrefabUtility.RecordPrefabInstancePropertyModifications(garment);
        }

        /// <summary>Adds a body → garment binding per name unless one with the same source and local name exists; records what it added. No names, no component.</summary>
        public static void Sync(SkinnedMeshRenderer garment, SkinnedMeshRenderer body, IEnumerable<string> names, TransferData data)
        {
            var list = names.ToList();
            if (list.Count == 0) return;
            var sync = garment.GetComponent<ModularAvatarBlendshapeSync>();
            var added = sync == null;
            if (added) sync = Undo.AddComponent<ModularAvatarBlendshapeSync>(garment.gameObject);
            // recorded after AddComponent, which flushes the records taken before it with nothing changed yet
            Undo.RecordObject(data, Localization.Get("undo.transfer"));
            if (added) data.addedSyncComponent = true;
            Undo.RecordObject(sync, Localization.Get("undo.transfer"));
            foreach (var name in list)
            {
                if (sync.Bindings.Any(b => Local(b) == name && b.Blendshape == name && Targets(b, sync, body))) continue;
                sync.Bindings.Add(new BlendshapeBinding { ReferenceMesh = new AvatarObjectReference(body.gameObject), Blendshape = name, LocalBlendshape = name });
                if (!data.addedBindings.Contains(name)) data.addedBindings.Add(name);
            }
            EditorUtility.SetDirty(sync);
            PrefabUtility.RecordPrefabInstancePropertyModifications(sync);
            EditorUtility.SetDirty(data);
        }

        /// <summary>Back to the source mesh; removes only the bindings this tool recorded, and the component if the tool added it and it is now empty.</summary>
        public static void Restore(SkinnedMeshRenderer garment, SkinnedMeshRenderer body, TransferData data)
        {
            var weights = WeightsByName(garment);
            Undo.RecordObject(garment, Localization.Get("undo.restore"));
            garment.sharedMesh = data.sourceMesh;
            SetWeights(garment, weights);
            PrefabUtility.RecordPrefabInstancePropertyModifications(garment);
            var sync = garment.GetComponent<ModularAvatarBlendshapeSync>();
            var removed = false;
            if (sync != null)
            {
                Undo.RecordObject(sync, Localization.Get("undo.restore"));
                sync.Bindings.RemoveAll(b => data.addedBindings.Contains(Local(b)) && b.Blendshape == Local(b) && Targets(b, sync, body));
                removed = sync.Bindings.Count == 0 && data.addedSyncComponent;
                if (removed) Undo.DestroyObjectImmediate(sync);
                else PrefabUtility.RecordPrefabInstancePropertyModifications(sync);
            }
            // recorded after DestroyObjectImmediate, which flushes the records taken before it
            Undo.RecordObject(data, Localization.Get("undo.restore"));
            if (removed) data.addedSyncComponent = false;
            data.addedBindings.Clear();
            EditorUtility.SetDirty(data);
        }

        /// <summary>
        /// The TransferData of a garment: on the generated mesh it shows (also when it lost its source mesh, so the caller can say so),
        /// unless that is another avatar's and this avatar has its own for the renderer; or else (transferred, never saved, maybe renamed
        /// since) one under Root whose source is the renderer's mesh, this avatar's first, preferring the same renderer path. Data from
        /// another avatar's folder (a duplicated avatar) comes back as an unsaved copy, edits copied, without that renderer's bindings,
        /// so writing it makes a file of this avatar's own.
        /// </summary>
        public static TransferData FindData(SkinnedMeshRenderer garment, string avatarName, string rendererPath)
        {
            var found = Locate(garment, avatarName, rendererPath);
            if (found == null || InFolder(found, avatarName)) return found;
            var copy = Copy(found);
            copy.addedBindings.Clear();
            copy.addedSyncComponent = false;
            foreach (var entry in copy.shapes)
                for (var f = 0; f < entry.frames.Count; f++)
                    if (entry.frames[f] != null) entry.frames[f] = Copy(entry.frames[f]);
            return copy;
        }

        /// <summary>Skinned renderers under the avatar (inactive too) whose transfer data FindData finds in this avatar's folder.</summary>
        public static IEnumerable<SkinnedMeshRenderer> WithSavedData(Transform avatar) =>
            avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => Locate(r, avatar.name, AvatarPaths.RelativePath(avatar, r.transform)) is TransferData d && InFolder(d, avatar.name))
                .ToList();

        /// <summary>FindData's search, without copying data that lives in another avatar's folder.</summary>
        private static TransferData Locate(SkinnedMeshRenderer garment, string avatarName, string rendererPath)
        {
            var mesh = garment.sharedMesh;
            if (mesh == null) return null;
            var shown = DataAt(AssetDatabase.GetAssetPath(mesh));
            if (shown != null && shown.sourceMesh != mesh)
            {
                if (shown.sourceMesh == null || InFolder(shown, avatarName)) return shown;
                // a duplicated avatar still showing the original's output: its own data, once a Transfer wrote it, holds its settings
                return DataFor(shown.sourceMesh).FirstOrDefault(d => InFolder(d, avatarName) && d.rendererPath == rendererPath) ?? shown;
            }
            var candidates = DataFor(mesh);
            return Pick(candidates.Where(d => InFolder(d, avatarName)).ToList()) ?? Pick(candidates);

            TransferData Pick(List<TransferData> list) => list.FirstOrDefault(d => d.rendererPath == rendererPath) ?? (list.Count == 1 ? list[0] : null);
        }

        /// <summary>Every TransferData under Root whose source is the mesh.</summary>
        private static List<TransferData> DataFor(Mesh source) =>
            !AssetDatabase.IsValidFolder(Root)
                ? new List<TransferData>()
                : AssetDatabase.FindAssets("t:Mesh", new[] { Root }).Select(AssetDatabase.GUIDToAssetPath).Distinct().Select(DataAt)
                    .Where(d => d != null && d.sourceMesh == source).ToList();

        private static bool InFolder(TransferData data, string avatarName) => AssetDatabase.GetAssetPath(data).StartsWith($"{Root}/{Sanitize(avatarName)}/");

        private static T Copy<T>(T original) where T : Object
        {
            var copy = Object.Instantiate(original);
            copy.name = original.name;
            return copy;
        }

        private static void Add(Mesh mesh, WrittenShape shape)
        {
            for (var f = 0; f < shape.Weights.Length; f++) mesh.AddBlendShapeFrame(shape.Name, shape.Weights[f], shape.Deltas[f], shape.Normals[f], null);
        }

        private static string Local(BlendshapeBinding b) => string.IsNullOrWhiteSpace(b.LocalBlendshape) ? b.Blendshape : b.LocalBlendshape;

        private static bool Targets(BlendshapeBinding b, Component container, SkinnedMeshRenderer body) =>
            b.ReferenceMesh != null && b.ReferenceMesh.Get(container) == body.gameObject;

        private static Dictionary<string, float> WeightsByName(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var result = new Dictionary<string, float>();
            if (mesh == null) return result;
            for (var i = 0; i < mesh.blendShapeCount; i++) result[mesh.GetBlendShapeName(i)] = renderer.GetBlendShapeWeight(i);
            return result;
        }

        private static void SetWeights(SkinnedMeshRenderer renderer, Dictionary<string, float> weights)
        {
            var mesh = renderer.sharedMesh;
            for (var i = 0; i < mesh.blendShapeCount; i++)
                renderer.SetBlendShapeWeight(i, weights.TryGetValue(mesh.GetBlendShapeName(i), out var w) ? w : 0f);
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var slash = folder.LastIndexOf('/');
            EnsureFolder(folder.Substring(0, slash));
            AssetDatabase.CreateFolder(folder.Substring(0, slash), folder.Substring(slash + 1));
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "_" : name;
        }
    }
}
