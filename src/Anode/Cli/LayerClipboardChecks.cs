using Anode.Core.Layers;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace Anode.Cli;

internal static class LayerClipboardChecks
{
    internal static string HistoryPolicy()
    {
        var original = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [DataFormats.UnicodeText] = "private text",
            [DataFormats.Html] = "<b>private text</b>",
            ["canincludeinclipboardhistory"] = new MemoryStream(new byte[] { 1, 0, 0, 0 })
        };
        var snapshot = new LayerClipboard.Snapshot(original);
        var data = snapshot.AsTransient().NativeFormats().ToDictionary(x => x.Key, x => x.Value);
        foreach (string name in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
            Require(data[name] is MemoryStream stream && stream.ToArray().SequenceEqual(new byte[4]), "Transient history policy did not contain a raw zero DWORD.");
        Require(data.Count == 5 && (string)data[DataFormats.UnicodeText] == "private text" && (string)data[DataFormats.Html] == "<b>private text</b>", "Policy changed clipboard payload or duplicated format names.");
        Require(snapshot.NativeFormats().Count() == 3 && ((MemoryStream)original["canincludeinclipboardhistory"]).ToArray()[0] == 1, "Transient policy mutated the snapshot or exclusive API policy.");
        Require(!LayerClipboard.Snapshot.Empty().AsTransient().NativeFormats().Any(), "A truly empty restore became nonempty.");
        return "transient swaps exclude Windows clipboard history/cloud, preserve payload and original policy, zero DWORD serialization, empty restore; memory only";
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class MemoryBackend : LayerClipboard.IBackend
    {
        internal LayerClipboard.Snapshot Current = LayerClipboard.Snapshot.Text("shared");
        internal uint Version;
        internal bool FailRead;
        internal bool FailWrite;
        public LayerClipboard.Snapshot Read() => FailRead ? throw new NotSupportedException("Unsupported clipboard format") : Current;
        public void Write(LayerClipboard.Snapshot value)
        {
            if (FailWrite) throw new ExternalException("Clipboard cannot be opened", unchecked((int)0x800401D0));
            Current = value; Version++;
        }
        public uint Sequence() => Version;
        internal string Text() => (string)Current.Formats[DataFormats.UnicodeText];
    }
    internal static string Isolation()
    {
        var backend = new MemoryBackend();
        var a = new LayerClipboard(backend); var b = new LayerClipboard(backend);
        Require(a.ReadText() == "" && b.ReadText() == "", "A new layer must have empty text.");
        var shortcuts = new (string chord, LayerHub.ClipboardAction expected)[]
        {
            ("CONTROL+C", LayerHub.ClipboardAction.Copy), ("ctrl+insert", LayerHub.ClipboardAction.Copy),
            ("ctrl+x", LayerHub.ClipboardAction.Cut), ("shift+delete", LayerHub.ClipboardAction.Cut),
            ("ctrl+v", LayerHub.ClipboardAction.Paste), ("shift+insert", LayerHub.ClipboardAction.Paste),
            ("ctrl+shift+v", LayerHub.ClipboardAction.Paste),
            ("alt+ctrl+c", LayerHub.ClipboardAction.None), ("ctrl+shift+c", LayerHub.ClipboardAction.None),
            ("win+ctrl+v", LayerHub.ClipboardAction.None), ("c", LayerHub.ClipboardAction.None)
        };
        foreach (var (chord, expected) in shortcuts)
            Require(LayerHub.ClipboardShortcut(chord) == expected, $"Wrong clipboard action for {chord}.");
        a.WriteText("A 😀"); b.WriteText("");
        var turn = a.BeginTurn(); Require(backend.Text() == "A 😀", "A was not activated.");
        backend.Write(new LayerClipboard.Snapshot(new() { [DataFormats.UnicodeText] = "copy A", [DataFormats.Html] = "<b>A</b>" }));
        turn.Complete(copy: true); Require(backend.Text() == "shared" && a.ReadText() == "copy A", "A copy or shared restore failed.");
        turn = b.BeginTurn(); Require(backend.Text() == "", "Empty Unicode text was not preserved.");
        backend.Write(LayerClipboard.Snapshot.Text("copy B")); turn.Complete(copy: true);
        Require(a.ReadText() == "copy A" && b.ReadText() == "copy B" && backend.Text() == "shared", "Layers crossed.");
        turn = a.BeginTurn(); Require(backend.Current.Formats.ContainsKey(DataFormats.Html), "Copy lost a non-text format.");
        backend.FailRead = true; backend.Version++;
        try { turn.Complete(); throw new InvalidOperationException("Unsupported data was accepted."); }
        catch (InvalidOperationException ex) when (ex.InnerException is NotSupportedException) { }
        backend.FailRead = false;
        Require(backend.Text() == "shared" && a.ReadText() == "copy A" && b.ReadText() == "copy B", "Failed capture corrupted state.");
        turn = a.BeginTurn();
        try { turn.Complete(copy: true); throw new InvalidOperationException("Unchanged copy was accepted."); }
        catch (InvalidOperationException ex) when (ex.InnerException is InvalidOperationException) { }
        Require(backend.Text() == "shared" && a.ReadText() == "copy A", "Failed copy corrupted state.");
        turn = a.BeginTurn(); backend.FailWrite = true;
        try { turn.Complete(); throw new InvalidOperationException("Failed restoration was accepted."); }
        catch (InvalidOperationException ex) when (ex.InnerException is ExternalException)
        { Require(ex.Message.Contains("800401D0"), "Missing native failure code."); }
        string active = backend.Text();
        try { b.BeginTurn(); throw new InvalidOperationException("Another layer bypassed pending restoration."); }
        catch (InvalidOperationException ex) when (ex.InnerException is ExternalException)
        { Require(ex.Message.Contains("no new input"), "Unclear pending recovery error."); }
        Require(backend.Text() == active, "Pending failure installed another layer's clipboard.");
        backend.FailWrite = false;
        turn = b.BeginTurn(); Require(backend.Text() == "copy B", "Recovery did not permit B after restoring shared state.");
        turn.Complete(); Require(backend.Text() == "shared", "Recovery lost original shared clipboard.");

        long clock = 0; int tries = 0;
        int native = ClipboardNativeRetry.Run(() => ++tries < 3
            ? throw new ExternalException("busy", unchecked((int)0x800401D0)) : 42, 150,
            () => clock, ms => clock += ms);
        Require(native == 42 && tries == 3 && clock == 100, "Transient clipboard retry.");
        clock = 0; tries = 0;
        native = ClipboardNativeRetry.Run(() => ++tries < 42
            ? throw new ExternalException("busy", unchecked((int)0x800401D0)) : 42,
            now: () => clock, wait: ms => clock += ms);
        Require(native == 42 && clock == 2050, "A longer native-only clipboard contention was not recovered.");
        clock = 0; tries = 0;
        try { ClipboardNativeRetry.Run<int>(() => { tries++; throw new ExternalException("busy", unchecked((int)0x800401D0)); },
            120, () => clock, ms => clock += ms); }
        catch (ExternalException) { }
        Require(tries == 3 && clock == 120, "Clipboard retry exceeded deadline.");
        tries = 0;
        try { ClipboardNativeRetry.Run<int>(() => { tries++; throw new ExternalException("permanent", unchecked((int)0x80070005)); }); }
        catch (ExternalException) { }
        Require(tries == 1, "Permanent clipboard error was retried.");
        return "shortcut parsing, independent text/formats, failed capture restoration, shared pending-recovery fence, native busy retry/deadline/permanent errors; memory backend only";
    }
}
