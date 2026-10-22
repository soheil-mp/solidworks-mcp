using System.Text.RegularExpressions;

namespace SolidWorksMcp.Core.Files;

/// <summary>
/// The single rule set for every path a tool reads or writes (design §3.11). Paths come from the model — and
/// possibly from injected text — so: no network/device/ADS paths, relative paths resolve under the output folder
/// (never the server's working directory), writes only under allowed roots, no silent overwrites, and a backup
/// before overwriting a file this server didn't write itself.
/// </summary>
public sealed class PathPolicy
{
    private static readonly Regex DeviceName = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase);
    private readonly HashSet<string> _writtenThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PathPolicy(string outputDir, IEnumerable<string> writeRoots, bool allowNetwork, string backupDir)
    {
        OutputDir = Path.GetFullPath(outputDir);
        WriteRoots = writeRoots.Select(r => EnsureSlash(Path.GetFullPath(r))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AllowNetwork = allowNetwork;
        BackupDir = backupDir;
    }

    /// <summary>Default for relative paths: SWMCP_OUTPUT_DIR or %USERPROFILE%\Documents\solidworks-mcp.</summary>
    public string OutputDir { get; }
    public IReadOnlyList<string> WriteRoots { get; }
    public bool AllowNetwork { get; }
    public string BackupDir { get; }

    public static PathPolicy FromEnvironment(string dataDir)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string output = Environment.GetEnvironmentVariable("SWMCP_OUTPUT_DIR") is { Length: > 0 } o
            ? o
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "solidworks-mcp");
        var roots = new List<string> { profile, Path.GetTempPath(), output };
        if (Environment.GetEnvironmentVariable("SWMCP_WRITE_ROOTS") is { Length: > 0 } extra)
            roots.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        bool net = Environment.GetEnvironmentVariable("SWMCP_ALLOW_NETWORK_PATHS") is "1" or "true";
        return new PathPolicy(output, roots, net, Path.Combine(dataDir, "backups"));
    }

    /// <summary>Validates a path the server will read (must exist). Relative paths resolve under <see cref="OutputDir"/>.</summary>
    public string ForRead(string path, string argName = "path")
    {
        string full = Normalize(path, argName);
        if (!File.Exists(full))
            throw new SwException(ErrorCodes.IoError, $"File not found: '{full}'.");
        return full;
    }

    /// <summary>
    /// Validates a path the server will write. Ensures the folder exists, refuses an existing file unless
    /// <paramref name="overwrite"/>, and — when overwriting a file not written by this server in this session — moves
    /// the old file to the backup folder first (returned in <paramref name="backupPath"/>).
    /// </summary>
    public string ForWrite(string path, bool overwrite, out string? backupPath, string argName = "path")
    {
        backupPath = null;
        string full = Normalize(path, argName);
        if (!WriteRoots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            throw new SwException(ErrorCodes.InvalidArgument, $"Writing to '{full}' is not allowed.",
                $"Allowed folders: {string.Join("; ", WriteRoots)}. Use a relative path (saved under {OutputDir}) or ask the user to add the folder to SWMCP_WRITE_ROOTS.");
        if (File.Exists(full))
        {
            if (!overwrite)
                throw new SwException(ErrorCodes.IoError, $"'{full}' already exists.",
                    "Pass overwrite=true to replace it (the old file is backed up unless this server wrote it), or choose another name.");
            bool ours;
            lock (_gate) ours = _writtenThisSession.Contains(full);
            if (!ours)
            {
                string dir = Path.Combine(BackupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(dir);
                backupPath = Path.Combine(dir, Path.GetFileName(full));
                File.Copy(full, backupPath, overwrite: true);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    /// <summary>Records that the server wrote <paramref name="fullPath"/> (later overwrites need no backup).</summary>
    public void RecordWritten(string fullPath)
    {
        lock (_gate) _writtenThisSession.Add(Path.GetFullPath(fullPath));
    }

    private string Normalize(string path, string argName)
    {
        if (string.IsNullOrWhiteSpace(path)) throw SwException.InvalidArgument($"'{argName}' is empty.");
        string p = path.Trim().Trim('"');
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.StartsWith("//?/") || p.StartsWith("//./"))
            throw SwException.InvalidArgument($"'{argName}': device and extended-length paths are not allowed.");
        if (!AllowNetwork && (p.StartsWith(@"\\") || p.StartsWith("//")))
            throw SwException.InvalidArgument($"'{argName}': network paths are not allowed ({p}).",
                "Use a local folder; the user can allow shares with SWMCP_ALLOW_NETWORK_PATHS=1.");
        // An alternate data stream or stray colon (only "X:" at the start is legal).
        int colon = p.IndexOf(':', Path.IsPathRooted(p) && p.Length > 1 && p[1] == ':' ? 2 : 0);
        if (colon >= 0) throw SwException.InvalidArgument($"'{argName}': ':' is only allowed after the drive letter.");
        string full = Path.IsPathFullyQualified(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(OutputDir, p));
        foreach (var seg in full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (DeviceName.IsMatch(seg)) throw SwException.InvalidArgument($"'{argName}': '{seg}' is a reserved device name.");
            if (seg.EndsWith('.') || seg.EndsWith(' ')) throw SwException.InvalidArgument($"'{argName}': path segments can't end with '.' or a space.");
        }
        if (!AllowNetwork && full.StartsWith(@"\\")) throw SwException.InvalidArgument($"'{argName}': network paths are not allowed.");
        return full;
    }

    private static string EnsureSlash(string p) => p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
}
