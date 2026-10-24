using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace Akiya82.BlendshapeTransfer.Tests
{
    /// <summary>
    /// Runs a loaded NUnit test assembly by reflection, one test per editor update, for when the Test Runner refuses a dirty scene.
    /// One fixture instance per class as in NUnit: [OneTimeSetUp] before its first test, [SetUp]/[TearDown] around each [Test] or
    /// [TestCase], [OneTimeTearDown] at the end. Writes "queued" plus progress while running and, at the end, one PASS/FAIL line
    /// per test and "done N/M passed".
    /// </summary>
    public static class TestJob
    {
        public static void Start(string assemblyName, string filter, string outputPath)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);
            if (assembly == null)
            {
                File.WriteAllText(outputPath, $"done 0/0 passed: assembly {assemblyName} is not loaded\n");
                return;
            }
            var tests = new List<(Type Type, MethodInfo Method, object[] Arguments, string Name)>();
            foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
            foreach (var method in type.GetMethods())
            {
                if (method.GetCustomAttributes(typeof(TestAttribute), true).Length > 0) tests.Add((type, method, null, $"{type.Name}.{method.Name}"));
                foreach (TestCaseAttribute testCase in method.GetCustomAttributes(typeof(TestCaseAttribute), true))
                    tests.Add((type, method, testCase.Arguments, $"{type.Name}.{method.Name}({string.Join(", ", testCase.Arguments)})"));
            }
            tests = tests.Where(t => string.IsNullOrEmpty(filter) || t.Name.Contains(filter)).ToList();
            var fixtures = new Dictionary<Type, object>();
            var brokenFixtures = new Dictionary<Type, string>();
            var lines = new List<string>();
            var next = 0;
            File.WriteAllText(outputPath, "queued\n");

            void Step()
            {
                if (next >= tests.Count)
                {
                    EditorApplication.update -= Step;
                    foreach (var kv in fixtures)
                    {
                        try { Invoke<OneTimeTearDownAttribute>(kv.Key, kv.Value); }
                        catch (Exception e) { lines.Add($"ONETIMETEARDOWN FAIL {kv.Key.Name}: {Inner(e).Message}"); }
                    }
                    var passed = lines.Count(l => l.StartsWith("PASS ") && !lines.Any(t => t.StartsWith("TEARDOWN FAIL " + l.Substring(5) + ":")));
                    var teardownFailures = lines.Count(l => l.StartsWith("ONETIMETEARDOWN FAIL"));
                    lines.Add($"done {passed}/{tests.Count} passed" + (teardownFailures > 0 ? $" ({teardownFailures} teardown failures)" : ""));
                    File.WriteAllText(outputPath, string.Join("\n", lines) + "\n");
                    return;
                }
                var (type, method, arguments, name) = tests[next++];
                if (!fixtures.TryGetValue(type, out var instance))
                {
                    try
                    {
                        instance = fixtures[type] = Activator.CreateInstance(type);
                        Invoke<OneTimeSetUpAttribute>(type, instance);
                    }
                    catch (Exception e) { brokenFixtures[type] = $"{Inner(e).GetType().Name}: {Inner(e).Message}"; }
                }
                if (brokenFixtures.TryGetValue(type, out var broken))
                {
                    lines.Add($"FAIL {name}: OneTimeSetUp failed: {broken}");
                }
                else
                {
                    try
                    {
                        Invoke<SetUpAttribute>(type, instance);
                        method.Invoke(instance, arguments);
                        lines.Add($"PASS {name}");
                    }
                    catch (Exception e)
                    {
                        lines.Add($"FAIL {name}: {Inner(e).GetType().Name}: {Inner(e).Message}");
                    }
                    finally
                    {
                        try { Invoke<TearDownAttribute>(type, instance); }
                        catch (Exception e) { lines.Add($"TEARDOWN FAIL {name}: {Inner(e).Message}"); }
                    }
                }
                File.WriteAllText(outputPath, "queued\n" + string.Join("\n", lines) + "\n");
            }

            EditorApplication.update += Step;
        }

        private static Exception Inner(Exception e) => e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;

        private static void Invoke<T>(Type type, object instance) where T : Attribute
        {
            foreach (var m in type.GetMethods().Where(m => m.GetCustomAttributes(typeof(T), true).Length > 0)) m.Invoke(instance, null);
        }
    }
}
