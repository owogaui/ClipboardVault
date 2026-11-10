using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32;

namespace ClipboardVault;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { SelfTest.Run(); return; }
        using var mutex = new Mutex(true, @"Local\ClipboardVault", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new Vault());
    }
}

static class Limits
{
    public const long Max = 100_000_000; // Decimal MB. Equality is rejected.
    public static void Check(long size) { if (size < 0 || size >= Max) throw new IOException("已略過：每次複製的總大小必須小於 100 MB。"); }
    public static void Copy(Stream source, Stream target, ref long total)
    {
        byte[] buffer = new byte[65536];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            Check(total + read);
            target.Write(buffer, 0, read);
            total += read;
        }
    }
}
sealed class LimitedMemory : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count) { Limits.Check(Math.Max(Length, Position + count)); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Limits.Check(Math.Max(Length, Position + buffer.Length)); base.Write(buffer); }
    public override void WriteByte(byte value) { Limits.Check(Math.Max(Length, Position + 1)); base.WriteByte(value); }
    public override void SetLength(long value) { Limits.Check(value); base.SetLength(value); }
}
record Snapshot(string Root, string Kind, byte[]? Data, string[]? Files);
sealed class Settings
{
    public string Root { get; set; } = "";
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardVault", "settings.json");
    public static Settings Load() { try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName)) ?? new(); } catch { return new(); } }
    public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(PathName)!); File.WriteAllText(PathName, JsonSerializer.Serialize(this)); }
}
sealed class Vault : Form
{
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    readonly NotifyIcon tray;
    readonly Settings settings = Settings.Load();
    readonly Channel<Snapshot> jobs = Channel.CreateBounded<Snapshot>(2);
    readonly System.Windows.Forms.Timer retry = new() { Interval = 100 };
    readonly Task worker;
    bool paused, closing, registered;
    int attempts;
    uint sequence;
    string? previous;

    public Vault()
    {
        Text = "ClipboardVault";
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        var menu = new ContextMenuStrip();
        var pause = menu.Items.Add("暫停備份");
        pause.Click += (_, _) => { paused = !paused; pause.Text = paused ? "恢復備份" : "暫停備份"; tray!.Text = paused ? "ClipboardVault — 已暫停" : "ClipboardVault — 備份中"; };
        menu.Items.Add("開啟備份資料夾", null, (_, _) => { try { Directory.CreateDirectory(settings.Root); Process.Start(new ProcessStartInfo(settings.Root) { UseShellExecute = true }); } catch (Exception e) { Tell(e.Message); } });
        menu.Items.Add("選擇備份資料夾…", null, (_, _) => ChooseFolder());
        var startup = new ToolStripMenuItem("開機自動啟動") { CheckOnClick = true };
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.Checked = key?.GetValue("ClipboardVault") != null;
        startup.Click += (_, _) => { try { using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (startup.Checked) key.SetValue("ClipboardVault", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("ClipboardVault", false); } catch (Exception e) { startup.Checked = !startup.Checked; Tell(e.Message); } };
        menu.Items.Add(startup);
        menu.Items.Add("結束", null, (_, _) => Close());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "ClipboardVault — 備份中", ContextMenuStrip = menu, Visible = true };
        retry.Tick += (_, _) => CaptureClipboard();
        worker = Task.Run(Work);
        Shown += (_, _) => { Hide(); if (string.IsNullOrWhiteSpace(settings.Root) && !ChooseFolder()) Close(); else Tell("已開始備份。每次內容必須小於 100 MB；右鍵圖示可暫停。"); };
    }
    bool ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "選擇剪貼簿備份資料夾（可能包含個人敏感資料）", UseDescriptionForTitle = true, SelectedPath = settings.Root };
        if (dialog.ShowDialog() != DialogResult.OK) return false;
        try { var next = new Settings { Root = dialog.SelectedPath }; next.Save(); settings.Root = next.Root; return true; }
        catch (Exception e) { Tell(e.Message); return false; }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); registered = AddClipboardFormatListener(Handle); if (!registered) throw new InvalidOperationException("無法啟動剪貼簿監聽。"); }
    protected override void OnHandleDestroyed(EventArgs e) { if (registered) RemoveClipboardFormatListener(Handle); base.OnHandleDestroyed(e); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x031D && !paused && !closing) { sequence = GetClipboardSequenceNumber(); attempts = 0; retry.Start(); }
        base.WndProc(ref m);
    }
    void CaptureClipboard()
    {
        retry.Stop();
        if (paused || closing || string.IsNullOrEmpty(settings.Root)) return;
        if (sequence != GetClipboardSequenceNumber()) return;
        try
        {
            Snapshot? item = null;
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                long total = 0;
                foreach (var path in paths) { var info = new FileInfo(path); if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("已略過：不支援資料夾或連結。"); total = checked(total + info.Length); Limits.Check(total); }
                item = new(settings.Root, "files", null, paths);
            }
            else if (Clipboard.ContainsImage())
            {
                using var image = Clipboard.GetImage();
                if (image == null) return;
                using var stream = new LimitedMemory();
                image.Save(stream, ImageFormat.Png);
                item = new(settings.Root, "image", stream.ToArray(), null);
            }
            else if (Clipboard.ContainsText())
            {
                var value = Clipboard.GetText();
                Limits.Check(Encoding.UTF8.GetByteCount(value));
                item = new(settings.Root, "text", Encoding.UTF8.GetBytes(value), null);
            }
            if (sequence != GetClipboardSequenceNumber()) return;
            if (item != null && !jobs.Writer.TryWrite(item)) Tell("備份忙碌中，已略過這筆複製。請稍後再複製。");
        }
        catch (ExternalException) { if (++attempts < 10) retry.Start(); else Tell("剪貼簿被占用，已略過這筆複製。"); }
        catch (Exception e) { Tell(e.Message); }
    }
    async Task Work()
    {
        await foreach (var item in jobs.Reader.ReadAllAsync())
        {
            string? stage = null;
            try
            {
                var day = Path.Combine(item.Root, DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(day);
                string id = DateTime.Now.ToString("HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8];
                stage = Path.Combine(day, ".pending_" + id);
                Directory.CreateDirectory(stage);
                long total = 0;
                if (item.Data != null) { Limits.Check(item.Data.LongLength); File.WriteAllBytes(Path.Combine(stage, item.Kind == "text" ? "text.txt" : "image.png"), item.Data); total = item.Data.LongLength; }
                else
                {
                    int i = 0;
                    foreach (string path in item.Files!)
                    {
                        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        Limits.Check(total + source.Length);
                        using var target = new FileStream(Path.Combine(stage, $"{++i:D3}_" + Path.GetFileName(path)), FileMode.CreateNew);
                        Limits.Copy(source, target, ref total);
                    }
                }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var path in Directory.GetFiles(stage).OrderBy(x => x, StringComparer.Ordinal))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
                    using var stream = File.OpenRead(path);
                    var buffer = new byte[65536]; int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, count);
                }
                string fingerprint = item.Root + ":" + Convert.ToHexString(hash.GetHashAndReset());
                if (fingerprint == previous) continue;
                // Keep metadata inside the same atomic record directory.
                File.WriteAllText(Path.Combine(stage, "record.json"), JsonSerializer.Serialize(new { time = DateTimeOffset.Now, kind = item.Kind, bytes = total }));
                Directory.Move(stage, Path.Combine(day, id));
                stage = null;
                previous = fingerprint;
            }
            catch (Exception e) { Tell("備份失敗：" + e.Message); }
            finally { if (stage != null) try { Directory.Delete(stage, true); } catch { Tell("暫存資料清理失敗，請檢查備份資料夾中的 .pending 資料夾。"); } }
        }
    }
    void Tell(string message)
    {
        if (closing || IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => Tell(message))); } catch (InvalidOperationException) { } return; }
        tray.ShowBalloonTip(4000, "ClipboardVault", message, ToolTipIcon.Info);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true; retry.Stop(); jobs.Writer.TryComplete();
        worker.GetAwaiter().GetResult();
        tray.Visible = false; tray.Dispose(); retry.Dispose();
        base.OnFormClosing(e);
    }
}
static class SelfTest
{
    public static void Run()
    {
        Limits.Check(99_999_999);
        foreach (long value in new long[] { -1, 100_000_000, 100_000_001 })
        {
            bool rejected = false; try { Limits.Check(value); } catch (IOException) { rejected = true; }
            if (!rejected) throw new Exception("Boundary rejection failed");
        }
        using var source = new MemoryStream(new byte[2]);
        using var target = new MemoryStream();
        long total = 99_999_998;
        bool blocked = false;
        try { Limits.Copy(source, target, ref total); } catch (IOException) { blocked = true; }
        if (!blocked || target.Length != 0) throw new Exception("Aggregate cap failed");
        using var accepted = new MemoryStream(new byte[1]);
        total = 99_999_998;
        Limits.Copy(accepted, target, ref total);
        if (total != 99_999_999) throw new Exception("Below-limit copy failed");
        using var boundedImage = new LimitedMemory();
        boundedImage.Position = Limits.Max - 1;
        bool imageBlocked = false;
        try { boundedImage.WriteByte(1); } catch (IOException) { imageBlocked = true; }
        if (!imageBlocked || boundedImage.Length != 0) throw new Exception("PNG output cap failed");
        if (Encoding.UTF8.GetByteCount("備份") != 6) throw new Exception("UTF-8 byte sizing failed");
        using var firstFile = new MemoryStream(new byte[3]);
        using var secondFile = new MemoryStream(new byte[4]);
        using var firstTarget = new MemoryStream();
        using var secondTarget = new MemoryStream();
        total = 99_999_995;
        Limits.Copy(firstFile, firstTarget, ref total);
        bool batchBlocked = false;
        try { Limits.Copy(secondFile, secondTarget, ref total); } catch (IOException) { batchBlocked = true; }
        if (!batchBlocked || firstTarget.Length != 3 || secondTarget.Length != 0) throw new Exception("Multi-file total failed");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-result.txt"), "PASS: exact 100 MB rejected; aggregate cap; below-limit copy; PNG output cap; UTF-8 bytes; multi-file cumulative cap.\n");
    }
}


