// Builds the SWEET iOS root image from an IPSW, natively (no Linux tools):
// what tools/mkiosroot.sh + tools/sweet-root-tweaks.py do, on an in-memory tree
// that HfsWriter then writes as a journaled, case-sensitive HFS+ image.
using System.Text;
using SweetEmu.Core.Apfs;
using SweetEmu.Core.Fs;
using SweetEmu.Core.Hfs;
using SweetEmu.Core.IO;

namespace SweetEmu.Core;

public sealed class RootBuildOptions
{
    public required string OutputImage { get; init; }
    /// Image size in bytes; 0 = automatic (16 GiB, or contents + 4 GiB if larger).
    public long ImageSize { get; init; }
    public required string TempDir { get; init; }
    public Action<string>? Log { get; init; }
    /// Fraction 0..1 of the data copy.
    public Action<double>? Progress { get; init; }
}

public static class RootBuilder
{
    const uint Mobile = 501;

    public static void Build(Ipsw ipsw, RootBuildOptions o, CancellationToken cancel = default)
    {
        void Log(string s) => o.Log?.Invoke(s);
        string build = ipsw.BuildVersion, version = ipsw.ProductVersion;
        Log($"IPSW: iOS {version} ({build})");
        if (build != Ipsw.TestedBuild)
            Log($"warning: the port is tested with {Ipsw.TestedBuild}; {build} may not boot");

        var opened = new List<IDisposable>();
        try
        {
            FsInode LoadVolume(string entry, string what)
            {
                Log($"reading {what} ({entry})");
                var src = ipsw.OpenDiskImage(entry, o.TempDir);
                opened.Add(src);
                var c = new ApfsContainer(ApfsContainer.Locate(src));
                opened.Add(c);
                var vols = c.Volumes().Where(v => !v.Encrypted).ToList();
                if (vols.Count == 0) throw new InvalidDataException($"{what}: no readable APFS volume");
                // The volume with a System folder (cryptexes have exactly one volume anyway).
                ApfsVolume? pick = null;
                Exception? firstError = null;
                var loaded = new List<ApfsVolume>();
                foreach (var v in vols)
                {
                    try
                    {
                        v.Load();
                    }
                    catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
                    {
                        Log($"  volume \"{v.Name}\" (role 0x{v.Role:x}) unreadable: {e.Message}");
                        firstError ??= e;
                        continue;
                    }
                    loaded.Add(v);
                    if (v.Children.TryGetValue(ApfsVolume.RootDirId, out var kids) && kids.Any(k => k.Name == "System"))
                    {
                        pick = v;
                        break;
                    }
                }
                pick ??= loaded.FirstOrDefault() ?? throw new InvalidDataException($"{what}: {firstError?.Message}", firstError);
                Log($"  volume \"{pick.Name}\"{(pick.Sealed ? " (sealed)" : "")}: {pick.Inodes.Count} inodes");
                return ApfsImport.Import(pick, w => Log("  warning: " + w));
            }

            var tree = new FsTree(LoadVolume(ipsw.OsImage, "OS"));
            cancel.ThrowIfCancellationRequested();
            var sysOs = LoadVolume(ipsw.SystemOsImage, "SystemOS cryptex");
            var appOs = LoadVolume(ipsw.AppOsImage, "AppOS cryptex");
            cancel.ThrowIfCancellationRequested();

            // mkiosroot.sh: cryptexes inside the one volume.
            tree.MkdirP("private/preboot/Cryptexes");
            tree.Put("private/preboot/Cryptexes/OS", sysOs);
            tree.Put("private/preboot/Cryptexes/App", appOs);

            // Shared cache where dyld looks at ignition level 1: hard links.
            const string dsc = "private/preboot/Cryptexes/OS/System/Library/Caches/com.apple.dyld";
            var dscDir = tree.Get(dsc);
            if (dscDir?.Kind == NodeKind.Dir)
            {
                var target = tree.MkdirP("System/Library/Caches/com.apple.dyld");
                foreach (var (name, node) in dscDir.Entries!)
                {
                    if (!target.Entries!.ContainsKey(name)) target.Entries[name] = LinkOrCopy(node);
                }
                Log($"linked {dscDir.Entries.Count} shared cache files");
            }
            else Log("warning: no dyld shared cache in the SystemOS cryptex");

            // Every cryptex file without a counterpart in the OS root gets a root path too.
            foreach (var c in new[] { "OS", "App" })
            {
                string baseDir = "private/preboot/Cryptexes/" + c;
                var cryptex = new FsTree(tree.Get(baseDir)!);
                int linked = 0;
                foreach (var (path, node) in cryptex.Walk().ToList())
                {
                    if (node.Kind == NodeKind.Dir) continue;
                    if (path.StartsWith("System/Library/Caches/com.apple.dyld/", StringComparison.Ordinal)) continue;
                    if (Resolve(tree, path, followLast: false) != null) continue;
                    string parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                    var dir = MkdirPResolving(tree, parent);
                    dir.Entries![path[(path.LastIndexOf('/') + 1)..]] = LinkOrCopy(node);
                    linked++;
                }
                Log($"linked {linked} {c} cryptex files into the root");
            }

            // logd's stores (normally made at restore).
            const string db = "private/var/db";
            tree.InstallDir(db + "/diagnostics", 0x1E9 /*0751*/, 272, 272);
            tree.InstallDir(db + "/uuidtext", 0x1ED, 272, 272);
            tree.InstallDir(db + "/diagnostics/timesync", 0x1ED, 272, 272);
            foreach (var d in new[] { "diagnostics", "uuidtext", "diagnostics/timesync" })
                tree.Put($"{db}/{d}/.migrated", FsInode.NewFile(Array.Empty<byte>(), 0x1A4, 272, 272));

            // System keybag for the built-in AppleKeyStore (no passcode).
            tree.InstallDir("private/var/keybags", 0x1C0 /*0700*/, 0, 0);
            tree.Put("private/var/keybags/systembag.kb", FsInode.NewFile(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\"><dict><key>KeyBagKeys</key><data>U1dFRVQ=</data></dict></plist>\n"), 0x180, 0, 0));

            ApplyTweaks(tree, build, version, Log);
            EmulatorTweaks(tree, Log);

            // Size and write.
            long dataBytes = 0;
            var seen = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
            int count = 0;
            foreach (var (_, n) in tree.Walk())
            {
                count++;
                if (n.Kind == NodeKind.File && seen.Add(n)) dataBytes += (n.Length + 4095) / 4096 * 4096;
            }
            long size = o.ImageSize > 0 ? o.ImageSize : Math.Max(16L << 30, dataBytes + (4L << 30));
            size = size / (1L << 20) * (1L << 20);
            Log($"writing {count} entries, {dataBytes >> 20} MiB of file data, into a {size >> 30} GiB image");
            var writer = new HfsWriter
            {
                VolumeName = "iOSRoot",
                Progress = (done, total) => o.Progress?.Invoke(total == 0 ? 1 : (double)done / total),
            };
            string part = o.OutputImage + ".part";
            writer.Write(tree, part, size, cancel);
            File.Move(part, o.OutputImage, true);
            Log($"root image ready: {o.OutputImage}");
        }
        finally
        {
            for (int i = opened.Count - 1; i >= 0; i--) opened[i].Dispose();
        }
    }

    /// Files are shared (hard link); symlinks are copied (HFS+ cannot hard-link them).
    static FsInode LinkOrCopy(FsInode n)
    {
        if (n.Kind != NodeKind.Symlink) return n;
        return new FsInode
        {
            Kind = NodeKind.Symlink, Mode = n.Mode, Uid = n.Uid, Gid = n.Gid, BsdFlags = n.BsdFlags,
            CreateNs = n.CreateNs, ModNs = n.ModNs, ChangeNs = n.ChangeNs, AccessNs = n.AccessNs, Target = n.Target,
        };
    }

    /// Path lookup that follows symlinks in intermediate components (and the last one if asked),
    /// with absolute targets taken relative to the image root. Null when the path does not exist.
    static FsInode? Resolve(FsTree tree, string path, bool followLast, int depth = 0)
    {
        if (depth > 32) return null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<FsInode> { tree.Root };
        for (int i = 0; i < parts.Length; i++)
        {
            var cur = stack[^1];
            string part = parts[i];
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (cur.Kind != NodeKind.Dir || !cur.Entries!.TryGetValue(part, out var next)) return null;
            bool last = i == parts.Length - 1;
            if (next.Kind == NodeKind.Symlink && (!last || followLast))
            {
                string target = next.Target ?? "";
                string prefix = target.StartsWith('/') ? "" : string.Join('/', parts[..i]);
                string rest = string.Join('/', parts[(i + 1)..]);
                string combined = string.Join('/', new[] { prefix, target.TrimStart('/'), rest }.Where(s => s.Length > 0));
                return Resolve(tree, combined, followLast, depth + 1);
            }
            stack.Add(next);
        }
        return stack[^1];
    }

    /// mkdir -p that follows existing symlinked directories.
    static FsInode MkdirPResolving(FsTree tree, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string sofar = "";
        FsInode cur = tree.Root;
        foreach (var part in parts)
        {
            sofar = sofar.Length == 0 ? part : sofar + "/" + part;
            var existing = Resolve(tree, sofar, followLast: true);
            if (existing != null)
            {
                if (existing.Kind != NodeKind.Dir) throw new IOException($"not a directory: {sofar}");
                cur = existing;
                continue;
            }
            var d = FsInode.NewDir(0x1ED, 0, 0);
            cur.Entries![part] = d;
            cur = d;
        }
        return cur;
    }

    /// Daemons that only crash-loop under QEMU (observed 2026-10-23: each one
    /// restarted ~100 times in 20 minutes). On one emulated CPU every restart
    /// takes time away from SpringBoard, so the emulator image does not run them.
    public static readonly string[] EmulatorDisabledDaemons =
    {
        "profiled", "aggregated", "backupd", "donotdisturbd", "passd", "coreidvd",
        "sharingd", "callservicesd", "assistantd",
    };

    public static void EmulatorTweaks(FsTree tree, Action<string> log)
    {
        var launchd = tree.Get("System/Library/xpc/launchd.plist");
        if (launchd?.Kind != NodeKind.File) return;
        var plist = (PDict)Plist.Read(launchd.ReadAll());
        if (plist.Dict("LaunchDaemons") is not { } jobs) return;
        var removed = new List<string>();
        foreach (var k in jobs.Keys.ToList())
        {
            if (jobs[k] is not PDict job) continue;
            string? program = job.Str("Program") ?? job.Array("ProgramArguments")?.FirstOrDefault() as string;
            if (program == null) continue;
            string name = program[(program.LastIndexOf('/') + 1)..];
            if (Array.IndexOf(EmulatorDisabledDaemons, name) >= 0)
            {
                jobs.Remove(k);
                removed.Add(name);
            }
        }
        launchd.SetContent(Plist.Write(plist, PlistFormat.Binary));
        log($"launchd.plist (emulator): {removed.Count} crash-looping jobs removed ({string.Join(", ", removed.Distinct())})");
    }

    // ---------------- tools/sweet-root-tweaks.py ----------------

    public static void ApplyTweaks(FsTree tree, string build, string version, Action<string> log)
    {
        // launchd.plist: crashing daemons must not panic; no baseband.
        var launchd = tree.Get("System/Library/xpc/launchd.plist");
        if (launchd?.Kind == NodeKind.File)
        {
            var plist = (PDict)Plist.Read(launchd.ReadAll());
            int renamed = 0, removed = 0;
            if (plist.Dict("LaunchDaemons") is { } jobs)
            {
                foreach (var (_, j) in jobs)
                {
                    if (j is PDict job && job.ContainsKey("_PanicOnCrash"))
                    {
                        var v = job["_PanicOnCrash"];
                        job.Remove("_PanicOnCrash");
                        job["_PanicOnCrasX"] = v;
                        renamed++;
                    }
                }
                foreach (var k in jobs.Keys.ToList())
                {
                    if (jobs[k] is PDict job && job.Str("Label") == "com.apple.CommCenter")
                    {
                        jobs.Remove(k);
                        removed++;
                    }
                }
            }
            launchd.SetContent(Plist.Write(plist, PlistFormat.Binary));
            log($"launchd.plist: {renamed} _PanicOnCrash renamed, {removed} CommCenter jobs removed");
        }
        else log("warning: no System/Library/xpc/launchd.plist");

        // Containers that live on the data volume on a real device.
        foreach (var (sub, uid) in new[] { ("Bundle", 33u), ("Data/System", 0u), ("Shared/SystemGroup", 0u), ("Temp", 0u) })
        {
            string p = "private/var/containers/" + sub;
            if (tree.Get(p) == null)
            {
                var d = tree.MkdirP(p);
                d.Uid = d.Gid = uid;
                d.Perm = 0x1ED;
            }
        }

        // mobileactivationd data ark (only when its system container exists in the image).
        var sys = tree.Get("private/var/containers/Data/System");
        FsInode? activation = null;
        if (sys?.Kind == NodeKind.Dir)
        {
            foreach (var (_, c) in sys.Entries!)
            {
                if (c.Kind != NodeKind.Dir || !c.Entries!.TryGetValue(".com.apple.mobile_container_manager.metadata.plist", out var meta)) continue;
                if ((Plist.Read(meta.ReadAll()) as PDict)?.Str("MCMMetadataIdentifier") == "com.apple.mobileactivationd")
                {
                    activation = c;
                    break;
                }
            }
        }
        if (activation != null)
        {
            var lib = new FsTree(activation).MkdirP("Library/internal", 0x1ED, Mobile, Mobile);
            lib.Uid = lib.Gid = Mobile;
            lib.Perm = 0x1ED;
            lib.Entries!["data_ark.plist"] = FsInode.NewFile(Plist.Write(new PDict
            {
                { "-ActivationState", "Activated" }, { "-BrickState", false },
                { "-BuildVersion", build }, { "-ProductVersion", version },
                { "-DataMigrationCompleted", true }, { "-PreSydroDataMigrationCompleted", true },
            }, PlistFormat.Binary), 0x180, Mobile, Mobile);
            log("data ark seeded");
        }
        else log("no mobileactivationd system container in the image; skipped data ark");

        // Audio: VirtualAudio HAL plug-in off; MediaToolbox recorder server off.
        var hal = tree.Get("Library/Audio/Plug-Ins/HAL");
        if (hal?.Kind == NodeKind.Dir && hal.Entries!.Remove("VirtualAudio.plugin", out var plugin))
        {
            tree.MkdirP("Library/Audio/Plug-Ins/HAL.disabled").Entries!["VirtualAudio.plugin"] = plugin;
            log("VirtualAudio.plugin disabled");
        }
        var mt = tree.Get("System/Library/Frameworks/MediaToolbox.framework");
        if (mt?.Kind == NodeKind.Dir)
        {
            foreach (var (name, d) in mt.Entries!)
            {
                if (d.Kind != NodeKind.Dir || !d.Entries!.TryGetValue("SubServerConfiguration.plist", out var conf) || conf.Kind != NodeKind.File) continue;
                var data = (PDict)Plist.Read(conf.ReadAll());
                data["StartRecorderServer"] = false;
                conf.SetContent(Plist.Write(data, PlistFormat.Xml));
                log($"StartRecorderServer off ({name})");
            }
        }

        void MergePref(string name, PDict values)
        {
            var dir = tree.Get("private/var/mobile/Library/Preferences") ?? tree.MkdirP("private/var/mobile/Library/Preferences", 0x1ED, Mobile, Mobile);
            var data = dir.Entries!.TryGetValue(name + ".plist", out var existing) && existing.Kind == NodeKind.File
                ? Plist.Read(existing.ReadAll()) as PDict ?? new PDict() : new PDict();
            foreach (var (k, v) in values) data[k] = v;
            dir.Entries![name + ".plist"] = FsInode.NewFile(Plist.Write(data, PlistFormat.Binary), 0x180, Mobile, Mobile);
            log($"pref {name}");
        }
        MergePref("com.apple.migration", new PDict
        {
            { "LastSystemVersion", build },
            { "DMUserDataDisposition", new PDict { { "SyntheticDidUpgrade", false }, { "buildVersion", build } } },
        });
        MergePref("com.apple.springboard", new PDict
        {
            { "BKNoWatchdogs", true },
            { "FBLaunchWatchdogExceptions", new PDict { { "com.apple.purplebuddy", 100.0 } } },
            { "MCForceShowCloudConfigurationUI", false },
        });
        MergePref("com.apple.purplebuddy", new PDict
        {
            { "SetupDone", true }, { "SetupFinishedAllSteps", true }, { "SetupVersion", 11L }, { "daemonizeNeedsToRun", false },
        });
        var mini = new PDict();
        foreach (var k in new[] { "ShouldRunDiagnosticsMiniBuddy", "ShouldRunHSA2MiniBuddy", "ShouldRunKeychainSyncMiniBuddy",
                     "ShouldRunMesaMiniBuddy", "ShouldRunPaymentMiniBuddy", "ShouldRunStandardMiniBuddy", "SilentICDPUpgrade" })
            mini[k] = false;
        MergePref("com.apple.purplebuddy.notbackedup", mini);
        MergePref("com.apple.managedconfiguration.notbackedup", new PDict
        {
            { "LockdownActivationIndicatesCloudConfigurationAvailable", false },
        });
    }
}
