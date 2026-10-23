// Test runner: `dotnet run --project SweetEmu.Tests [filter]`.
// Some tests need external reference tools/data and are skipped when missing
// (paths via environment variables, see each test).
using System.Diagnostics;
using System.Reflection;
using SweetEmu.Tests;

var filter = args.Length > 0 ? args[0] : "";
int pass = 0, fail = 0, skip = 0;
foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
{
    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
    {
        if (m.GetCustomAttribute<TestAttribute>() == null) continue;
        string name = $"{type.Name}.{m.Name}";
        if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        var sw = Stopwatch.StartNew();
        try
        {
            m.Invoke(null, null);
            Console.WriteLine($"  ok    {name} ({sw.ElapsedMilliseconds} ms)");
            pass++;
        }
        catch (TargetInvocationException e) when (e.InnerException is SkipException s)
        {
            Console.WriteLine($"  skip  {name}: {s.Message}");
            skip++;
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine($"  FAIL  {name}: {e.InnerException}");
            fail++;
        }
    }
}
Console.WriteLine($"{pass} passed, {fail} failed, {skip} skipped");
return fail == 0 ? 0 : 1;

namespace SweetEmu.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public sealed class SkipException(string message) : Exception(message) { }

    public static class Assert
    {
        public static void True(bool cond, string what = "assertion")
        {
            if (!cond) throw new Exception("failed: " + what);
        }

        public static void Equal<T>(T expected, T actual, string what = "value")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        public static void Bytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what = "bytes")
        {
            if (expected.Length != actual.Length)
                throw new Exception($"{what}: length {actual.Length}, expected {expected.Length}");
            int i = expected.CommonPrefixLength(actual);
            if (i != expected.Length) throw new Exception($"{what}: first difference at {i}");
        }

        public static string Tool(string env, string fallback)
        {
            var p = Environment.GetEnvironmentVariable(env) ?? fallback;
            if (!File.Exists(p) && !Directory.Exists(p)) throw new SkipException($"{p} not found (set {env})");
            return p;
        }

        public static string Run(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception($"{exe} failed ({p.ExitCode}): {e}");
            return o;
        }
    }
}
