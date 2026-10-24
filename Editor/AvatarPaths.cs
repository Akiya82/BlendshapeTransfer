using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// The avatar root of an object and paths under it, by NDMF's rule: the outermost root, inactive objects included. Stored
    /// renderer paths and MA's object references depend on the same rule.
    /// </summary>
    internal static class AvatarPaths
    {
        // NDMF's generic root, matched by name so that the tool needs no reference to NDMF
        private const string NdmfAvatarRoot = "nadena.dev.ndmf.runtime.components.NDMFAvatarRoot";

        public static Transform RootOf(Transform t)
        {
            Transform root = null;
            for (; t != null; t = t.parent)
                if (IsRoot(t)) root = t;
            return root;
        }

        private static bool IsRoot(Transform t)
        {
            if (t.TryGetComponent<VRCAvatarDescriptor>(out _)) return true;
            foreach (var c in t.GetComponents<MonoBehaviour>())
                if (c != null && c.GetType().FullName == NdmfAvatarRoot) return true;
            return false;
        }

        /// <summary>Names from below root down to child, joined with '/'; "" for the root itself, null when child is not under root.</summary>
        public static string RelativePath(Transform root, Transform child)
        {
            if (root == child) return "";
            var segments = new List<string>();
            while (child != root && child != null)
            {
                segments.Add(child.gameObject.name);
                child = child.parent;
            }
            if (child == null && root != null) return null;
            segments.Reverse();
            return string.Join("/", segments);
        }
    }
}
