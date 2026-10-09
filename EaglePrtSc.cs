using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Native
{
    internal const uint CaptureMarker = 0x45504331, TestMarker = 0x45505431;
    internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct KeyData { public uint vk, scan, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int x,y; public uint data,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyInput { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyInput key; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public InputUnion data; }
    [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll",SetLastError=true)] internal static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(IntPtr window,uint affinity);
    internal static bool Modifiers() { foreach(int k in new int[] {16,17,18,91,92}) if((GetAsyncKeyState(k)&0x8000)!=0) return true; return false; }
    internal static void SendPrint(bool down, uint marker)
    {
        Input i=new Input(); i.type=1; i.data.key.vk=0x2c; i.data.key.scan=0x37;
        i.data.key.flags=(uint)(down?1:3); i.data.key.extra=new UIntPtr(marker);
        if(SendInput(1,new Input[]{i},Marshal.SizeOf(typeof(Input)))!=1) throw new System.ComponentModel.Win32Exception();
    }
    internal static void Print(uint marker) { SendPrint(true,marker); SendPrint(false,marker); }
}

internal sealed class ImportPendingException : Exception
{
    internal readonly int Delay;
    internal ImportPendingException(int delay=5000){Delay=delay;}
}
internal sealed class ImportVerificationRequired : Exception
{
    internal readonly Action Verify;
    internal ImportVerificationRequired(Action verify){Verify=verify;}
}
internal sealed class ImportCancelledException : IOException
{
    internal ImportCancelledException():base("此素材已在 Eagle 删除，停止重传；本地原件已保留。"){}
}

internal static class ImportProgress
{
    static readonly object Gate=new object();
    static readonly Dictionary<string,string> Stages=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    static string latest;static DateTime latestTime=DateTime.MinValue;
    internal static Action<string,string> Changed;
    internal static string Label(string path)
    {
        string name=Path.GetFileName(path);var match=System.Text.RegularExpressions.Regex.Match(name,@"^(?:Recording|Screenshot)-(\d{4})-(\d{2})-(\d{2})_(\d{2})-(\d{2})-(\d{2})-");
        return match.Success?match.Groups[4].Value+":"+match.Groups[5].Value+":"+match.Groups[6].Value+(name.StartsWith("Recording-")?" 视频":" 截图"):Path.GetFileNameWithoutExtension(path);
    }
    static DateTime CaptureTime(string path)
    {
        string name=Path.GetFileName(path);var match=System.Text.RegularExpressions.Regex.Match(name,@"^(?:Recording|Screenshot)-(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3})-");DateTime value;
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value,"yyyy-MM-dd_HH-mm-ss-fff",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out value)?value:DateTime.Now;
    }
    internal static bool IsLatest(string path){lock(Gate)return string.Equals(latest,path,StringComparison.OrdinalIgnoreCase);}
    internal static string Summary{get{lock(Gate)return latest==null?"":Label(latest)+"："+Stages[latest];}}
    internal static void ResetForTests(){lock(Gate){Stages.Clear();latest=null;latestTime=DateTime.MinValue;}}
    internal static void Set(string path,string stage)
    {
        bool changed;lock(Gate)
        {
            string previous;changed=!Stages.TryGetValue(path,out previous) || previous!=stage;Stages[path]=stage;
            DateTime captured=CaptureTime(path);if(latest==null || captured>=latestTime){latest=path;latestTime=captured;}
        }
        var callback=Changed;if(changed && callback!=null)callback(path,stage);
    }
}

// The API only returns success. Eagle's own Add log provides the immutable ID
// before delayed NAS metadata/indexing completes; filenames and timestamps must match.
internal static class EagleReceipt
{
    internal static string TestLogPath;
    internal static string FindId(string path,DateTime submittedUtc)
    {
        string log=TestLogPath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Eagle","log.log");
        if(submittedUtc==DateTime.MinValue || !File.Exists(log))return null;
        try
        {
            string text;
            using(var file=File.Open(log,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {file.Seek(Math.Max(0,file.Length-131072),SeekOrigin.Begin);using(var reader=new StreamReader(file,Encoding.UTF8))text=reader.ReadToEnd();}
            string pattern=@"^\[(?<time>[^\]]+)\].*\[bg\] Add \["+System.Text.RegularExpressions.Regex.Escape(Path.GetFileName(path))+@"\]\((?<id>[A-Za-z0-9]+)\)";
            string id=null;
            foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,pattern,System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                DateTime stamp;if(DateTime.TryParseExact(match.Groups["time"].Value,"yyyy-MM-dd HH:mm:ss.fff",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeLocal,out stamp) && stamp.ToUniversalTime()>=submittedUtc)id=match.Groups["id"].Value;
            }
            return id;
        }
        catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
    }
}

// Submission/polling and expensive NAS verification have separate serial workers.
// A pending copy returns to the scheduler instead of blocking newer captures.
internal sealed class SerialImportQueue : IDisposable
{
    readonly object gate=new object();
    readonly Dictionary<string,DateTime> pending=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
    readonly Queue<KeyValuePair<string,Action>> verification=new Queue<KeyValuePair<string,Action>>();
    readonly HashSet<string> active=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly AutoResetEvent available=new AutoResetEvent(false);
    readonly AutoResetEvent verifyAvailable=new AutoResetEvent(false);
    readonly Action<string> import;
    readonly Action<string,Exception> completed;
    readonly int storageRetryMilliseconds;
    bool stopping;
    internal SerialImportQueue(Action<string> import,Action<string,Exception> completed,int storageRetryMilliseconds=30000)
    {
        this.import=import;this.completed=completed;this.storageRetryMilliseconds=Math.Max(1,storageRetryMilliseconds);
        var worker=new Thread(Run);worker.IsBackground=true;worker.Name="Eagle NAS import queue";worker.Start();
        var verifier=new Thread(VerifyRun);verifier.IsBackground=true;verifier.Name="Eagle durable verification";verifier.Start();
    }
    internal int Count {get{lock(gate)return active.Count;}}
    internal bool Enqueue(string path)
    {
        lock(gate)
        {
            if(stopping || !active.Add(path)) return false;
            pending.Add(path,DateTime.UtcNow);available.Set();return true;
        }
    }
    void Run()
    {
        while(true)
        {
            string path=null;
            lock(gate){if(stopping)return;DateTime first=DateTime.MaxValue;foreach(var job in pending)if(job.Value<=DateTime.UtcNow && job.Value<first){path=job.Key;first=job.Value;}if(path!=null)pending.Remove(path);}
            if(path==null){available.WaitOne(250);continue;}
            Execute(path,delegate{import(path);},true);
        }
    }
    void VerifyRun()
    {
        while(true)
        {
            KeyValuePair<string,Action> job=new KeyValuePair<string,Action>();
            lock(gate){if(stopping)return;if(verification.Count>0)job=verification.Dequeue();}
            if(job.Key==null){verifyAvailable.WaitOne(250);continue;}
            Execute(job.Key,job.Value,false);
        }
    }
    void Execute(string path,Action action,bool canTransfer)
    {
        Exception failure=null;
        try{action();}
        catch(ImportPendingException wait){lock(gate){if(!stopping){pending[path]=DateTime.UtcNow.AddMilliseconds(wait.Delay);available.Set();}}return;}
        catch(ImportVerificationRequired next){if(canTransfer){lock(gate){verification.Enqueue(new KeyValuePair<string,Action>(path,next.Verify));verifyAvailable.Set();}return;}failure=next;}
        catch(WebException ex){Program.Log("IMPORT_CONNECTION_WAIT "+Path.GetFileName(path)+" "+ex.Status);lock(gate){if(!stopping){pending[path]=DateTime.UtcNow.AddSeconds(30);available.Set();}}return;}
        catch(IOException ex)
        {
            int code=Marshal.GetHRForException(ex)&0xffff;
            if(code==32 || code==33 || code==53 || code==64 || code==67 || code==121 || code==1237)
            {
                ImportProgress.Set(path,"等待 NAS 连接或文件释放");Program.Log("IMPORT_STORAGE_WAIT "+Path.GetFileName(path)+" "+ex.Message);
                lock(gate){if(!stopping){pending[path]=DateTime.UtcNow.AddMilliseconds(storageRetryMilliseconds);available.Set();}}return;
            }
            failure=ex;
        }
        catch(Exception ex){failure=ex;}
        lock(gate)active.Remove(path);
        try{completed(path,failure);}catch(Exception ex){Program.Log("QUEUE_CALLBACK_FAILED "+ex.Message);}
    }
    public void Dispose(){lock(gate){stopping=true;available.Set();verifyAvailable.Set();}}
}

internal sealed class FeedbackToast : Form
{
    readonly Label text=new Label();
    readonly System.Windows.Forms.Timer dismiss=new System.Windows.Forms.Timer();
    internal FeedbackToast()
    {
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
        ClientSize=new Size(350,76);BackColor=Color.FromArgb(27,36,48);Padding=new Padding(16);
        text.Dock=DockStyle.Fill;text.ForeColor=Color.White;text.Font=new Font("Microsoft YaHei UI",10);text.TextAlign=ContentAlignment.MiddleLeft;Controls.Add(text);
        dismiss.Interval=1700;dismiss.Tick+=delegate{dismiss.Stop();Hide();};
    }
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Native.SetWindowDisplayAffinity(Handle,0x11);}
    internal void Present(string message,bool error)
    {
        dismiss.Stop();dismiss.Interval=error?5000:1700;text.Text=message;BackColor=error?Color.FromArgb(120,52,38):Color.FromArgb(27,66,54);
        Rectangle screen=Screen.FromPoint(Cursor.Position).WorkingArea;Location=new Point(screen.Right-Width-24,screen.Bottom-Height-24);
        Show();dismiss.Start();
    }
    internal void BeforeCapture(){dismiss.Stop();Hide();}
    protected override void Dispose(bool disposing){if(disposing)dismiss.Dispose();base.Dispose(disposing);}
}

internal sealed class DoublePress
{
    internal bool Held, Pending, Pair;
    internal long First,PressedAt;
    bool longHandled;
    internal bool Down(long now)
    {
        if(Held) return false;
        Held=true;PressedAt=now;longHandled=false;
        if(Pending && now-First<=450) { Pending=false; Pair=true; return true; }
        Pending=true; First=now; return false;
    }
    internal bool Up() { Held=false; bool result=Pair; Pair=false; return result; }
    internal bool Expired(long now) { if(Pending && !Held && now-First>450) {Pending=false; return true;} return false; }
    internal bool LongPress(long now)
    {
        if(!Held || longHandled || now-PressedAt<800)return false;
        longHandled=true;Pending=false;Pair=false;return true;
    }
}

internal static class VerifiedCache
{
    internal static bool Delete(string path,string stored,string expectedHash,string pendingRoot)
    {
        path=Path.GetFullPath(path);pendingRoot=Path.GetFullPath(pendingRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        string name=Path.GetFileName(path),extension=Path.GetExtension(path);
        bool capture=(name.StartsWith("Screenshot-",StringComparison.OrdinalIgnoreCase) && (extension.Equals(".png",StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg",StringComparison.OrdinalIgnoreCase))) || (name.StartsWith("Recording-",StringComparison.OrdinalIgnoreCase) && extension.Equals(".mp4",StringComparison.OrdinalIgnoreCase));
        if(!capture || !string.Equals(Path.GetDirectoryName(path),pendingRoot,StringComparison.OrdinalIgnoreCase))throw new IOException("缓存清理路径不在本程序的 Pending 目录中。");
        string destination=Path.GetFullPath(stored);
        if(destination.StartsWith(pendingRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Eagle 原文件不能位于本地缓存目录内。");
        try
        {
            // API acceptance alone is insufficient. Recheck the durable file before removing any source.
            if(!File.Exists(destination) || string.IsNullOrEmpty(expectedHash) || CaptureContext.Hash(destination)!=expectedHash)return false;
            if(File.Exists(path) && (new FileInfo(path).Length!=new FileInfo(destination).Length || CaptureContext.Hash(path)!=expectedHash))return false;
            File.Delete(path);
            File.Delete(path+".submitted.json");File.Delete(path+".recording.json");File.Delete(path+".validated.json");File.Delete(path+".submitted.json.tmp");File.Delete(path+".confirmed.json.tmp");File.Delete(path+".validated.json.tmp");
            // Keep the confirmation last so a partial cleanup can be resumed after a restart.
            File.Delete(path+".confirmed.json");
            Program.Log("CACHE_DELETED "+name);return true;
        }
        catch(IOException ex){Program.Log("CACHE_CLEANUP_DEFERRED "+name+" "+ex.Message);return false;}
        catch(UnauthorizedAccessException ex){Program.Log("CACHE_CLEANUP_DEFERRED "+name+" "+ex.Message);return false;}
    }
    internal static bool DeleteConfirmed(string path)
    {
        var marker=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path+".confirmed.json",Encoding.UTF8));
        bool deleted=Delete(path,(string)marker["verifiedPath"],(string)marker["sha256"],Program.Pending);
        if(!deleted)Program.Log("CACHE_RETAINED "+Path.GetFileName(path));return deleted;
    }
}

internal static class Program
{
    internal static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    internal static readonly string Pending=Path.Combine(Root,"Pending");
    internal static readonly string LogPath=Path.Combine(Root,"activity.log");
    internal static readonly string Name="Local\\EaglePrtSc-"+Environment.UserName;
    static readonly object logLock=new object();
    internal static void Log(string message) {lock(logLock) {try{File.AppendAllText(LogPath,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+" "+message+Environment.NewLine,Encoding.UTF8);}catch{}}}
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--test-double") { Native.Print(Native.TestMarker); Thread.Sleep(150); Native.Print(Native.TestMarker); return 0; }
        if(args.Length>0 && args[0]=="--stop") {try{using(var e=EventWaitHandle.OpenExisting(Name+"-Stop")) e.Set();}catch(WaitHandleCannotBeOpenedException){} return 0;}
        if(args.Length>0 && args[0]=="--apply-video-defaults")
        {
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));var settings=SettingsStore.Snapshot();settings.FrameRate=60;settings.AutoVideoBitrate=true;
                settings.VideoMbps=settings.RecommendedVideoMbps(SystemInformation.VirtualScreen.Size);SettingsStore.Save(settings);
                Log("VIDEO_DEFAULTS_APPLIED fps=60 Mbps="+settings.VideoMbps);return 0;
            }
            catch(Exception ex){Log("VIDEO_DEFAULTS_APPLY_FAILED "+ex.Message);return 90;}
        }
        if(args.Length>0 && args[0]=="--test-queue") return TestQueue();
        if(args.Length==2 && args[0]=="--test-cache-cleanup")return TestCacheCleanup(args[1]);
        if(args.Length==2 && args[0]=="--test-import-pipeline")return ImportPipelineTests.Run(args[1]);
        if(args.Length==2 && args[0]=="--test-video-validation")return ImportPipelineTests.TestVideos(args[1]);
        if(args.Length==2 && args[0]=="--test-dependencies")return DependencyInstaller.Test(args[1],false);
        if(args.Length==2 && args[0]=="--test-dependencies-download")return DependencyInstaller.Test(args[1],true);
        if(args.Length==4 && args[0]=="--test-dependencies-archive")return DependencyInstaller.TestRealArchive(args[1],args[2],args[3]);
        if(args.Length==2 && args[0]=="--test-startup")return StartupRegistration.Test(args[1]);
        if(args.Length==2 && args[0]=="--test-bitrate")
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            return BitrateTests.Run(args[1]);
        }
        if(args.Length==2 && args[0]=="--test-settings")return SettingsTests.Run(args[1]);
        if(args.Length==2 && args[0]=="--test-color")return ColorEncodingTests.Run(args[1]);
        if(args.Length==2 && args[0]=="--test-desktop-performance")return DesktopPerformanceTests.Run(args[1]);
        if(args.Length==2 && args[0]=="--preview-settings")
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(args[1]);
            using(var form=new SettingsForm(delegate{return "● 就绪   ·   热键监听中   ·   导入队列：0";}))
            {
                form.Show();Application.DoEvents();
                for(int i=0;i<3;i++){form.PreviewTab(i);Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(args[1],"settings-"+i+".png"),ImageFormat.Png);}}
                form.PreviewTab(1,true);Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(args[1],"settings-bitrate.png"),ImageFormat.Png);}
            }
            return 0;
        }
        if(args.Length==2 && (args[0]=="--test-media" || args[0]=="--test-loopback" || args[0]=="--test-recording" || args[0]=="--test-recording-pipeline")) return RecordingTests.Run(args[0],args[1]);
        if(args.Length==2 && args[0]=="--test-audio-timeline") return AudioTimelineTests.Run(args[1]);
        if(args.Length==2 && args[0]=="--test-feedback")
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var toast=new FeedbackToast())
            {
                toast.Present("已截图，正在导入 Eagle\n待完成：2 张，可继续截图",false);
                using(var preview=new Bitmap(toast.Width,toast.Height)){toast.DrawToBitmap(preview,new Rectangle(0,0,toast.Width,toast.Height));preview.Save(args[1],ImageFormat.Png);}
            }
            return 0;
        }
        if(args.Length==2 && args[0]=="--test-import")
        {
            try {CaptureContext.ImportFile(args[1]);Log("IMPORT_TEST_PASS sourceExists="+File.Exists(args[1]));return 0;}
            catch(Exception ex){Log("IMPORT_TEST_FAIL "+ex.Message);return 10;}
        }
        if(args.Length==2 && args[0]=="--test-recording-feedback")
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var badge=new RecordingBadge())
            {
                badge.UpdateTime(DateTime.Now.AddSeconds(-65));
                using(var preview=new Bitmap(badge.Width,badge.Height)){badge.DrawToBitmap(preview,new Rectangle(0,0,badge.Width,badge.Height));preview.Save(args[1],ImageFormat.Png);}
            }
            return 0;
        }
        if(args.Length>0 && args[0]=="--test-state")
        {
            var d=new DoublePress(); if(d.Down(0)||d.Down(100)||d.Up()||!d.Down(150)||!d.Up()||d.Expired(1000)) return 1;
            d=new DoublePress(); d.Down(0); d.Up(); if(d.Expired(450)||!d.Expired(451)||d.Down(1000)) return 2;
            d.Up(); if(!d.Expired(1500)) return 3;
            d=new DoublePress(); d.Down(0); if(d.Expired(1000)||d.Down(1100)) return 4; d.Up(); if(!d.Expired(1200)) return 5;
            d=new DoublePress();d.Down(0);d.Up();if(!d.Down(100)||!d.Up())return 6;d.Down(180);d.Up();if(!d.Down(260)||!d.Up()||d.Expired(1000))return 7;
            d=new DoublePress();d.Down(0);if(d.LongPress(799)||!d.LongPress(800)||d.LongPress(1600)||d.Up()||d.Expired(2000))return 8;
            d=new DoublePress();d.Down(0);d.Up();d.Down(100);if(!d.LongPress(900)||d.Up()||d.Expired(2000))return 9;
            d.Down(2100);d.Up();if(!d.Down(2200)||!d.Up())return 10;
            d=new DoublePress();d.Down(0);if(d.LongPress(799)||d.Up()||!d.Expired(801))return 11;
            File.WriteAllText(Path.Combine(Root,"state-test.txt"),"PASS: single/double press, repeats, 450ms boundary, 800ms long press once, release suppression, double-then-hold and subsequent double press",Encoding.UTF8); return 0;
        }
        bool created; using(var mutex=new Mutex(true,Name,out created))
        {
            if(!created){if(args.Length==0 || args[0]!="--background")try{using(var e=EventWaitHandle.OpenExisting(Name+"-Settings"))e.Set();}catch(WaitHandleCannotBeOpenedException){}return 0;}
            try {
                try{Native.SetProcessDpiAwarenessContext(new IntPtr(-4));}catch(EntryPointNotFoundException){Native.SetProcessDPIAware();}
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Directory.CreateDirectory(Pending);
                foreach(string retired in Directory.GetFiles(Root,"EaglePrtSc*.retired"))
                {
                    if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(retired),@"^EaglePrtSc(?:-\d{14}-[a-f0-9]{6})?\.retired$"))continue;
                    try{if(FileVersionInfo.GetVersionInfo(retired).ProductName=="EaglePrtSc"){File.Delete(retired);Log("UPDATE_OLD_BINARY_CLEANED");}else Log("UPDATE_RETIRED_FILE_PRESERVED unknown product");}catch(Exception ex){Log("UPDATE_CLEANUP_DEFERRED "+ex.Message);}
                }
                using(var context=new CaptureContext(args.Length==0 || args[0]!="--background")) Application.Run(context);
                return 0;
            } catch(Exception ex) {Log("FATAL "+ex.Message); MessageBox.Show(ex.Message,"EaglePrtSc",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
        }
    }
    static int TestQueue()
    {
        using(var entered=new ManualResetEvent(false)) using(var release=new ManualResetEvent(false)) using(var finished=new ManualResetEvent(false))
        {
            var order=new List<string>();object gate=new object();int concurrent=0,maxConcurrent=0,errors=0,completed=0;
            using(var queue=new SerialImportQueue(delegate(string path)
            {
                int running=Interlocked.Increment(ref concurrent);lock(gate){maxConcurrent=Math.Max(maxConcurrent,running);order.Add(path);}
                try{if(path=="first"){entered.Set();if(!release.WaitOne(5000))throw new Exception("Test release timed out");}if(path=="second")throw new IOException("Simulated NAS failure");}
                finally{Interlocked.Decrement(ref concurrent);}
            },delegate(string path,Exception error){if(error!=null)Interlocked.Increment(ref errors);if(Interlocked.Increment(ref completed)==3)finished.Set();}))
            {
                try
                {
                    queue.Enqueue("first");if(!entered.WaitOne(2000))throw new Exception("Worker did not start");
                    var timing=Stopwatch.StartNew();bool second=queue.Enqueue("second"),third=queue.Enqueue("third");
                    if(!second||!third||timing.ElapsedMilliseconds>1000||queue.Count!=3)throw new Exception("New captures blocked by slow import");
                    if(queue.Enqueue("first")||queue.Enqueue("second"))throw new Exception("Duplicate queued path accepted");
                    release.Set();if(!finished.WaitOne(3000))throw new Exception("Queue failed to continue after error");
                    lock(gate)if(string.Join(",",order.ToArray())!="first,second,third"||maxConcurrent!=1||errors!=1||completed!=3||queue.Count!=0)throw new Exception("FIFO or failure isolation failed");
                    File.WriteAllText(Path.Combine(Root,"queue-test.txt"),"PASS: enqueue while NAS import is blocked; FIFO serial imports; active-path deduplication; failed import does not block next file; queue drains completely.",Encoding.UTF8);
                    return 0;
                }
                catch(Exception ex){Log("QUEUE_TEST_FAIL "+ex.Message);return 20;}
                finally{release.Set();}
            }
        }
    }
    static int TestCacheCleanup(string directory)
    {
        try
        {
            string pending=Path.Combine(directory,"Pending"),eagle=Path.Combine(directory,"Eagle");Directory.CreateDirectory(pending);Directory.CreateDirectory(eagle);
            foreach(string name in new[]{"Screenshot-test.png","Screenshot-test.jpg","Recording-test.mp4"})
            {
                string source=Path.Combine(pending,name),stored=Path.Combine(eagle,name);File.WriteAllText(source,"complete-media");string hash=CaptureContext.Hash(source);
                foreach(string suffix in new[]{".submitted.json",".confirmed.json",".recording.json"})File.WriteAllText(source+suffix,"{}");
                if(VerifiedCache.Delete(source,stored,hash,pending) || !File.Exists(source))throw new Exception("Deleted before destination existed");
                File.WriteAllText(stored,"different-media");if(VerifiedCache.Delete(source,stored,hash,pending) || !File.Exists(source))throw new Exception("Deleted a hash mismatch");
                File.Copy(source,stored,true);File.AppendAllText(source,"new-data");if(VerifiedCache.Delete(source,stored,hash,pending) || !File.Exists(source))throw new Exception("Deleted a changed source");
                File.WriteAllText(source,"complete-media");
                if(!VerifiedCache.Delete(source,stored,hash,pending) || File.Exists(source) || !File.Exists(stored) || Directory.GetFiles(pending,name+"*").Length!=0)throw new Exception("Confirmed cleanup failed");
                if(!VerifiedCache.Delete(source,stored,hash,pending))throw new Exception("Cleanup is not idempotent");
                // Simulate a crash after media deletion but before sidecar cleanup.
                File.WriteAllText(source+".confirmed.json","{}");if(!VerifiedCache.Delete(source,stored,hash,pending) || File.Exists(source+".confirmed.json"))throw new Exception("Interrupted cleanup did not resume");
            }
            string foreign=Path.Combine(directory,"Screenshot-foreign.png"),other=Path.Combine(eagle,"foreign.png");File.WriteAllText(foreign,"preserve");File.Copy(foreign,other,true);bool rejected=false;
            try{VerifiedCache.Delete(foreign,other,CaptureContext.Hash(foreign),pending);}catch(IOException){rejected=true;}
            if(!rejected || !File.Exists(foreign))throw new Exception("Cleanup escaped Pending");
            File.WriteAllText(Path.Combine(directory,"cache-cleanup-test.txt"),"PASS: PNG/JPEG/MP4 cleaned only after matching destination; missing NAS, destination mismatch and changed source retained; sidecars cleaned; interrupted cleanup resumed; foreign path rejected; Eagle originals retained",Encoding.UTF8);return 0;
        }
        catch(Exception ex){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 60;}
    }
}

internal sealed class CaptureContext : ApplicationContext
{
    readonly Control dispatcher=new Control();
    readonly NotifyIcon tray=new NotifyIcon();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly EventWaitHandle stop;
    readonly EventWaitHandle showSettings;
    SettingsForm settingsForm;
    CaptureSettings screenshotSettings;
    readonly DoublePress detector=new DoublePress();
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly Native.HookProc callback;
    readonly FeedbackToast feedback=new FeedbackToast();
    readonly RecordingBadge recordingBadge=new RecordingBadge();
    readonly SerialImportQueue imports;
    DesktopRecording recording;
    bool recordingBusy,exitRequested;
    long healthCheck;
    readonly List<DesktopRecording> finalizing=new List<DesktopRecording>();
    ToolStripMenuItem recordMenu;
    IntPtr hook;
    bool swallowed, waitingRelease, waitingClipboard,closing;
    int captureRequests;
    string deferredFeedback;
    bool deferredError;
    long deadline;
    uint sequence;
    internal CaptureContext(bool openSettings)
    {
        dispatcher.CreateControl();
        imports=new SerialImportQueue(ImportFile,ImportCompleted);
        ImportProgress.Changed=delegate(string path,string stage){Post(delegate{if(ImportProgress.IsLatest(path) && (stage=="已提交，等待 Eagle 入库" || stage=="已入库，等待缓存清理" || stage=="Eagle 索引异常，原文件已保留"))ShowFeedback(ImportProgress.Label(path)+"\n"+stage,stage=="Eagle 索引异常，原文件已保留");});};
        stop=new EventWaitHandle(false,EventResetMode.ManualReset,Program.Name+"-Stop");
        showSettings=new EventWaitHandle(false,EventResetMode.AutoReset,Program.Name+"-Settings");
        var menu=new ContextMenuStrip();
        menu.Items.Add("设置…",null,delegate{OpenSettings();});
        menu.Items.Add("双击 PrtSc → 全屏存入 Eagle（450 毫秒）").Enabled=false;
        recordMenu=new ToolStripMenuItem("长按 PrtSc → 开始录制",null,delegate{ToggleRecording();});menu.Items.Add(recordMenu);
        menu.Items.Add("查看本地截图与视频",null,delegate{Process.Start("explorer.exe",Program.Pending);});
        menu.Items.Add("重试待导入文件",null,delegate{Retry();});
        menu.Items.Add("退出",null,delegate{RequestExit();});
        tray.Icon=SystemIcons.Application; tray.Text="双击 PrtSc 保存全屏到 Eagle"; tray.ContextMenuStrip=menu; tray.Visible=true;
        tray.DoubleClick+=delegate{OpenSettings();};
        callback=OnKey; hook=Native.SetWindowsHookEx(13,callback,Native.GetModuleHandle(null),0);
        if(hook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        timer.Interval=25; timer.Tick+=Tick; timer.Start();
        Program.Log("READY version="+System.Reflection.Assembly.GetExecutingAssembly().GetName().Version+" hook="+hook+" screen="+SystemInformation.VirtualScreen);
        dispatcher.BeginInvoke((Action)delegate{DependencyInstaller.Start();RestorePending(false);if(openSettings)OpenSettings();});
    }
    void OpenSettings()
    {
        if(settingsForm==null || settingsForm.IsDisposed)settingsForm=new SettingsForm(delegate{return "● "+(recording!=null?"正在录制":recordingBusy?"准备录制":"就绪")+" · 待完成："+imports.Count+" · "+ImportProgress.Summary;});
        if(settingsForm.WindowState==FormWindowState.Minimized)settingsForm.WindowState=FormWindowState.Normal;
        settingsForm.Show();settingsForm.Activate();
    }
    IntPtr OnKey(int code,IntPtr msg,IntPtr data)
    {
        if(code<0) return Native.CallNextHookEx(hook,code,msg,data);
        var k=(Native.KeyData)Marshal.PtrToStructure(data,typeof(Native.KeyData));
        if(k.vk!=0x2c || k.extra.ToUInt64()==Native.CaptureMarker || ((k.flags&0x10)!=0 && k.extra.ToUInt64()!=Native.TestMarker)) return Native.CallNextHookEx(hook,code,msg,data);
        int m=msg.ToInt32(); bool down=m==0x100||m==0x104; bool up=m==0x101||m==0x105;
        if(down)
        {
            if(!detector.Held && Native.Modifiers()) return Native.CallNextHookEx(hook,code,msg,data);
            swallowed=true;
            // Handle a delayed timer without dropping the previous single press.
            if(detector.Expired(clock.ElapsedMilliseconds)) dispatcher.BeginInvoke((Action)ForwardSingle);
            detector.Down(clock.ElapsedMilliseconds);
            return new IntPtr(1);
        }
        if(up && swallowed)
        {
            swallowed=false;
            if(detector.LongPress(clock.ElapsedMilliseconds)) dispatcher.BeginInvoke((Action)ToggleRecording);
            if(detector.Up()) dispatcher.BeginInvoke((Action)BeginCapture);
            return new IntPtr(1);
        }
        return Native.CallNextHookEx(hook,code,msg,data);
    }
    void ForwardSingle() { try {Native.Print(Native.CaptureMarker);Program.Log("SINGLE forwarded");}catch(Exception ex){Program.Log("SINGLE error "+ex.Message);} }
    void BeginCapture()
    {
        captureRequests++;
        Program.Log("DOUBLE recognized captureRequests="+captureRequests+" importQueue="+imports.Count);
        BeginNextCapture();
    }
    void BeginNextCapture()
    {
        if(waitingRelease || waitingClipboard || captureRequests==0) return;
        captureRequests--;feedback.BeforeCapture();
        screenshotSettings=SettingsStore.Snapshot();
        waitingRelease=true;deadline=clock.ElapsedMilliseconds+2000;
    }
    void Tick(object sender,EventArgs e)
    {
        if(stop.WaitOne(0)) {RequestExit();return;}
        if(showSettings.WaitOne(0))OpenSettings();
        if(detector.LongPress(clock.ElapsedMilliseconds)) ToggleRecording();
        if(detector.Expired(clock.ElapsedMilliseconds)) ForwardSingle();
        if(recording!=null && !recordingBusy)
        {
            recordingBadge.UpdateTime(recording.StartedAt);
            if(clock.ElapsedMilliseconds>healthCheck){healthCheck=clock.ElapsedMilliseconds+1000;if(!recording.Healthy){Program.Log("RECORD_UNHEALTHY stopping");ToggleRecording();}}
        }
        BeginNextCapture();
        if(waitingRelease)
        {
            if(!Native.Modifiers() && (Native.GetAsyncKeyState(0x2c)&0x8000)==0)
            {
                waitingRelease=false;
                try {sequence=Native.GetClipboardSequenceNumber();Native.Print(Native.CaptureMarker);waitingClipboard=true;deadline=clock.ElapsedMilliseconds+3000;}
                catch(Exception ex){Fail(ex.Message);}
            }
            else if(clock.ElapsedMilliseconds>deadline) {waitingRelease=false;Fail("请松开组合键后再双击 PrtSc。");}
        }
        if(waitingClipboard)
        {
            try
            {
                if(Native.GetClipboardSequenceNumber()!=sequence && Clipboard.ContainsImage())
                {
                    using(Image img=Clipboard.GetImage())
                    {
                        Rectangle bounds=SystemInformation.VirtualScreen;
                        if(img==null || img.Width!=bounds.Width || img.Height!=bounds.Height) throw new Exception("剪贴板图像不是完整桌面，已取消导入。");
                        string path=Path.Combine(Program.Pending,"Screenshot-"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+(screenshotSettings.ImageFormat=="JPEG"?".jpg":".png"));
                        ScreenshotOutput.Save(img,path,screenshotSettings); waitingClipboard=false;
                        Program.Log("CAPTURE "+img.Width+"x"+img.Height+" "+path);
                        ImportProgress.Set(path,"等待提交");imports.Enqueue(path);
                        Program.Log("QUEUED "+Path.GetFileName(path)+" count="+imports.Count);
                        ShowFeedback(ImportProgress.Label(path)+" 已保存\n等待 Eagle 入库 · 待完成："+imports.Count,false);
                    }
                }
            }
            catch(ExternalException) { /* Clipboard temporarily locked: retry until deadline. */ }
            catch(Exception ex) {waitingClipboard=false;Fail(ex.Message);}
            if(waitingClipboard && clock.ElapsedMilliseconds>deadline) {waitingClipboard=false;Fail("Windows 未生成全屏截图。请检查 PrtSc 是否被其他截图软件占用。");}
        }
        if(!waitingRelease && !waitingClipboard && captureRequests==0 && deferredFeedback!=null)
        {
            string message=deferredFeedback;bool error=deferredError;deferredFeedback=null;ShowFeedback(message,error);
        }
    }
    void Retry()
    {
        RestorePending(true);
    }
    void RestorePending(bool explicitRetry)
    {
        var pendingFiles=new List<string>();pendingFiles.AddRange(Directory.GetFiles(Program.Pending,"Screenshot-*.png"));pendingFiles.AddRange(Directory.GetFiles(Program.Pending,"Screenshot-*.jpg"));pendingFiles.AddRange(Directory.GetFiles(Program.Pending,"Recording-*.mp4"));
        foreach(string marker in Directory.GetFiles(Program.Pending,"*.confirmed.json"))
        {
            string source=marker.Substring(0,marker.Length-".confirmed.json".Length),name=Path.GetFileName(source);
            if((name.StartsWith("Screenshot-",StringComparison.OrdinalIgnoreCase) || name.StartsWith("Recording-",StringComparison.OrdinalIgnoreCase)) && !pendingFiles.Contains(source))pendingFiles.Add(source);
        }
        string[] files=pendingFiles.ToArray();Array.Sort(files,StringComparer.OrdinalIgnoreCase);
        int restored=0;
        foreach(string file in files) if(!File.Exists(file+".cancelled.json") && imports.Enqueue(file)) restored++;
        if(restored>0){Program.Log("QUEUE_RESTORED count="+restored);ShowFeedback("已加入导入队列："+restored+" 个文件\n可以继续截图或录制",false);}
        else if(explicitRetry) ShowFeedback(imports.Count>0?"正在导入："+imports.Count+" 个文件，可继续使用":"没有待导入文件。",false);
    }
    void Post(Action action){try{if(!closing)dispatcher.BeginInvoke(action);}catch(InvalidOperationException){}}
    void ToggleRecording()
    {
        if(recordingBusy){ShowFeedback("正在准备录制，请稍候",false);return;}
        if(recording==null)
        {
            if(exitRequested)return;
            if(!DependencyInstaller.Available){if(!DependencyInstaller.Busy)DependencyInstaller.Start();ShowFeedback("录制依赖尚未就绪\n可在“连接与文件”查看安装进度；截图仍可使用",false);return;}
            CaptureSettings options=SettingsStore.Snapshot();
            recordingBusy=true;recordMenu.Enabled=false;ShowFeedback("正在准备录制\n"+options.AudioSummary+(options.DrawMouse?" · 显示鼠标":" · 隐藏鼠标"),false);
            Rectangle screen=SystemInformation.VirtualScreen;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DesktopRecording session=null;Exception failure=null;
                try{session=new DesktopRecording(screen,Program.Pending,Path.Combine(Program.Root,"RecordingWork"),false,options);session.Start();}
                catch(Exception ex){failure=ex;if(session!=null){session.Dispose();session=null;}}
                Post(delegate
                {
                    recordingBusy=false;recordMenu.Enabled=true;
                    if(failure!=null){Program.Log("RECORD_START_FAILED "+failure.ToString());ShowFeedback("录制启动失败\n"+failure.Message,true);Notify(failure.Message,true);}
                    else{recording=session;recordMenu.Text="停止录制并导入 Eagle（也可长按 PrtSc）";tray.Text="正在录制 · 长按 PrtSc 停止";recordingBadge.UpdateTime(session.StartedAt);ShowFeedback("已开始录制\n再次长按 PrtSc 停止",false);}
                    if(exitRequested)RequestExit();
                });
            });
        }
        else
        {
            DesktopRecording session=recording;recording=null;finalizing.Add(session);recordingBadge.Hide();
            recordMenu.Text="长按 PrtSc → 开始录制";tray.Text="双击截图 · 长按录制到 Eagle";
            ShowFeedback("已停止，正在保存视频\n可以继续截图，视频会排队导入",false);
            // Independent of the NAS queue: another recording can begin while this MP4 is finalized/imported.
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path=null;Exception failure=null;
                try{path=session.StopAndFinalize();}catch(Exception ex){failure=ex;}finally{session.Dispose();}
                Post(delegate
                {
                    finalizing.Remove(session);
                    if(failure==null){ImportProgress.Set(path,"等待提交");imports.Enqueue(path);ShowFeedback(ImportProgress.Label(path)+" 已保存\n等待 Eagle 入库 · 待完成："+imports.Count,false);}
                    else{Program.Log("RECORD_STOP_FAILED "+failure.ToString());ShowFeedback("视频处理失败，临时文件已保留\n"+session.WorkPath,true);Notify(failure.Message,true);}
                    if(exitRequested)RequestExit();
                });
            });
        }
    }
    void RequestExit()
    {
        exitRequested=true;
        if(recording!=null){ToggleRecording();return;}
        if(recordingBusy || finalizing.Count>0){tray.Text="正在保存录制视频，完成后退出";return;}
        ExitThread();
    }
    static string Request(string endpoint,object body)
    {
        if(TestRequest!=null)return TestRequest(endpoint,body);
        var req=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:41595/api/"+endpoint);req.Proxy=null;req.Timeout=10000;req.ReadWriteTimeout=10000;
        if(body!=null)
        {
            req.Method="POST";req.ContentType="application/json; charset=utf-8";
            byte[] bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));req.ContentLength=bytes.Length;
            using(var s=req.GetRequestStream()) s.Write(bytes,0,bytes.Length);
        }
        using(var r=req.GetResponse()) using(var reader=new StreamReader(r.GetResponseStream(),Encoding.UTF8)) return reader.ReadToEnd();
    }
    static Dictionary<string,object> Successful(string response)
    {
        var r=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(response);
        if(!r.ContainsKey("status") || (string)r["status"]!="success") throw new InvalidOperationException("Eagle 导入接口返回失败。");
        return r;
    }
    static void EnsureEagle()
    {
        try{Successful(Request("library/info",null));return;}catch{}
        if(Process.GetProcessesByName("Eagle").Length==0)
        {
            string executable=SettingsStore.Snapshot().EaglePath;
            if(!File.Exists(executable))throw new FileNotFoundException("请在设置中选择 Eagle 的安装位置。");
            Process.Start(new ProcessStartInfo(executable){UseShellExecute=true});
        }
        for(int i=0;i<20;i++){Thread.Sleep(500);try{Successful(Request("library/info",null));return;}catch{}}
        throw new Exception("Eagle 尚未就绪，文件已保存在待导入目录。");
    }
    internal static string Hash(string path)
    {
        using(var sha=SHA256.Create()) using(var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) return BitConverter.ToString(sha.ComputeHash(file));
    }
    internal static Func<string,object,string> TestRequest;
    internal static Func<DateTime> TestEagleSession;
    internal static void WriteMarker(string path,object value)
    {
        string temporary=path+".tmp";
        byte[] bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
        using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
        if(File.Exists(path))File.Replace(temporary,path,null,true);else File.Move(temporary,path);
    }
    static Dictionary<string,object> ReadMarker(string path)
    {return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));}
    static string Field(Dictionary<string,object> value,string key)
    {return value.ContainsKey(key)?Convert.ToString(value[key],System.Globalization.CultureInfo.InvariantCulture):"";}
    static DateTime Timestamp(Dictionary<string,object> value,string key)
    {
        DateTime result;return DateTime.TryParse(Field(value,key),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out result)?result.ToUniversalTime():DateTime.MinValue;
    }
    static string LibraryPath()
    {return (string)((Dictionary<string,object>)((Dictionary<string,object>)Successful(Request("library/info",null))["data"])["library"])["path"];}
    static List<Dictionary<string,object>> MatchingItems(string path,Dictionary<string,object> confirmation)
    {
        var result=new List<Dictionary<string,object>>();
        if(confirmation!=null && confirmation.ContainsKey("id"))
        {
            try{var response=Successful(Request("item/info?id="+Uri.EscapeDataString(Field(confirmation,"id")),null));var item=(Dictionary<string,object>)response["data"];if(Field(item,"id")!=Field(confirmation,"id"))throw new IOException("Eagle 返回了其他素材的 ID，已保留原文件。");result.Add(item);return result;}catch(WebException ex){if(ex.Status!=WebExceptionStatus.ProtocolError)throw;}catch(InvalidOperationException){}
            // A previously verified item may have been removed. Never recreate it automatically.
            return result;
        }
        var listed=Successful(Request("item/list?limit=100&keyword="+Uri.EscapeDataString(Path.GetFileNameWithoutExtension(path)),null));
        foreach(object obj in (System.Collections.IEnumerable)listed["data"])
        {
            var item=(Dictionary<string,object>)obj;
            if(Field(item,"name")==Path.GetFileNameWithoutExtension(path))result.Add(item);
        }
        return result;
    }
    internal static void ImportFile(string path){ImportStep(path,Program.Pending);}
    internal static void ImportStep(string path,string pendingRoot)
    {
        try
        {
            if(File.Exists(path+".cancelled.json"))throw new ImportCancelledException();
            bool historical=File.Exists(path+".confirmed.json");
            if(!File.Exists(path) && !historical)throw new IOException("本地原文件不存在，无法确认 Eagle 入库。");
            EnsureEagle();
            var confirmation=historical?ReadMarker(path+".confirmed.json"):null;
            var submission=File.Exists(path+".submitted.json")?ReadMarker(path+".submitted.json"):null;
            string library=LibraryPath();
            string boundLibrary=confirmation!=null?Field(confirmation,"libraryPath"):(submission!=null?Field(submission,"libraryPath"):"");
            if(boundLibrary.Length==0 && confirmation!=null)boundLibrary=Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Field(confirmation,"verifiedPath"))));
            if(boundLibrary.Length>0 && !string.Equals(boundLibrary,library,StringComparison.OrdinalIgnoreCase)){ImportProgress.Set(path,"等待原素材库");throw new ImportPendingException(30000);}
            EnsureHealthyIndex(path);
            if(submission!=null && Field(submission,"id").Length==0)
            {
                string acceptedId=EagleReceipt.FindId(path,Timestamp(submission,"submittedAt"));
                if(acceptedId!=null){submission["id"]=acceptedId;WriteMarker(path+".submitted.json",submission);Program.Log("IMPORT_RECEIPT "+Path.GetFileName(path)+" id="+acceptedId);}
            }
            var identity=confirmation??submission;
            if(identity!=null && DeletedItem(identity,library))CancelImport(path,identity,library);
            if(confirmation!=null && Timestamp(confirmation,"cleanupAfter")>DateTime.UtcNow){ImportProgress.Set(path,"已入库，等待缓存清理");throw new ImportPendingException(15000);}
            string expectedHash=confirmation!=null?Field(confirmation,"sha256"):(submission!=null?Field(submission,"sha256"):Hash(path));
            if(string.IsNullOrEmpty(expectedHash))throw new IOException("提交记录缺少校验值；缓存已保留，请检查记录。");
            List<Dictionary<string,object>> matches=MatchingItems(path,identity);
            foreach(var item in matches)
            {
                if(DeletedItem(item,library))CancelImport(path,item,library);
                string stored=Path.Combine(library,"images",Field(item,"id")+".info",Field(item,"name")+"."+Field(item,"ext"));
                // Listing an item is not proof its NAS copy is complete. Hashing runs on another worker.
                if(!File.Exists(stored) || (File.Exists(path) && new FileInfo(stored).Length!=new FileInfo(path).Length)){ImportProgress.Set(path,"等待 NAS 复制完成");continue;}
                if(!Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase) && !ImageReady(item))
                {
                    // Original bytes alone do not prove Eagle's image analysis finished.
                    // Do not pile refresh jobs onto a stalled Eagle/WebDAV worker.
                    ImportProgress.Set(path,"等待 Eagle 图片预览");throw new ImportPendingException(30000);
                }
                var candidate=item;
                ImportProgress.Set(path,"校验 NAS 文件");
                throw new ImportVerificationRequired(delegate
                {
                    if(File.Exists(path) && Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase))VideoValidation.Ensure(path);
                    if(!string.Equals(LibraryPath(),library,StringComparison.OrdinalIgnoreCase))throw new ImportPendingException(30000);
                    if(DeletedItem(candidate,library))CancelImport(path,candidate,library);
                    if(!File.Exists(stored) || Hash(stored)!=expectedHash)
                    {
                        bool found=false;
                        foreach(var other in matches)
                        {
                            string alternate=Path.Combine(library,"images",Field(other,"id")+".info",Field(other,"name")+"."+Field(other,"ext"));
                            if(File.Exists(alternate) && Hash(alternate)==expectedHash){stored=alternate;candidate=other;found=true;break;}
                        }
                        if(!found){ImportProgress.Set(path,"等待 NAS 文件一致");throw new ImportPendingException(30000);}
                    }
                    if(File.Exists(path) && Hash(path)!=expectedHash)throw new IOException("缓存内容与原提交不一致，禁止删除或上传为其他素材。");
                    if(Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase) && !VideoReady(candidate))
                    {
                        ImportProgress.Set(path,"等待 Eagle 解析视频");
                        // Eagle can accept/copy a video but fail its media analysis while NAS is reconnecting.
                        // Refresh the existing ID; never POST addFromPath again to fix a thumbnail/player entry.
                        string journal=File.Exists(path+".confirmed.json")?path+".confirmed.json":path+".submitted.json";
                        var state=File.Exists(journal)?ReadMarker(journal):new Dictionary<string,object>();
                        if(Timestamp(state,"thumbnailRequestedAt")==DateTime.MinValue)
                        {
                            state["thumbnailRequestedAt"]=DateTime.UtcNow.ToString("o");WriteMarker(journal,state);
                            Successful(Request("item/refreshThumbnail",new {id=candidate["id"]}));
                            Program.Log("EAGLE_MEDIA_REFRESH "+Path.GetFileName(path)+" id="+candidate["id"]);
                        }
                        throw new ImportPendingException(15000);
                    }
                    if(confirmation==null)
                    {
                        WriteMarker(path+".confirmed.json",new {id=candidate["id"],verifiedPath=stored,libraryPath=library,sha256=expectedHash,confirmedAt=DateTime.UtcNow.ToString("o"),cleanupAfter=DateTime.UtcNow.AddMinutes(2).ToString("o")});
                        Program.Log("IMPORTED_VERIFIED "+Path.GetFileName(path)+" id="+candidate["id"]+"; waiting for NAS stability before cleanup");
                        ImportProgress.Set(path,"已入库，等待缓存清理");
                        throw new ImportPendingException(15000);
                    }
                    if(Timestamp(confirmation,"cleanupAfter")>DateTime.UtcNow)throw new ImportPendingException(15000);
                    EnsureHealthyIndex(path);
                    // NAS hashing can take minutes; recheck preview state before deleting.
                    var current=MatchingItems(path,candidate);
                    if(current.Count==0)throw new ImportPendingException(30000);
                    if(DeletedItem(current[0],library))CancelImport(path,current[0],library);
                    if(Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase)?!VideoReady(current[0]):!ImageReady(current[0]))
                    {ImportProgress.Set(path,"等待 Eagle 预览恢复");throw new ImportPendingException(30000);}
                    if(!VerifiedCache.Delete(path,stored,expectedHash,pendingRoot))throw new ImportPendingException(30000);
                });
            }
            if(historical)throw new IOException("已入库素材的原文件缺失、被修改或尚未同步；保留缓存，不重新上传旧素材。");
            if(matches.Count>0)throw new ImportPendingException();
            DateTime session=TestEagleSession!=null?TestEagleSession():EagleSessionUtc();
            bool send=submission==null;
            if(submission!=null)
            {
                // A missing index in the same Eagle session never causes a second POST.
                // Persist intent before POST: a timeout has an unknown outcome, not a safe retry.
                DateTime submitted=Timestamp(submission,"submittedAt");
                // Acceptance survives application/Eagle restarts. Its absence from an
                // index may mean deletion or delayed WebDAV synchronization, never proof of failure.
                send=Field(submission,"state")=="rejected" || (Field(submission,"state")!="accepted" && boundLibrary.Length>0 && submitted!=DateTime.MinValue && session>submitted && session>Timestamp(submission,"sessionUtc") && DateTime.UtcNow-session>TimeSpan.FromMinutes(2) && DateTime.UtcNow-submitted>TimeSpan.FromMinutes(10) && LibraryReady());
            }
            if(send)
            {
                // Decoding legacy videos is isolated from the submission/polling worker, too.
                if(Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase) && !VideoValidation.IsValidated(path,expectedHash))
                    throw new ImportVerificationRequired(delegate{VideoValidation.Ensure(path);SubmitFile(path,expectedHash,library,session);throw new ImportPendingException();});
                SubmitFile(path,expectedHash,library,session);
            }
            else ImportProgress.Set(path,"已提交，等待 Eagle 入库");
            throw new ImportPendingException();
        }
        catch(WebException ex){Program.Log("IMPORT_CONNECTION_WAIT "+Path.GetFileName(path)+" "+ex.Status);throw new ImportPendingException(30000);}
    }
    static void SubmitFile(string path,string expectedHash,string library,DateTime session)
    {
        if(Hash(path)!=expectedHash)throw new IOException("待上传文件已改变，保留原提交记录。");
        // Binding is checked again after potentially lengthy video decoding.
        if(!string.Equals(LibraryPath(),library,StringComparison.OrdinalIgnoreCase))throw new ImportPendingException(30000);
        EnsureHealthyIndex(path);
        string submittedAt=DateTime.UtcNow.ToString("o"),sessionUtc=session.ToString("o");
        WriteMarker(path+".submitted.json",new {state="intent",submittedAt=submittedAt,sessionUtc=sessionUtc,libraryPath=library,sha256=expectedHash});
        bool isVideo=Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase);
        string response=Request("item/addFromPath",new {path=path,name=Path.GetFileNameWithoutExtension(path),tags=new string[]{isVideo?"屏幕录制":"全屏截图"},annotation=isVideo?"长按 PrtSc · 全屏录制 · 不录麦克风":"双击 PrtSc · Windows 原生全屏截图"});
        var result=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(response);
        bool accepted=Field(result,"status")=="success";
        WriteMarker(path+".submitted.json",new {state=accepted?"accepted":"rejected",submittedAt=submittedAt,sessionUtc=sessionUtc,libraryPath=library,sha256=expectedHash});
        if(!accepted)throw new ImportPendingException(30000);
        Program.Log("SUBMITTED "+Path.GetFileName(path));
        ImportProgress.Set(path,"已提交，等待 Eagle 入库");
    }
    static bool DeletedItem(Dictionary<string,object> item,string library)
    {
        if(item.ContainsKey("isDeleted") && Convert.ToBoolean(item["isDeleted"]))return true;
        string id=Field(item,"id");if(id.Length==0)return false;
        // On a disconnected/reconnected WebDAV drive, the API may still return an old
        // cached isDeleted=false. Respect the user's tombstone in the library itself.
        string metadata=Path.Combine(library,"images",id+".info","metadata.json");
        if(!File.Exists(metadata))return false;
        try{var disk=ReadMarker(metadata);return Field(disk,"id")==id && disk.ContainsKey("isDeleted") && Convert.ToBoolean(disk["isDeleted"]);}
        catch(IOException){throw new ImportPendingException(30000);}catch(ArgumentException){throw new ImportPendingException(30000);}
    }
    static void CancelImport(string path,Dictionary<string,object> identity,string library)
    {
        WriteMarker(path+".cancelled.json",new {id=Field(identity,"id"),libraryPath=library,cancelledAt=DateTime.UtcNow.ToString("o"),reason="deleted-in-eagle"});
        ImportProgress.Set(path,"已在 Eagle 删除，停止重传");throw new ImportCancelledException();
    }
    static bool LibraryReady()
    {string root=LibraryPath();return File.Exists(Path.Combine(root,"metadata.json")) && Directory.Exists(Path.Combine(root,"images"));}
    static string unhealthyIndex;
    static void EnsureHealthyIndex(string path)
    {
        var response=Successful(Request("item/list?limit=100",null));
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(object value in (System.Collections.IEnumerable)response["data"])
        {
            var item=(Dictionary<string,object>)value;string id=Field(item,"id");
            if(id.Length==0 || (item.ContainsKey("isDeleted") && Convert.ToBoolean(item["isDeleted"])))continue;
            if(!ids.Add(id))
            {
                if(unhealthyIndex!=id){unhealthyIndex=id;Program.Log("EAGLE_INDEX_DUPLICATE id="+id+"; imports and cache cleanup paused");}
                ImportProgress.Set(path,"Eagle 索引异常，原文件已保留");throw new ImportPendingException(30000);
            }
        }
        if(unhealthyIndex!=null){Program.Log("EAGLE_INDEX_RECOVERED");unhealthyIndex=null;}
    }
    static bool ImageReady(Dictionary<string,object> item)
    {
        double width,height;
        return double.TryParse(Field(item,"width"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out width) && width>0 && !double.IsInfinity(width) && double.TryParse(Field(item,"height"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out height) && height>0 && !double.IsInfinity(height) && (!item.ContainsKey("noThumbnail") || !Convert.ToBoolean(item["noThumbnail"])) && (!item.ContainsKey("noPreview") || !Convert.ToBoolean(item["noPreview"]));
    }
    static bool VideoReady(Dictionary<string,object> item)
    {
        double width,height,duration;
        return double.TryParse(Field(item,"width"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out width) && width>0 && double.TryParse(Field(item,"height"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out height) && height>0 && double.TryParse(Field(item,"duration"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out duration) && duration>0 && !double.IsInfinity(duration) && (!item.ContainsKey("noThumbnail") || !Convert.ToBoolean(item["noThumbnail"])) && (!item.ContainsKey("noPreview") || !Convert.ToBoolean(item["noPreview"]));
    }
    static DateTime EagleSessionUtc()
    {
        DateTime oldest=DateTime.MaxValue;Process[] processes=Process.GetProcessesByName("Eagle");
        foreach(Process process in processes)using(process){try{DateTime started=process.StartTime.ToUniversalTime();if(started<oldest)oldest=started;}catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}}
        return oldest==DateTime.MaxValue?DateTime.MinValue:oldest;
    }
    void ImportCompleted(string path,Exception error)
    {
        if(error!=null)Program.Log("IMPORT_FAILED "+Path.GetFileName(path)+" "+error.Message);
        if(closing) return;
        try{dispatcher.BeginInvoke((Action)delegate
        {
            if(error==null){ImportProgress.Set(path,"已入库，缓存已清理");if(ImportProgress.IsLatest(path))ShowFeedback(ImportProgress.Label(path)+" 已存入 Eagle\n本地缓存已清理",false);}
            else if(error is ImportCancelledException){ImportProgress.Set(path,"已在 Eagle 删除，停止重传");ShowFeedback(ImportProgress.Label(path)+" 已取消导入\n本地原件保留",false);}
            else {ImportProgress.Set(path,"导入未确认，原文件保留");ShowFeedback(ImportProgress.Label(path)+" 导入未确认\n原文件保留，可从托盘重试",true);Notify(ImportProgress.Label(path)+"\n"+error.Message,true);}
        });}catch(InvalidOperationException){}
    }
    void ShowFeedback(string message,bool error)
    {
        if(waitingRelease || waitingClipboard || captureRequests>0){deferredFeedback=message;deferredError=error;return;}
        feedback.Present(message,error);
    }
    void Fail(string message) {waitingRelease=false;waitingClipboard=false;Program.Log("ERROR "+message);ShowFeedback(message,true);Notify(message,true);}
    // Reuse the quiet visual overlay; Windows balloon notifications can play system audio.
    void Notify(string message,bool error) {ShowFeedback(message,error);}
    protected override void ExitThreadCore()
    {
        closing=true;ImportProgress.Changed=null;imports.Dispose();feedback.Dispose();recordingBadge.Dispose();if(settingsForm!=null)settingsForm.Dispose();
        timer.Stop();if(hook!=IntPtr.Zero) Native.UnhookWindowsHookEx(hook);hook=IntPtr.Zero;
        tray.Visible=false;tray.Dispose();timer.Dispose();stop.Dispose();showSettings.Dispose();dispatcher.Dispose();Program.Log("STOPPED");base.ExitThreadCore();
    }
}

internal static class ImportPipelineTests
{
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Step(string path,string pending,bool runVerification)
    {
        try{CaptureContext.ImportStep(path,pending);}
        catch(ImportPendingException){}
        catch(ImportVerificationRequired work){if(runVerification)try{work.Verify();}catch(ImportPendingException){}}
    }
    internal static int Run(string directory)
    {
        try
        {
            string pending=Path.Combine(directory,"Pending"),library=Path.Combine(directory,"Eagle");Directory.CreateDirectory(pending);Directory.CreateDirectory(Path.Combine(library,"images"));File.WriteAllText(Path.Combine(library,"metadata.json"),"{}");
            string selectedLibrary=library;int posts=0;bool timeout=false,wrongInfo=false;var items=new List<Dictionary<string,object>>();DateTime session=DateTime.UtcNow.AddHours(-1);string lastInfo=null;
            EagleReceipt.TestLogPath=Path.Combine(directory,"eagle-log.txt");File.WriteAllText(EagleReceipt.TestLogPath,"");
            CaptureContext.TestEagleSession=delegate{return session;};
            CaptureContext.TestRequest=delegate(string endpoint,object body)
            {
                if(endpoint=="library/info")return Json.Serialize(new {status="success",data=new {library=new {path=selectedLibrary}}});
                if(endpoint=="item/addFromPath"){posts++;if(timeout)throw new WebException("Simulated response lost");return "{\"status\":\"success\"}";}
                if(endpoint.StartsWith("item/info?id=")){string id=Uri.UnescapeDataString(endpoint.Substring("item/info?id=".Length));lastInfo=id;if(wrongInfo && id=="NEWRECEIPT")return Json.Serialize(new {status="success",data=items[0]});foreach(var item in items)if((string)item["id"]==id)return Json.Serialize(new {status="success",data=item});throw new WebException("Missing item",WebExceptionStatus.ProtocolError);}
                if(endpoint=="item/list?limit=100")return Json.Serialize(new {status="success",data=items});
                if(endpoint.StartsWith("item/list?")){var matches=new List<Dictionary<string,object>>();string name=Uri.UnescapeDataString(endpoint.Substring(endpoint.IndexOf("keyword=")+8));foreach(var item in items)if((string)item["name"]==name)matches.Add(item);return Json.Serialize(new {status="success",data=matches});}
                throw new Exception("Unexpected endpoint "+endpoint);
            };
            string first=Path.Combine(pending,"Screenshot-first.png"),second=Path.Combine(pending,"Screenshot-second.png");File.WriteAllText(first,"first-capture");File.WriteAllText(second,"new-capture");
            Step(first,pending,true);Require(posts==1,"First import not submitted");
            for(int i=0;i<10;i++)Step(first,pending,true);Require(posts==1,"Delayed Eagle index duplicated POST");
            // Simulate process restart: no in-memory recovery object survives.
            Step(first,pending,true);Require(posts==1,"App restart duplicated accepted submission");
            Step(second,pending,true);Require(posts==2,"New capture blocked by previous unconfirmed import");
            selectedLibrary=library+"-other";Step(first,pending,true);Require(posts==2,"Old capture imported into a different library");selectedLibrary=library;
            var itemFirst=new Dictionary<string,object>{{"id","first-id"},{"name","Screenshot-first"},{"ext","png"},{"isDeleted",false},{"width",3440},{"height",1440}};items.Add(itemFirst);
            string folder=Path.Combine(library,"images","first-id.info");Directory.CreateDirectory(folder);string stored=Path.Combine(folder,"Screenshot-first.png");File.WriteAllText(stored,"incomplete");
            Step(first,pending,true);Require(File.Exists(first) && !File.Exists(first+".confirmed.json") && posts==2,"Partial NAS file confirmed or resubmitted");
            File.Copy(first,stored,true);itemFirst["noPreview"]=true;Step(first,pending,true);Require(File.Exists(first) && !File.Exists(first+".confirmed.json") && posts==2,"Image without Eagle preview confirmed or reuploaded");
            itemFirst["noPreview"]=false;itemFirst["noThumbnail"]=true;Step(first,pending,true);Require(File.Exists(first) && !File.Exists(first+".confirmed.json"),"Image without thumbnail confirmed");
            itemFirst["noThumbnail"]=false;Step(first,pending,true);Require(File.Exists(first+".confirmed.json") && File.Exists(first),"Source deleted before NAS stability grace");
            Step(first,pending,true);Require(File.Exists(first),"Cleanup grace ignored");
            var confirmation=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(first+".confirmed.json"));confirmation["cleanupAfter"]=DateTime.UtcNow.AddMinutes(-1).ToString("o");CaptureContext.WriteMarker(first+".confirmed.json",confirmation);
            itemFirst["noPreview"]=true;Step(first,pending,true);Require(File.Exists(first) && posts==2,"Previously confirmed image deleted after preview failed");itemFirst["noPreview"]=false;
            string renamed=Path.Combine(folder,"Renamed.png");File.Move(stored,renamed);itemFirst["name"]="Renamed";
            Step(first,pending,true);Require(!File.Exists(first) && File.Exists(renamed) && Directory.GetFiles(pending,"Screenshot-first*").Length==0,"Renamed verified item did not clean sidecars");
            timeout=true;string uncertain=Path.Combine(pending,"Screenshot-uncertain.png");File.WriteAllText(uncertain,"unknown outcome");Step(uncertain,pending,true);Require(posts==3,"Timeout request not attempted");timeout=false;
            for(int i=0;i<5;i++)Step(uncertain,pending,true);Require(posts==3,"Unknown POST outcome duplicated request");
            var old=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(uncertain+".submitted.json"));old["submittedAt"]=DateTime.UtcNow.AddMinutes(-30).ToString("o");old["sessionUtc"]=DateTime.UtcNow.AddHours(-2).ToString("o");CaptureContext.WriteMarker(uncertain+".submitted.json",old);session=DateTime.UtcNow.AddMinutes(-15);
            Step(uncertain,pending,true);Require(posts==4,"Known interrupted submission was not recovered after grace");for(int i=0;i<5;i++)Step(uncertain,pending,true);Require(posts==4,"Recovered import duplicated after repeated polls");
            var legacy=new {submittedAt=DateTime.UtcNow.AddHours(-3).ToString("o"),sha256=CaptureContext.Hash(second)};CaptureContext.WriteMarker(second+".submitted.json",legacy);Step(second,pending,true);Require(posts==4,"Unbound legacy submission was blindly reimported");
            string receipt=Path.Combine(pending,"Screenshot-2026-10-07_21-10-00-100-receipt.png");File.WriteAllText(receipt,"new receipt");Step(receipt,pending,true);Require(posts==5,"Receipt fixture not submitted");
            string stamp=DateTime.Now.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss.fff"),older=DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.WriteAllText(EagleReceipt.TestLogPath,"["+older+"] [info] [bg] Add ["+Path.GetFileName(receipt)+"](STALEID)\n["+stamp+"] [info] [bg] Add [Recording-2026-10-07_19-31-14-340-d2e68a.mp4](OLDVIDEO)\n["+stamp+"] [info] [bg] Add ["+Path.GetFileName(receipt)+"](NEWRECEIPT)\n",Encoding.UTF8);
            items.Add(new Dictionary<string,object>{{"id","NEWRECEIPT"},{"name",Path.GetFileNameWithoutExtension(receipt)},{"ext","png"},{"isDeleted",false}});
            Step(receipt,pending,true);var receiptMarker=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(receipt+".submitted.json"));Require((string)receiptMarker["id"]=="NEWRECEIPT" && lastInfo=="NEWRECEIPT" && posts==5,"New receipt bound to stale video or duplicated POST");
            wrongInfo=true;bool mismatch=false;try{Step(receipt,pending,true);}catch(IOException){mismatch=true;}wrongInfo=false;Require(mismatch && File.Exists(receipt) && !File.Exists(receipt+".cancelled.json") && posts==5,"Other item ID substituted for latest recording");
            string receiptFolder=Path.Combine(library,"images","NEWRECEIPT.info");Directory.CreateDirectory(receiptFolder);File.WriteAllText(Path.Combine(receiptFolder,"metadata.json"),Json.Serialize(new {id="NEWRECEIPT",isDeleted=true}));
            bool cancelled=false;try{Step(receipt,pending,true);}catch(ImportCancelledException){cancelled=true;}Require(cancelled && File.Exists(receipt+".cancelled.json") && File.Exists(receipt) && posts==5,"Cached isDeleted=false resurrected deleted item");
            cancelled=false;try{Step(receipt,pending,true);}catch(ImportCancelledException){cancelled=true;}Require(cancelled && posts==5,"Cancelled import revived after restart");
            string deleted=Path.Combine(pending,"Screenshot-deleted.png");File.WriteAllText(deleted,"deleted");items.Add(new Dictionary<string,object>{{"id","APIDELETED"},{"name","Screenshot-deleted"},{"ext","png"},{"isDeleted",true}});
            cancelled=false;try{Step(deleted,pending,true);}catch(ImportCancelledException){cancelled=true;}Require(cancelled && posts==5 && File.Exists(deleted),"Deleted API item was added again");
            string accepted=Path.Combine(pending,"Screenshot-accepted-noindex.png");File.WriteAllText(accepted,"accepted");Step(accepted,pending,true);Require(posts==6,"Accepted fixture not submitted");
            var acceptedState=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(accepted+".submitted.json"));acceptedState["submittedAt"]=DateTime.UtcNow.AddHours(-3).ToString("o");acceptedState["sessionUtc"]=DateTime.UtcNow.AddHours(-4).ToString("o");CaptureContext.WriteMarker(accepted+".submitted.json",acceptedState);Step(accepted,pending,true);Require(posts==6,"Eagle restart resurrected accepted but absent item");
            ImportProgress.ResetForTests();string recent=Path.Combine(pending,"Recording-2026-10-07_21-06-13-094-new.mp4"),historic=Path.Combine(pending,"Recording-2026-10-07_19-31-14-340-old.mp4");ImportProgress.Set(recent,"已提交");ImportProgress.Set(historic,"已入库");Require(ImportProgress.Summary.Contains("21:06:13") && !ImportProgress.Summary.Contains("19:31"),"Old completion replaced latest recording status");
            string blocked=Path.Combine(pending,"Screenshot-index-blocked.png");File.WriteAllText(blocked,"retain during corrupt index");items.Add(itemFirst);
            Step(blocked,pending,true);Step(blocked,pending,true);Require(posts==6 && File.Exists(blocked) && !File.Exists(blocked+".submitted.json"),"Duplicate Eagle IDs did not pause new submissions");
            string retained=Path.Combine(pending,"Screenshot-index-retained.png");File.WriteAllText(retained,"verified but index duplicated");var retainedItem=new Dictionary<string,object>{{"id","retained-id"},{"name","Retained"},{"ext","png"},{"width",3440},{"height",1440}};items.Add(retainedItem);string retainedFolder=Path.Combine(library,"images","retained-id.info");Directory.CreateDirectory(retainedFolder);string retainedStored=Path.Combine(retainedFolder,"Retained.png");File.Copy(retained,retainedStored);CaptureContext.WriteMarker(retained+".confirmed.json",new {id="retained-id",libraryPath=library,verifiedPath=retainedStored,sha256=CaptureContext.Hash(retained),cleanupAfter=DateTime.UtcNow.AddMinutes(-1).ToString("o")});Step(retained,pending,true);Require(File.Exists(retained),"Duplicate index allowed cache deletion");
            items.RemoveAt(items.Count-2);Step(blocked,pending,true);Require(posts==7,"Healthy index did not resume queued submission");Step(blocked,pending,true);Require(posts==7,"Index recovery reuploaded submitted source");Step(retained,pending,true);Require(!File.Exists(retained),"Healthy index did not resume safe cleanup");
            TestWorkers();
            using(var finished=new ManualResetEvent(false))
            {
                int tries=0,failures=0;
                using(var queue=new SerialImportQueue(delegate(string path){if(Interlocked.Increment(ref tries)==1)throw new IOException("NAS file still exclusively open",unchecked((int)0x80070020));},delegate(string path,Exception error){if(error!=null)failures++;finished.Set();},50))
                {queue.Enqueue("locked-NAS");Require(finished.WaitOne(2000) && tries==2 && failures==0 && queue.Count==0,"Sharing violation dropped the import instead of retrying");}
            }
            File.WriteAllText(Path.Combine(directory,"import-pipeline-test.txt"),"PASS: delayed index; restart deduplication; new capture progresses; library binding; partial copy; NAS grace; rename cleanup; unknown POST; recovery; legacy retention; verifier isolation; exact filename/time receipt ID; wrong ID rejected; disk/API tombstones; cancelled restart; accepted receipt retained across Eagle restart; latest recording status; sharing violation retries; image preview retention before and after confirmation; duplicate-ID pause and safe recovery",Encoding.UTF8);return 0;
        }
        catch(Exception ex){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 61;}
        finally{CaptureContext.TestRequest=null;CaptureContext.TestEagleSession=null;EagleReceipt.TestLogPath=null;}
    }
    static void TestWorkers()
    {
        using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))using(var freshDone=new ManualResetEvent(false))using(var oldDone=new ManualResetEvent(false))
        {
            using(var queue=new SerialImportQueue(delegate(string path)
            {
                if(path=="old")throw new ImportVerificationRequired(delegate{entered.Set();if(!release.WaitOne(5000))throw new Exception("Verifier release timed out");});
            },delegate(string path,Exception error){Require(error==null,"Worker failure");if(path=="old")oldDone.Set();else freshDone.Set();}))
            {
                try{queue.Enqueue("old");Require(entered.WaitOne(2000),"Verifier did not start");Require(!queue.Enqueue("old"),"Verifying source lost deduplication");queue.Enqueue("fresh");Require(freshDone.WaitOne(1500) && queue.Count==1,"Slow NAS hash blocked fresh submission");release.Set();Require(oldDone.WaitOne(2000),"Verification did not finish");}
                finally{release.Set();}
            }
        }
    }
    internal static int TestVideos(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);string pending=Path.Combine(directory,"Pending"),library=Path.Combine(directory,"Eagle");Directory.CreateDirectory(pending);Directory.CreateDirectory(library);int posts=0;
            Dictionary<string,object> eagleItem=null;int refreshes=0;
            CaptureContext.TestRequest=delegate(string endpoint,object body)
            {
                if(endpoint=="library/info")return Json.Serialize(new {status="success",data=new {library=new {path=library}}});
                if(endpoint.StartsWith("item/list?"))return Json.Serialize(new {status="success",data=eagleItem!=null && (endpoint=="item/list?limit=100" || endpoint.EndsWith("Recording-valid"))?new[]{eagleItem}:new Dictionary<string,object>[0]});
                if(endpoint.StartsWith("item/info?id="))return Json.Serialize(new {status="success",data=eagleItem});
                if(endpoint=="item/refreshThumbnail"){refreshes++;return "{\"status\":\"success\"}";}
                if(endpoint=="item/addFromPath"){posts++;return "{\"status\":\"success\"}";}throw new Exception("Unexpected endpoint");
            };
            string bad=Path.Combine(pending,"Recording-broken.mp4");File.WriteAllText(bad,"truncated mp4");bool rejected=false;try{Step(bad,pending,true);}catch(IOException){rejected=true;}Require(rejected && posts==0 && !File.Exists(bad+".submitted.json") && File.Exists(bad),"Broken video submitted or deleted");
            string good=Path.Combine(pending,"Recording-valid.mp4");MediaTools.Run(MediaTools.Locate("ffmpeg"),"-v error -y -f lavfi -i testsrc2=size=160x120:rate=30 -t 0.5 -c:v libx264 -pix_fmt yuv420p -movflags +faststart "+MediaTools.Quote(good),30000);
            Step(good,pending,true);Require(posts==1 && File.Exists(good+".validated.json"),"Valid full-decode video not submitted");Step(good,pending,true);Require(posts==1,"Valid video duplicated before indexing");
            // Structural corruption with the container header still present must fail decode/probe.
            string corrupt=Path.Combine(pending,"Recording-corrupt.mp4");File.Copy(good,corrupt);using(var file=new FileStream(corrupt,FileMode.Open,FileAccess.Write)){file.SetLength(file.Length-500);}rejected=false;try{Step(corrupt,pending,true);}catch(IOException){rejected=true;}Require(rejected && posts==1 && File.Exists(corrupt),"Truncated packets uploaded");
            eagleItem=new Dictionary<string,object>{{"id","video-id"},{"name","Recording-valid"},{"ext","mp4"},{"isDeleted",false},{"width",160},{"height",120},{"duration",0.5},{"noPreview",true}};
            string eagleFolder=Path.Combine(library,"images","video-id.info");Directory.CreateDirectory(eagleFolder);File.Copy(good,Path.Combine(eagleFolder,"Recording-valid.mp4"));
            Step(good,pending,true);Require(refreshes==1 && posts==1 && File.Exists(good) && !File.Exists(good+".confirmed.json"),"Unanalysed Eagle video was deleted or reuploaded");
            Step(good,pending,true);Require(refreshes==1 && posts==1 && File.Exists(good),"Stalled Eagle thumbnail worker flooded with refresh requests");
            eagleItem["noPreview"]=false;
            Step(good,pending,true);Require(File.Exists(good+".confirmed.json") && File.Exists(good),"Analysed playable video not confirmed");
            var confirmed=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(good+".confirmed.json"));confirmed["cleanupAfter"]=DateTime.UtcNow.AddMinutes(-1).ToString("o");CaptureContext.WriteMarker(good+".confirmed.json",confirmed);
            Step(good,pending,true);Require(!File.Exists(good) && Directory.GetFiles(pending,"Recording-valid*").Length==0 && File.Exists(Path.Combine(eagleFolder,"Recording-valid.mp4")),"Video cleanup left validation sidecars or removed Eagle original");
            File.WriteAllText(Path.Combine(directory,"video-validation-test.txt"),"PASS: invalid MP4/truncated packets blocked before POST; full H264 decode; deduplication; noPreview rejected; single refresh request; retain source until media analysis; NAS grace; all video sidecars cleaned",Encoding.UTF8);return 0;
        }
        catch(Exception ex){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 62;}
        finally{CaptureContext.TestRequest=null;}
    }
}
