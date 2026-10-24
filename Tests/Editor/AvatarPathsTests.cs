using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class AvatarPathsTests
    {
        private Rig rig;
        private Transform outer, inner, a, b, loose;

        [SetUp]
        public void SetUp()
        {
            rig = new Rig();
            outer = Make("Outer", null).transform;
            outer.gameObject.AddComponent<VRCAvatarDescriptor>();
            inner = Make("Inner", outer).transform;
            inner.gameObject.AddComponent<VRCAvatarDescriptor>();
            a = Make("A", inner).transform;
            b = Make("B", a).transform;
            loose = Make("Loose", null).transform;
        }

        [TearDown]
        public void TearDown() => rig.Dispose();

        private GameObject Make(string name, Transform parent)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, rig.Scene);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        [Test]
        public void RootOf_IsTheOutermostDescriptor_FromTheObjectItself()
        {
            Assert.That(AvatarPaths.RootOf(b), Is.SameAs(outer));
            Assert.That(AvatarPaths.RootOf(inner), Is.SameAs(outer));
            Assert.That(AvatarPaths.RootOf(outer), Is.SameAs(outer));
            Assert.That(AvatarPaths.RootOf(loose), Is.Null);
            Assert.That(AvatarPaths.RootOf(null), Is.Null);
        }

        [Test]
        public void RootOf_IgnoresWhetherObjectsAreActive()
        {
            a.gameObject.SetActive(false);
            outer.gameObject.SetActive(false);
            Assert.That(AvatarPaths.RootOf(b), Is.SameAs(outer));
        }

        [Test]
        public void RootOf_CountsNdmfsGenericAvatarRoot()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot")).FirstOrDefault(t => t != null);
            if (type == null) Assert.Ignore("NDMF is not loaded");
            var wrapper = Make("Wrapper", null);
            wrapper.AddComponent(type);
            outer.SetParent(wrapper.transform, false);
            Assert.That(AvatarPaths.RootOf(b), Is.SameAs(wrapper.transform));
        }

        [Test]
        public void RelativePath_AsNdmf()
        {
            Assert.That(AvatarPaths.RelativePath(outer, outer), Is.EqualTo(""));
            Assert.That(AvatarPaths.RelativePath(outer, inner), Is.EqualTo("Inner"));
            Assert.That(AvatarPaths.RelativePath(outer, b), Is.EqualTo("Inner/A/B"));
            Assert.That(AvatarPaths.RelativePath(inner, b), Is.EqualTo("A/B"));
            Assert.That(AvatarPaths.RelativePath(inner, outer), Is.Null);
            Assert.That(AvatarPaths.RelativePath(outer, loose), Is.Null);
        }
    }
}
