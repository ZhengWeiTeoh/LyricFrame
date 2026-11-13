using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

sealed class MusicDesktop : ApplicationContext
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(int action, int parameter, StringBuilder value, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(int action, int parameter, string value, int flags);
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicDesktop");
    readonly string artwork = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "furina.jpg");
    static readonly string Startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Music Desktop.lnk");
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 17847);
    readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
    readonly Dictionary<string, SongLyrics> cache = new Dictionary<string, SongLyrics>();
    readonly object tracksLock = new object();
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly ToolStripMenuItem startupItem;
    string background, original, backup, activeKey, frameKey;
    bool applied;
    volatile bool closing;
    int slot;
    Image image;
    string loadedImage;
    Task<SongLyrics> lookup;
    CancellationTokenSource cancellation;
    SongLyrics lyrics;
    DateTime retryAt, pageAt;
    double plainTime;
    string lastError;
    string displayedLine = "", displayedNext = "", outgoingLine = "";
    long transitionAt;
    const int TransitionMs = 320;
    DesktopSurface surface;
    Bitmap scaledArt;
    string scaledKey, chromeKey;
    bool surfaceAttempted;
    string selectedDisplay;
    string attachedDisplay;

    sealed class Track
    {
        public string title { get; set; }
        public string artist { get; set; }
        public string album { get; set; }
        public string next { get; set; }
        public int tab { get; set; }
        public double position { get; set; }
        public double duration { get; set; }
        public double rate { get; set; }
        public bool playing { get; set; }
        public DateTime received { get; set; }
    }

    sealed class Record
    {
        public string trackName { get; set; }
        public string artistName { get; set; }
        public double duration { get; set; }
        public bool instrumental { get; set; }
        public string syncedLyrics { get; set; }
        public string plainLyrics { get; set; }
        public string lyrics { get; set; }
    }

    sealed class SongLyrics
    {
        public readonly List<KeyValuePair<double, string>> Timed = new List<KeyValuePair<double, string>>();
        public string[] Plain = new string[0];
        public string Status = "";
        public bool Retry;
    }

    [STAThread]
    static void Main(string[] arguments)
    {
        if (arguments.Length == 1 && arguments[0] == "--enable-startup")
        {
            try { SetStartup(true); }
            catch (Exception error) { MessageBox.Show(error.Message, "Music Desktop"); Environment.ExitCode = 1; }
            return;
        }
        bool first;
        using (var instance = new Mutex(true, @"Local\MusicDesktop-Standalone", out first))
        {
            if (!first) return;
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            try { Application.Run(new MusicDesktop()); }
            catch (Exception error) { MessageBox.Show(error.Message, "Music Desktop"); }
        }
    }

    MusicDesktop()
    {
        Directory.CreateDirectory(folder);
        var preference = Path.Combine(folder, "background.txt");
        var displayPreference = Path.Combine(folder, "display.txt");
        selectedDisplay = File.Exists(displayPreference) ? File.ReadAllText(displayPreference).Trim() : "";
        background = File.Exists(preference) ? File.ReadAllText(preference) : artwork;
        if (!File.Exists(background)) background = artwork;
        var previous = new StringBuilder(4096);
        if (!SystemParametersInfo(0x73, previous.Capacity, previous, 0))
            throw new InvalidOperationException("Could not read the Windows wallpaper.");
        original = previous.ToString();
        var oldFrame = Path.GetFileName(original).StartsWith("wallpaper-", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(Path.GetDirectoryName(original) ?? "").StartsWith("MusicDesktop-", StringComparison.OrdinalIgnoreCase);
        if (oldFrame) original = File.Exists(artwork) ? artwork : "";
        if (File.Exists(original) && !original.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            backup = Path.Combine(folder, "previous" + Path.GetExtension(original));
            File.Copy(original, backup, true);
        }
        else if (original.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            backup = Directory.GetFiles(folder, "previous.*").FirstOrDefault();
        }

        listener.Start();
        Serve();
        var menu = new ContextMenuStrip();
        var change = menu.Items.Add("Change background");
        change.Click += delegate { ChooseBackground(preference); };
        var displays = (ToolStripMenuItem)menu.Items.Add("Display");
        displays.DropDownOpening += delegate
        {
            while (displays.DropDownItems.Count > 0) displays.DropDownItems[0].Dispose();
            foreach (var screen in Screen.AllScreens)
            {
                var device = screen.DeviceName;
                var number = Regex.Match(device, @"\d+$").Value;
                var label = "Display " + number + " (" + screen.Bounds.Width + " × " + screen.Bounds.Height + ")" +
                    (screen.Primary ? " — primary" : "");
                var item = (ToolStripMenuItem)displays.DropDownItems.Add(label);
                item.Checked = TargetScreen().DeviceName == device;
                item.Click += delegate
                {
                    try
                    {
                        File.WriteAllText(displayPreference, device);
                        selectedDisplay = device;
                        surfaceAttempted = false;
                        frameKey = null;
                        Tick();
                        if (surface == null)
                            tray.ShowBalloonTip(3500, "LyricFrame", "Could not attach to this display. Try reopening LyricFrame.", ToolTipIcon.Warning);
                    }
                    catch (Exception error) { MessageBox.Show(error.Message, "LyricFrame"); }
                };
            }
        };
        startupItem = (ToolStripMenuItem)menu.Items.Add("Start with Windows");
        startupItem.Checked = File.Exists(Startup);
        startupItem.Click += delegate { ToggleStartup(); };
        menu.Items.Add("Quit LyricFrame").Click += delegate { ExitThread(); };
        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Text = "LyricFrame";
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ChooseBackground(preference); };
        tray.Visible = true;
        timer.Interval = 500;
        timer.Tick += delegate { Tick(); };
        timer.Start();
        Tick();
    }

    void ToggleStartup()
    {
        try { SetStartup(!File.Exists(Startup)); }
        catch (Exception error) { MessageBox.Show(error.Message, "Music Desktop"); }
        startupItem.Checked = File.Exists(Startup);
    }

    static void SetStartup(bool enabled)
    {
        if (!enabled) { File.Delete(Startup); return; }
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        object link = null;
        try
        {
            link = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] {Startup});
            var type = link.GetType();
            type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] {Application.ExecutablePath});
            type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] {AppDomain.CurrentDomain.BaseDirectory});
            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
        }
                finally
                {
                    if (link != null) Marshal.FinalReleaseComObject(link);
                    Marshal.FinalReleaseComObject(shell);
                }
    }

    void ChooseBackground(string preference)
    {
        using (var dialog = new OpenFileDialog {Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp", RestoreDirectory = true})
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;
            try
            {
                using (Image.FromFile(dialog.FileName)) { }
                File.WriteAllText(preference, dialog.FileName);
                background = dialog.FileName;
                loadedImage = null;
                scaledKey = null;
                frameKey = null;
                Tick();
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Music Desktop"); }
        }
    }

    async void Serve()
    {
        while (!closing)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            Receive(client);
        }
    }

    async void Receive(TcpClient client)
    {
        using (client)
        using (var timeout = new CancellationTokenSource(2500))
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                using (var bytes = new MemoryStream())
                {
                    int bodyAt = -1, length = 0;
                    while (bytes.Length < 16384)
                    {
                        var count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                        if (count == 0) return;
                        bytes.Write(buffer, 0, count);
                        var raw = bytes.GetBuffer();
                        if (bodyAt < 0)
                        {
                            var headers = Encoding.ASCII.GetString(raw, 0, (int)bytes.Length);
                            var end = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (end < 0) continue;
                            var lines = headers.Substring(0, end).Split(new[] {"\r\n"}, StringSplitOptions.None);
                            var fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var line in lines.Skip(1))
                            {
                                int separator = line.IndexOf(':');
                                if (separator > 0) fields[line.Substring(0, separator)] = line.Substring(separator + 1).Trim();
                            }
                            string host, origin, size, contentType;
                            if (!lines[0].StartsWith("POST /track HTTP/", StringComparison.Ordinal) ||
                                !fields.TryGetValue("Host", out host) || host != "127.0.0.1:17847" ||
                                (fields.TryGetValue("Origin", out origin) && !Regex.IsMatch(origin, @"\Achrome-extension://[a-p]{32}\z")) ||
                                !fields.TryGetValue("Content-Type", out contentType) || contentType != "application/json" ||
                                !fields.TryGetValue("Content-Length", out size) || !int.TryParse(size, out length) || length < 1 || length > 8192) return;
                            bodyAt = end + 4;
                        }
                        if (bytes.Length < bodyAt + length) continue;
                        var track = new JavaScriptSerializer().Deserialize<Track>(Encoding.UTF8.GetString(raw, bodyAt, length));
                        if (track == null || track.title == null || (track.playing && string.IsNullOrWhiteSpace(track.title)) || track.title.Length > 500 ||
                            new[] {track.artist, track.album, track.next}.Any(value => value == null || value.Length > 500) ||
                            double.IsNaN(track.position) || double.IsInfinity(track.position) ||
                            double.IsNaN(track.duration) || double.IsInfinity(track.duration) ||
                            double.IsNaN(track.rate) || double.IsInfinity(track.rate) ||
                            track.position < 0 || track.duration < 0 || track.rate <= 0) return;
                        track.received = DateTime.UtcNow;
                        lock (tracksLock)
                        {
                            if (track.playing) tracks[track.tab] = track;
                            else tracks.Remove(track.tab);
                        }
                        var response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(response, 0, response.Length, timeout.Token).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is SocketException || error is OperationCanceledException ||
                      error is ArgumentException || error is InvalidOperationException)) System.Diagnostics.Debug.WriteLine(error);
            }
        }
    }

    Track Current()
    {
        lock (tracksLock)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-18);
            foreach (var id in tracks.Where(pair => pair.Value.received < cutoff).Select(pair => pair.Key).ToArray()) tracks.Remove(id);
            return tracks.Values.OrderByDescending(track => track.playing).ThenByDescending(track => track.received).FirstOrDefault();
        }
    }

    void Tick()
    {
        try
        {
            var track = Current();
            var key = track == null ? "" : json.Serialize(new[] {track.title, track.artist, track.album, Math.Round(track.duration).ToString()});
            var now = DateTime.UtcNow;
            if (key != activeKey)
            {
                CancelLookup();
                activeKey = key;
                cache.TryGetValue(key, out lyrics);
                retryAt = DateTime.MinValue;
                plainTime = 0;
                pageAt = now;
            }
            if (lookup != null && lookup.IsCompleted)
            {
                lyrics = lookup.Status == TaskStatus.RanToCompletion ? lookup.Result : new SongLyrics {Status = "Lyrics sources unavailable", Retry = true};
                lookup = null;
                cancellation.Dispose();
                cancellation = null;
                pageAt = now;
                if (lyrics.Retry) retryAt = now.AddSeconds(65);
                else if (lyrics.Timed.Count > 0 || lyrics.Plain.Length > 0)
                {
                    if (cache.Count >= 64) cache.Clear();
                    cache[key] = lyrics;
                }
            }
            if (track != null && lookup == null && (lyrics == null || lyrics.Retry && now >= retryAt))
            {
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                lookup = Task.Run(() => FindLyrics(track, token), token);
            }
            string line = "", nextLine = "";
            if (track != null && lyrics != null)
            {
                if (track.duration > 0 && lyrics.Timed.Count > 0)
                {
                    double position = track.position + (track.playing ? Math.Min(2, (now - track.received).TotalSeconds) * track.rate : 0);
                    int index = lyrics.Timed.FindLastIndex(item => item.Key <= position);
                    if (index >= 0) line = lyrics.Timed[index].Value;
                    if (index + 1 < lyrics.Timed.Count) nextLine = lyrics.Timed[index + 1].Value;
                }
                else if (lyrics.Plain.Length > 0)
                {
                    if (track.playing) plainTime += Math.Min(1, (now - pageAt).TotalSeconds);
                    int index = ((int)(plainTime / 6.5) % ((lyrics.Plain.Length + 1) / 2)) * 2;
                    line = lyrics.Plain[index];
                    if (index + 1 < lyrics.Plain.Length) nextLine = lyrics.Plain[index + 1];
                }
                else line = lyrics.Status;
            }
            else if (track != null) line = "Looking up lyrics…";
            pageAt = now;
            Paint(track == null ? "" : track.title, track == null ? "" : track.next, line, nextLine);
            lastError = null;
        }
        catch (Exception error)
        {
            if (error.Message != lastError)
            {
                lastError = error.Message;
                tray.ShowBalloonTip(3500, "Music Desktop", error.Message, ToolTipIcon.Warning);
            }
        }
    }

    void CancelLookup()
    {
        if (cancellation == null) return;
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = null;
        lookup = null;
    }

    static T Fetch<T>(string url, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Timeout = 5500;
        request.ReadWriteTimeout = 5500;
        request.UserAgent = "Music Desktop/1.0";
        using (token.Register(request.Abort))
        {
            try
            {
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return new JavaScriptSerializer().Deserialize<T>(reader.ReadToEnd());
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    response.Dispose();
                    return null;
                }
                throw;
            }
        }
    }

    static string Normalize(string value) { return (value ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(); }
    static string[] TitleNames(string value)
    {
        var title = Normalize(value);
        return new[] {title, Regex.Replace(title, @"\s*\([^()]+\)\s*$", "")}
            .Concat(Regex.Matches(title, @"\(([^()]+)\)").Cast<Match>().Select(m => m.Groups[1].Value.Trim()))
            .Where(name => name.Length > 0).ToArray();
    }

    static SongLyrics Parse(Record record, bool synced)
    {
        var result = new SongLyrics();
        var timedText = record.syncedLyrics ?? "";
        var offset = Regex.Match(timedText, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        double milliseconds;
        double.TryParse(offset.Groups[1].Value, out milliseconds);
        var words = new List<string>();
        foreach (var row in timedText.Split('\n'))
        {
            var stamps = Regex.Matches(row, @"\[(\d+):(\d{2})(?:[.:](\d{1,3}))?\]");
            if (stamps.Count == 0) continue;
            var text = Regex.Replace(row, @"\[\d+:\d{2}(?:[.:]\d{1,3})?\]", "").Trim();
            if (text.Length > 0) words.Add(text);
            if (!synced) continue;
            foreach (Match stamp in stamps)
            {
                double minute, second;
                if (!double.TryParse(stamp.Groups[1].Value, out minute) || !double.TryParse(stamp.Groups[2].Value, out second)) continue;
                var digits = stamp.Groups[3].Value;
                double fraction = digits.Length == 0 ? 0 : int.Parse(digits) / Math.Pow(10, digits.Length);
                result.Timed.Add(new KeyValuePair<double,string>(minute * 60 + second + fraction - milliseconds / 1000, text));
            }
        }
        result.Timed.Sort((left, right) => left.Key.CompareTo(right.Key));
        var plain = record.plainLyrics ?? record.lyrics;
        result.Plain = string.IsNullOrWhiteSpace(plain) ? words.ToArray() :
            plain.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        return result;
    }

    static SongLyrics FindLyrics(Track track, CancellationToken token)
    {
        var artist = Regex.Replace(track.artist, @"\s+-\s+Topic$", "", RegexOptions.IgnoreCase);
        var baseTitle = Regex.Replace(track.title, @"\s*[\[(](?:live(?:\s+[^)\]]*)?|现场版|現場版)[)\]]\s*$", "", RegexOptions.IgnoreCase).Trim();
        bool failed = false, limited = false;
        var encode = new Func<string,string>(Uri.EscapeDataString);
        try
        {
            if (artist.Length > 0)
            {
                string query = "track_name=" + encode(track.title) + "&artist_name=" + encode(artist);
                if (track.album.Length > 0) query += "&album_name=" + encode(track.album);
                if (track.duration >= 1 && track.duration <= 3600) query += "&duration=" + Math.Round(track.duration).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var record = Fetch<Record>("https://lrclib.net/api/get?" + query, token);
                if (record != null && (record.instrumental || record.syncedLyrics != null || record.plainLyrics != null))
                    return record.instrumental ? new SongLyrics() : Parse(record, true);
                if (token.WaitHandle.WaitOne(400)) token.ThrowIfCancellationRequested();
            }
        }
        catch (WebException error)
        {
            failed = true;
            using (var response = error.Response as HttpWebResponse)
                limited = response != null && (int)response.StatusCode == 429;
        }
        foreach (var name in new[] {track.title, baseTitle}.Where(value => value.Length > 0).Distinct())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!limited)
                {
                    var records = Fetch<Record[]>("https://lrclib.net/api/search?q=" + encode(name), token) ?? new Record[0];
                    var matching = records.Where(record => TitleNames(record.trackName).Intersect(TitleNames(name)).Any() &&
                        (record.syncedLyrics != null || record.plainLyrics != null)).ToArray();
                    var credited = matching.Where(record => artist.Length > 0 && Normalize(record.artistName) == Normalize(artist))
                        .OrderBy(record => Math.Abs(record.duration - track.duration)).FirstOrDefault();
                    var close = matching.Where(record => track.duration > 0 && Math.Abs(record.duration - track.duration) <= 2).ToArray();
                    var recordMatch = credited ?? (close.Select(record => Normalize(record.artistName)).Distinct().Count() == 1 ? close[0] : null);
                    if (recordMatch != null) return Parse(recordMatch, name == track.title && track.duration > 0 && Math.Abs(recordMatch.duration - track.duration) <= 8);
                    if (token.WaitHandle.WaitOne(400)) token.ThrowIfCancellationRequested();
                }
            }
            catch (WebException error)
            {
                failed = true;
                using (var response = error.Response as HttpWebResponse)
                    limited = response != null && (int)response.StatusCode == 429;
            }
            if (artist.Length == 0) continue;
            try
            {
                var fallback = Fetch<Record>("https://api.lyrics.ovh/v1/" + encode(artist) + "/" + encode(name), token);
                if (fallback != null && !string.IsNullOrWhiteSpace(fallback.lyrics)) return Parse(fallback, false);
            }
            catch (WebException error)
            {
                failed = true;
                if (error.Response != null) error.Response.Close();
            }
        }
        return new SongLyrics {Status = failed ? "Lyrics sources unavailable · retrying" : "Lyrics not found for this song", Retry = failed};
    }

    static void Draw(Graphics graphics, string text, float size, RectangleF bounds, bool right, bool bold)
    {
        if (text.Length == 0) return;
        using (var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
        {
            var rect = Rectangle.Round(bounds);
            var flags = TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak |
                TextFormatFlags.EndEllipsis | (right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var shadow = rect;
            shadow.Offset(1, 2);
            TextRenderer.DrawText(graphics, text, font, shadow, Color.FromArgb(25, 30, 42), flags);
            TextRenderer.DrawText(graphics, text, font, rect, Color.White, flags);
        }
    }

    Screen TargetScreen()
    {
        return Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == selectedDisplay) ??
            Screen.AllScreens.FirstOrDefault(screen => !screen.Primary) ?? Screen.PrimaryScreen;
    }

    void Paint(string title, string next, string line, string nextLine)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (line != displayedLine || nextLine != displayedNext)
        {
            outgoingLine = displayedLine;
            displayedLine = line;
            displayedNext = nextLine;
            transitionAt = outgoingLine.Length > 0 && line.Length > 0 && title.Length > 0 ? now : 0;
        }
        double progress = transitionAt == 0 ? 1 :
            Math.Min(1, (now - transitionAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / TransitionMs);
        var target = TargetScreen();
        if (target.DeviceName != attachedDisplay)
        {
            if (surface != null) surface.Dispose();
            surface = null;
            surfaceAttempted = false;
            attachedDisplay = target.DeviceName;
            frameKey = null;
        }
        if (!surfaceAttempted)
        {
            surfaceAttempted = true;
            surface = DesktopSurface.Attach(target.Bounds);
        }
        if (surface != null && !surface.Attached)
        {
            surface.Dispose();
            surface = null;
            frameKey = null;
        }
        var interval = surface != null && progress < 1 ? 16 : 500;
        if (timer.Interval != interval) timer.Interval = interval;
        if (surface == null) progress = 1;
        var clock = DateTime.Now.ToString("HH:mm");
        var bounds = target.Bounds;
        float w = bounds.Width, h = bounds.Height;
        float aspect = w / h;
        var aspects = Screen.AllScreens.Select(screen => (float)screen.Bounds.Width / screen.Bounds.Height).ToArray();
        // Windows Fill center-crops the image on displays with a different aspect ratio.
        float insetX = surface != null ? 0 : Math.Max(0, (1 - aspects.Min() / aspect) / 2);
        float insetY = surface != null ? 0 : Math.Max(0, (1 - aspect / aspects.Max()) / 2);
        var key = json.Serialize(new[] {background, bounds.Size.ToString(), insetX.ToString(), insetY.ToString(), clock, title, next, line, nextLine,
            progress < 1 ? now.ToString() : ""});
        if (key == frameKey) return;
        if (background != loadedImage)
        {
            var source = File.Exists(background) ? background : artwork;
            Bitmap replacement;
            if (File.Exists(source))
            {
                using (var file = Image.FromFile(source)) replacement = new Bitmap(file);
            }
            else
            {
                replacement = new Bitmap(960, 540);
                using (var canvas = Graphics.FromImage(replacement))
                using (var gradient = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 960, 540), Color.FromArgb(38, 62, 96), Color.FromArgb(13, 19, 36), 125f))
                    canvas.FillRectangle(gradient, 0, 0, 960, 540);
            }
            if (image != null) image.Dispose();
            image = replacement;
            loadedImage = background;
        }
        var artKey = background + "|" + bounds.Size;
        if (scaledArt == null || scaledKey != artKey)
        {
            if (scaledArt != null) scaledArt.Dispose();
            scaledArt = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            var scale = Math.Max(w / image.Width, h / image.Height);
            var iw = image.Width * scale;
            var ih = image.Height * scale;
            using (var canvas = Graphics.FromImage(scaledArt))
            {
                canvas.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                canvas.DrawImage(image, (w - iw) / 2, (h - ih) / 2, iw, ih);
            }
            scaledKey = artKey;
        }
        Action<Graphics> draw = graphics =>
        {
            var clipBounds = Rectangle.Intersect(Rectangle.Ceiling(graphics.ClipBounds), new Rectangle(Point.Empty, bounds.Size));
            graphics.DrawImage(scaledArt, clipBounds, clipBounds, GraphicsUnit.Pixel);
            Draw(graphics, clock, h * .061f, new RectangleF(w * (.04f + insetX), h * (.045f + insetY), w * .32f, h * .11f), false, true);
            Draw(graphics, title, h * .029f, new RectangleF(w * .52f, h * (.052f + insetY), w * (.44f - insetX), h * .08f), true, true);
            Draw(graphics, next.Length == 0 ? "" : "Next: " + next, h * .020f, new RectangleF(w * .52f, h * (.127f + insetY), w * (.44f - insetX), h * .06f), true, false);
            var currentBounds = new RectangleF(w * .41f, h * (.828f - insetY), w * (.55f - insetX), h * .083f);
            var nextBounds = new RectangleF(w * .41f, h * (.910f - insetY), w * (.55f - insetX), h * .056f);
            if (progress < 1)
            {
                var oldBounds = currentBounds;
                oldBounds.Offset(0, -h * .082f * (float)progress);
                var clip = graphics.Save();
                graphics.IntersectClip(RectangleF.FromLTRB(currentBounds.Left, currentBounds.Top, nextBounds.Right, nextBounds.Bottom));
                Draw(graphics, outgoingLine, h * .033f, oldBounds, true, true);
                graphics.Restore(clip);
                currentBounds.Offset(0, h * .082f * (float)(1 - progress));
                nextBounds.Offset(0, h * .026f * (float)(1 - progress));
            }
            Draw(graphics, line, h * .033f, currentBounds, true, true);
            if (progress >= .65) Draw(graphics, nextLine, h * .023f, nextBounds, true, false);
        };
        if (surface != null)
        {
            var header = json.Serialize(new[] {artKey, clock, title, next});
            var dirty = header == chromeKey && frameKey != null
                ? Rectangle.FromLTRB((int)(w * .40f), (int)(h * .82f), bounds.Width, bounds.Height)
                : new Rectangle(Point.Empty, bounds.Size);
            surface.Render(bounds, dirty, draw);
            chromeKey = header;
            frameKey = key;
            return;
        }
        if (Screen.AllScreens.Length > 1) return;
        using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
        {
            using (var graphics = Graphics.FromImage(bitmap)) draw(graphics);
            var path = Path.Combine(folder, "wallpaper-" + (1 - slot) + ".bmp");
            bitmap.Save(path, ImageFormat.Bmp);
            if (!SystemParametersInfo(0x14, 0, path, 2)) throw new InvalidOperationException("Windows rejected the wallpaper update.");
            slot = 1 - slot;
            applied = true;
            frameKey = key;
        }
    }

    protected override void ExitThreadCore()
    {
        closing = true;
        timer.Dispose();
        if (surface != null) surface.Dispose();
        if (scaledArt != null) scaledArt.Dispose();
        listener.Stop();
        CancelLookup();
        if (image != null) image.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip.Dispose();
        tray.Dispose();
        if (applied)
        {
            var restore = File.Exists(original) &&
                !original.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !original.EndsWith(@"\Themes\TranscodedWallpaper", StringComparison.OrdinalIgnoreCase) ? original : backup ?? "";
            SystemParametersInfo(0x14, 0, restore, 3);
        }
        base.ExitThreadCore();
    }
}



