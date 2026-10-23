using System.Diagnostics;
using SweetEmu.Core;
using SweetEmu.Core.Fs;

namespace SweetEmu.Tests;

public static class RootTweakTests
{
    static string Python(string script, string dir)
    {
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SkipException("python3 not available");
        }
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("python: " + e);
        return o;
    }

    [Test]
    public static void TweaksMatchSweetRootTweaksPy()
    {
        var dir = Directory.CreateTempSubdirectory("tweaks").FullName;
        // Inputs written by Python's plistlib (binary launchd.plist, XML SubServerConfiguration, an existing pref).
        Python("""
            import plistlib
            plistlib.dump({'LaunchDaemons': {
                '/System/Library/LaunchDaemons/com.apple.CommCenter.plist': {'Label': 'com.apple.CommCenter', 'Program': '/x'},
                '/System/Library/LaunchDaemons/com.apple.backboardd.plist': {'Label': 'com.apple.backboardd', '_PanicOnCrash': True, 'Nested': [1, 2.5, 'x']},
                '/System/Library/LaunchDaemons/com.apple.logd.plist': {'Label': 'com.apple.logd', 'KeepAlive': True}}},
                open('launchd.plist', 'wb'), fmt=plistlib.FMT_BINARY)
            plistlib.dump({'StartRecorderServer': True, 'Other': 'keep'}, open('ssc.plist', 'wb'), fmt=plistlib.FMT_XML)
            plistlib.dump({'Existing': 42}, open('sb.plist', 'wb'), fmt=plistlib.FMT_BINARY)
            """, dir);

        var tree = new FsTree(FsInode.NewDir(0x1ED, 0, 0));
        tree.MkdirP("System/Library/xpc");
        tree.Put("System/Library/xpc/launchd.plist", FsInode.NewFile(File.ReadAllBytes(Path.Combine(dir, "launchd.plist")), 0x1A4, 0, 0));
        tree.MkdirP("System/Library/Frameworks/MediaToolbox.framework/Versions");
        tree.Put("System/Library/Frameworks/MediaToolbox.framework/Versions/SubServerConfiguration.plist",
            FsInode.NewFile(File.ReadAllBytes(Path.Combine(dir, "ssc.plist")), 0x1A4, 0, 0));
        tree.MkdirP("Library/Audio/Plug-Ins/HAL/VirtualAudio.plugin");
        tree.MkdirP("private/var/mobile/Library/Preferences", 0x1ED, 501, 501);
        tree.Put("private/var/mobile/Library/Preferences/com.apple.springboard.plist",
            FsInode.NewFile(File.ReadAllBytes(Path.Combine(dir, "sb.plist")), 0x180, 501, 501));

        var log = new List<string>();
        RootBuilder.ApplyTweaks(tree, "20H392", "16.7.16", log.Add);

        File.WriteAllBytes(Path.Combine(dir, "launchd.out"), tree.Get("System/Library/xpc/launchd.plist")!.ReadAll());
        File.WriteAllBytes(Path.Combine(dir, "ssc.out"), tree.Get("System/Library/Frameworks/MediaToolbox.framework/Versions/SubServerConfiguration.plist")!.ReadAll());
        foreach (var n in new[] { "com.apple.springboard", "com.apple.purplebuddy", "com.apple.migration" })
            File.WriteAllBytes(Path.Combine(dir, n + ".out"), tree.Get($"private/var/mobile/Library/Preferences/{n}.plist")!.ReadAll());

        Python("""
            import plistlib
            d = plistlib.load(open('launchd.out', 'rb'))['LaunchDaemons']
            assert not any(j.get('Label') == 'com.apple.CommCenter' for j in d.values()), d
            bb = d['/System/Library/LaunchDaemons/com.apple.backboardd.plist']
            assert bb == {'Label': 'com.apple.backboardd', 'Nested': [1, 2.5, 'x'], '_PanicOnCrasX': True}, bb
            assert d['/System/Library/LaunchDaemons/com.apple.logd.plist'] == {'Label': 'com.apple.logd', 'KeepAlive': True}
            assert open('launchd.out', 'rb').read(8) == b'bplist00'
            s = open('ssc.out', 'rb').read()
            assert s.startswith(b'<?xml') and plistlib.loads(s) == {'StartRecorderServer': False, 'Other': 'keep'}, s
            sb = plistlib.load(open('com.apple.springboard.out', 'rb'))
            assert sb == {'Existing': 42, 'BKNoWatchdogs': True, 'FBLaunchWatchdogExceptions': {'com.apple.purplebuddy': 100.0}, 'MCForceShowCloudConfigurationUI': False}, sb
            pb = plistlib.load(open('com.apple.purplebuddy.out', 'rb'))
            assert pb == {'SetupDone': True, 'SetupFinishedAllSteps': True, 'SetupVersion': 11, 'daemonizeNeedsToRun': False}, pb
            m = plistlib.load(open('com.apple.migration.out', 'rb'))
            assert m == {'LastSystemVersion': '20H392', 'DMUserDataDisposition': {'SyntheticDidUpgrade': False, 'buildVersion': '20H392'}}, m
            print('ok')
            """, dir);

        Assert.True(tree.Get("Library/Audio/Plug-Ins/HAL/VirtualAudio.plugin") == null, "plugin moved away");
        Assert.True(tree.Get("Library/Audio/Plug-Ins/HAL.disabled/VirtualAudio.plugin") != null, "plugin in HAL.disabled");
        var bundle = tree.Get("private/var/containers/Bundle")!;
        Assert.Equal(33u, bundle.Uid, "Bundle owner _installd");
        var pref = tree.Get("private/var/mobile/Library/Preferences/com.apple.purplebuddy.plist")!;
        Assert.Equal(501u, pref.Uid, "pref owner mobile");
        Assert.Equal((ushort)0x180, pref.Perm, "pref mode 0600");
        Directory.Delete(dir, true);
    }

    [Test]
    public static void PlistRoundTrips()
    {
        var d = new PDict
        {
            { "s", "héllo" }, { "i", 42L }, { "neg", -7L }, { "big", 1L << 40 }, { "r", 1.5 }, { "t", true }, { "f", false },
            { "data", new byte[] { 1, 2, 3 } }, { "arr", new List<object> { "a", 1L, new PDict { { "k", "v" } } } },
            { "date", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
        };
        foreach (var fmt in new[] { PlistFormat.Binary, PlistFormat.Xml })
        {
            var back = (PDict)Plist.Read(Plist.Write(d, fmt));
            Assert.Equal("héllo", back.Str("s"), $"{fmt} string");
            Assert.Equal(42L, (long)back["i"], $"{fmt} int");
            Assert.Equal(-7L, (long)back["neg"], $"{fmt} negative");
            Assert.Equal(1L << 40, (long)back["big"], $"{fmt} big");
            Assert.Equal(1.5, (double)back["r"], $"{fmt} real");
            Assert.Equal(true, (bool)back["t"], $"{fmt} true");
            Assert.Bytes(new byte[] { 1, 2, 3 }, (byte[])back["data"], $"{fmt} data");
            Assert.Equal("v", ((PDict)back.Array("arr")![2]).Str("k"), $"{fmt} nested");
            Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), ((DateTime)back["date"]).ToUniversalTime(), $"{fmt} date");
            Assert.Equal(string.Join(",", d.Keys), string.Join(",", back.Keys), $"{fmt} key order");
        }
    }
}
