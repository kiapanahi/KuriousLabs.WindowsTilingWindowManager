using System.Text.Json;
using Bastion.Core;

namespace Bastion.Daemon;

/// <summary>
/// Reads and merges GitHub issue #9's two rules files: the shipped curated file and the user's
/// overlay. The only I/O this subsystem's "loading" half performs — deliberately separate from the
/// "watching" half (<see cref="IConfigDirectoryWatcher"/>/<see cref="WindowRulesHotReloadService"/>),
/// which calls back into this same loader on every debounced directory change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synchronous, deliberately.</b> Both files are small (a curated list plus a handful of user
/// overrides — kilobytes, not megabytes) and are read at most once at startup and occasionally
/// thereafter on a human-triggered edit event, never on a hot path. Plain synchronous
/// <see cref="File.ReadAllBytes(string)"/> keeps both call sites — the startup
/// <c>OptionsBuilder&lt;WindowRulesOptions&gt;.Configure</c> delegate (which has no async shape to
/// call into) and <see cref="WindowRulesHotReloadService"/>'s debounce <c>TimerCallback</c> (also
/// synchronous) — simple, with no <see cref="Task"/> plumbing to track for what is, in both cases,
/// an infrequent, small, one-shot read.
/// </para>
/// <para>
/// <b>Parsed independently, merged as object graphs — never text-merged</b>
/// (<c>docs/engineering/json-ipc-config.md</c> §2): each file is deserialized on its own into a
/// <see cref="WindowRulesDocument"/>, and only the two resulting <em>objects</em> are combined, via
/// <see cref="WindowRulesDocument.Merge"/> (a pure <c>Bastion.Core</c> function — no string
/// concatenation or JSON-text splicing occurs anywhere in this type).
/// </para>
/// <para>
/// A missing file (fresh install: no user overlay yet; a broken install: no shipped file) is the
/// routine, expected "no rules from this side" case, not an error — mirrors
/// <c>Bastion.Win32.HwndJournalStore.ReadAsync</c>'s identical treatment of a missing journal file.
/// Any other <see cref="JsonException"/> (malformed JSON, a rule failing its
/// <see langword="required"/> members) propagates to the caller, which decides what "malformed"
/// means for its own boundary — fail-fast at startup
/// (<c>docs/engineering/daemon-architecture.md</c> §4) vs. keep-serving-the-last-known-good on a
/// hot-reload (DESIGN.md §3.9) are two different callers' policies, not this loader's.
/// </para>
/// </remarks>
internal sealed class WindowRulesConfigLoader(WindowRulesConfigPaths paths)
{
    /// <summary>Loads both files fresh from disk and returns their merged result. See this type's remarks for the merge/error-handling contract.</summary>
    public WindowRulesDocument LoadMerged()
    {
        WindowRulesDocument shipped = LoadDocument(paths.ShippedRulesFilePath);
        WindowRulesDocument overlay = LoadDocument(paths.UserRulesFilePath);
        return WindowRulesDocument.Merge(shipped, overlay);
    }

    private static WindowRulesDocument LoadDocument(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return WindowRulesDocument.Empty;
        }

        return JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.WindowRulesDocument)
            ?? throw new JsonException($"Config file '{path}' deserialized to a null document.");
    }
}
