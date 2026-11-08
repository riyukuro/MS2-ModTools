// m2d: MS2 .m2d archive tool.
//   m2d list <archive.m2d>
//   m2d extract <archive.m2d> <outdir> [prefix ...]     extract entries (optionally only those whose name starts with a prefix)
//   m2d cat <archive.m2d> <entry>                       write one entry to stdout
//   m2d info <archive.m2d>                              pack version / header info
//   m2d repack <archive.m2d> <srcdir> <out.m2d|.m2h>    see Repack.cs
using Maple2.File.IO;
using Maple2.File.IO.Crypto.Common;

static int Usage() {
    Console.Error.WriteLine("usage: m2d list|info <m2d> | extract <m2d> <outdir> [prefix...] | cat <m2d> <entry> | repack <orig.m2d> <srcdir> <out.m2d>");
    return 2;
}
if (args.Length < 2) return Usage();
switch (args[0]) {
    case "list": {
        using var r = new M2dReader(args[1]);
        foreach (var e in r.Files) Console.WriteLine(e.Name);
        return 0;
    }
    case "info": {
        using var r = new M2dReader(args[1]);
        Console.WriteLine($"files={r.Files.Count}");
        var e0 = r.Files[0];
        Console.WriteLine($"first={e0.Name} idx={e0.Index} hash={e0.Hash} hdr={e0.FileHeader?.GetType().Name} ver={e0.FileHeader?.Version} flag={e0.FileHeader?.BufferFlag}");
        return 0;
    }
    case "cat": {
        if (args.Length < 3) return Usage();
        using var r = new M2dReader(args[1]);
        var e = r.GetEntry(args[2]);
        if (e == null) { Console.Error.WriteLine("not found"); return 1; }
        using var o = Console.OpenStandardOutput();
        var b = r.GetBytes(e); o.Write(b, 0, b.Length);
        return 0;
    }
    case "extract": {
        if (args.Length < 3) return Usage();
        var prefixes = args.Skip(3).ToArray();
        using var r = new M2dReader(args[1]);
        int n = 0, bad = 0; long bytes = 0;
        foreach (var e in r.Files) {
            if (prefixes.Length > 0 && !prefixes.Any(p => e.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            try {
                var b = r.GetBytes(e);
                var path = Path.Combine(args[2], e.Name.Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, b);
                n++; bytes += b.Length;
            } catch (Exception ex) { bad++; Console.Error.WriteLine($"FAIL {e.Name}: {ex.Message}"); }
        }
        Console.WriteLine($"extracted {n} files ({bytes} bytes), {bad} failures");
        return bad == 0 ? 0 : 1;
    }
    case "repack": return Repack.Run(args);
    default: return Usage();
}
