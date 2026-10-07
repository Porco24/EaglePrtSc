using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly:System.Reflection.AssemblyTitle("EaglePrtSc")]
[assembly:System.Reflection.AssemblyProduct("EaglePrtSc")]
[assembly:System.Reflection.AssemblyDescription("PrtSc screenshot and screen recording settings for Eagle")]
[assembly:System.Reflection.AssemblyVersion("5.11.0.0")]
[assembly:System.Reflection.AssemblyFileVersion("5.11.0.0")]

public sealed class CaptureSettings
{
    public string ImageFormat {get;set;}
    public int ImageScale {get;set;}
    public int JpegQuality {get;set;}
    public int VideoScale {get;set;}
    public int FrameRate {get;set;}
    public decimal VideoMbps {get;set;}
    public bool AutoVideoBitrate {get;set;}
    public string VideoEncoder {get;set;}
    public string CaptureMethod {get;set;}
    public bool PreferQuality {get;set;}
    public int QualityLevel {get;set;}
    public int SharpenPercent {get;set;}
    public bool SystemAudio {get;set;}
    public int AudioVolume {get;set;}
    public int AudioKbps {get;set;}
    public bool DrawMouse {get;set;}
    public string EaglePath {get;set;}
    public string FFmpegPath {get;set;}
    public string FFprobePath {get;set;}
    public CaptureSettings()
    {
        ImageFormat="PNG";ImageScale=100;JpegQuality=90;VideoScale=100;FrameRate=60;AutoVideoBitrate=true;VideoMbps=RecommendedVideoMbps(SystemInformation.VirtualScreen.Size);
        VideoEncoder="Auto";CaptureMethod="Auto";PreferQuality=true;QualityLevel=20;SharpenPercent=0;
        SystemAudio=true;AudioVolume=100;AudioKbps=192;DrawMouse=false;
        EaglePath="";FFmpegPath="";FFprobePath="";
    }
    internal CaptureSettings Copy(){return (CaptureSettings)MemberwiseClone();}
    internal void Normalize()
    {
        ImageFormat=string.Equals(ImageFormat,"JPEG",StringComparison.OrdinalIgnoreCase)?"JPEG":"PNG";
        ImageScale=Math.Max(10,Math.Min(100,ImageScale));JpegQuality=Math.Max(1,Math.Min(100,JpegQuality));
        VideoScale=Math.Max(10,Math.Min(100,VideoScale));FrameRate=Math.Max(1,Math.Min(200,FrameRate));
        VideoMbps=Math.Max(0.5m,Math.Min(100m,VideoMbps));AudioVolume=Math.Max(0,Math.Min(200,AudioVolume));
        AudioKbps=Array.IndexOf(new[]{96,128,192,256,320},AudioKbps)>=0?AudioKbps:192;
        VideoEncoder=VideoEncoder=="NVENC" || VideoEncoder=="x264"?VideoEncoder:"Auto";
        CaptureMethod=CaptureMethod=="GDI"?"GDI":"Auto";
        QualityLevel=Math.Max(16,Math.Min(28,QualityLevel));SharpenPercent=Math.Max(0,Math.Min(100,SharpenPercent));
        EaglePath=EaglePath??"";FFmpegPath=FFmpegPath??"";FFprobePath=FFprobePath??"";
    }
    internal Size VideoSize(Size source){return new Size(Math.Max(2,source.Width*VideoScale/100/2*2),Math.Max(2,source.Height*VideoScale/100/2*2));}
    internal decimal RecommendedVideoMbps(Size source){return BitrateRecommendation.For(VideoSize(source),FrameRate);}
    internal decimal EffectiveVideoMbps(Size source){return AutoVideoBitrate?RecommendedVideoMbps(source):VideoMbps;}
    internal string AudioSummary {get{return SystemAudio?"电脑声音 "+AudioVolume+"% · 不录麦克风":"静音 · 不录麦克风";}}
}

internal static class BitrateRecommendation
{
    internal static decimal For(Size output,int fps)
    {
        // H.264 SDR 30-fps anchors from the YouTube upload guide, interpolated by pixel count.
        // Desktop recordings, ultrawide displays and rates outside 30/60 fps use an estimate.
        decimal[] pixels={640m*360,854m*480,1280m*720,1920m*1080,2560m*1440,3840m*2160};
        decimal[] mbps={1m,2.5m,5m,8m,16m,40m};
        decimal width=Math.Max(2,output.Width),height=Math.Max(2,output.Height),shortSide=Math.Min(width,height);
        decimal area=width*height,referenceArea=shortSide*shortSide*16m/9m;
        decimal baseline=mbps[mbps.Length-1]*referenceArea/pixels[pixels.Length-1];
        if(referenceArea<=pixels[0])baseline=mbps[0]*referenceArea/pixels[0];
        else for(int i=1;i<pixels.Length;i++)if(referenceArea<=pixels[i]){baseline=mbps[i-1]+(mbps[i]-mbps[i-1])*(referenceArea-pixels[i-1])/(pixels[i]-pixels[i-1]);break;}
        baseline*=area/referenceArea;
        int rate=Math.Max(1,Math.Min(200,fps));
        decimal factor=rate<=30?rate/30m:rate<=60?1m+(rate-30)/60m:rate/40m;
        return Math.Max(0.5m,Math.Min(100m,decimal.Ceiling(baseline*factor*2m)/2m));
    }
}

internal static class SettingsStore
{
    static readonly object gate=new object();
    static CaptureSettings current;
    internal static string Warning="";
    internal static string FilePath {get{return Path.Combine(Program.Root,"settings.json");}}
    internal static CaptureSettings Load(string path)
    {
        var serializer=new JavaScriptSerializer();string json=File.Exists(path)?File.ReadAllText(path,Encoding.UTF8):null;
        var value=json!=null?serializer.Deserialize<CaptureSettings>(json):new CaptureSettings();
        if(value!=null && json!=null)
        {
            var fields=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(json);
            if(!fields.ContainsKey("VideoMbps"))value.VideoMbps=value.RecommendedVideoMbps(SystemInformation.VirtualScreen.Size);
            // Preserve a legacy manually chosen rate; matching defaults can use automatic calculation.
            if(!fields.ContainsKey("AutoVideoBitrate"))value.AutoVideoBitrate=!fields.ContainsKey("VideoMbps") || value.VideoMbps==value.RecommendedVideoMbps(SystemInformation.VirtualScreen.Size);
            if(!fields.ContainsKey("PreferQuality"))value.PreferQuality=value.AutoVideoBitrate;
        }
        if(value==null)throw new IOException("设置文件内容为空。");value.Normalize();return value;
    }
    static string LegacyPath(string name)
    {string path=Path.Combine(Program.Root,name+"-path.txt");return File.Exists(path)?File.ReadAllText(path,Encoding.UTF8).Trim():"";}
    internal static CaptureSettings Snapshot()
    {
        lock(gate)
        {
            if(current==null)
            {
                try{current=Load(FilePath);}catch(Exception ex){Warning="设置文件无法读取，已使用默认值；原文件保留。";Program.Log("SETTINGS_LOAD_FAILED "+ex.Message);current=new CaptureSettings();}
                if(string.IsNullOrEmpty(current.EaglePath))current.EaglePath=LegacyPath("eagle");
                if(string.IsNullOrEmpty(current.FFmpegPath))current.FFmpegPath=LegacyPath("ffmpeg");
                if(string.IsNullOrEmpty(current.FFprobePath))current.FFprobePath=LegacyPath("ffprobe");
            }
            return current.Copy();
        }
    }
    internal static void Write(string path,CaptureSettings value)
    {
        var normalized=value.Copy();normalized.Normalize();string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(normalized),Encoding.UTF8);
            if(File.Exists(path))File.Replace(temporary,path,path+".bak",true);else File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static void Save(CaptureSettings value)
    {lock(gate){Write(FilePath,value);current=value.Copy();current.Normalize();Warning="";}Program.Log("SETTINGS_SAVED");}
    internal static void ApplyDependencyPaths(string ffmpeg,string ffprobe,CaptureSettings expected)
    {
        lock(gate)
        {
            var value=Snapshot();
            // Preserve any paths or recording settings edited while the download ran.
            if(value.FFmpegPath==expected.FFmpegPath)value.FFmpegPath=ffmpeg;
            if(value.FFprobePath==expected.FFprobePath)value.FFprobePath=ffprobe;
            Write(FilePath,value);current=value.Copy();
        }
    }
}

internal static class ScreenshotOutput
{
    internal static Size SizeFor(Image source,int percent){return new Size(Math.Max(1,source.Width*percent/100),Math.Max(1,source.Height*percent/100));}
    internal static void Save(Image source,string path,CaptureSettings settings)
    {
        Size size=SizeFor(source,settings.ImageScale);
        if(settings.ImageFormat=="PNG" && settings.ImageScale==100){source.Save(path,ImageFormat.Png);return;}
        using(var target=new Bitmap(size.Width,size.Height,PixelFormat.Format24bppRgb))
        {
            using(var g=Graphics.FromImage(target)){g.Clear(Color.White);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(source,new Rectangle(Point.Empty,size),0,0,source.Width,source.Height,GraphicsUnit.Pixel);}
            if(settings.ImageFormat=="JPEG")
            {
                ImageCodecInfo encoder=null;foreach(var codec in ImageCodecInfo.GetImageEncoders())if(codec.MimeType=="image/jpeg")encoder=codec;
                using(var parameters=new EncoderParameters(1)){parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,(long)settings.JpegQuality);target.Save(path,encoder,parameters);}
            }
            else target.Save(path,ImageFormat.Png);
        }
    }
}

internal sealed class StartupRegistration
{
    readonly string executable,shortcutPath;
    internal StartupRegistration(string executable,string directory=null)
    {
        this.executable=Path.GetFullPath(executable);
        shortcutPath=Path.Combine(directory??Environment.GetFolderPath(Environment.SpecialFolder.Startup),"EaglePrtSc.lnk");
    }
    static object Shell(){Type type=Type.GetTypeFromProgID("WScript.Shell");if(type==null)throw new IOException("Windows 快捷方式服务不可用。");return Activator.CreateInstance(type);}
    static object Invoke(object target,string member,BindingFlags flags,params object[] args)
    {return target.GetType().InvokeMember(member,flags,null,target,args);}
    static void Release(object value){if(value!=null && Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
    string ReadShortcut(string property)
    {
        if(!File.Exists(shortcutPath))return "";
        object shell=null,link=null;
        try{shell=Shell();link=Invoke(shell,"CreateShortcut",BindingFlags.InvokeMethod,shortcutPath);return (string)Invoke(link,property,BindingFlags.GetProperty);}
        finally{Release(link);Release(shell);}
    }
    internal string Target(){return ReadShortcut("TargetPath");}
    internal bool IsRegistered()
    {string target=Target();return target.Length>0 && string.Equals(Path.GetFullPath(target),executable,StringComparison.OrdinalIgnoreCase);}
    internal bool IsBackground(){return IsRegistered() && ReadShortcut("Arguments")=="--background";}
    internal void Set(bool enabled)
    {
        string target=Target();
        if(target.Length>0 && !string.Equals(Path.GetFullPath(target),executable,StringComparison.OrdinalIgnoreCase))throw new IOException("开机启动快捷方式指向另一个 EaglePrtSc 目录，已保留。请先在那个目录取消开机启动。");
        if(!enabled){if(File.Exists(shortcutPath))File.Delete(shortcutPath);return;}
        if(!File.Exists(executable))throw new FileNotFoundException("找不到本程序 EXE，无法注册开机启动。",executable);
        string temporary=Path.Combine(Path.GetTempPath(),"EaglePrtSc-"+Guid.NewGuid().ToString("N")+".lnk");
        object shell=null,link=null;
        try
        {
            shell=Shell();link=Invoke(shell,"CreateShortcut",BindingFlags.InvokeMethod,temporary);
            Invoke(link,"TargetPath",BindingFlags.SetProperty,executable);
            Invoke(link,"Arguments",BindingFlags.SetProperty,"--background");
            Invoke(link,"WorkingDirectory",BindingFlags.SetProperty,Path.GetDirectoryName(executable));
            Invoke(link,"Description",BindingFlags.SetProperty,"EaglePrtSc · 双击截图 / 长按录制");
            Invoke(link,"Save",BindingFlags.InvokeMethod);
            Release(link);link=null;
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));File.Copy(temporary,shortcutPath,true);
            if(!IsRegistered())throw new IOException("未能确认开机启动注册成功。");
        }
        finally{Release(link);Release(shell);if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static int Test(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            string executable=System.Windows.Forms.Application.ExecutablePath;
            var startup=new StartupRegistration(executable,directory);
            if(startup.IsRegistered())throw new Exception("Isolated directory unexpectedly registered");
            startup.Set(true);if(!startup.IsRegistered())throw new Exception("Startup registration did not persist");
            startup.Set(true);
            object shell=null,link=null;
            try
            {
                shell=Shell();link=Invoke(shell,"CreateShortcut",BindingFlags.InvokeMethod,Path.Combine(directory,"EaglePrtSc.lnk"));
                if((string)Invoke(link,"Arguments",BindingFlags.GetProperty)!="--background" || !string.Equals((string)Invoke(link,"WorkingDirectory",BindingFlags.GetProperty),Path.GetDirectoryName(executable),StringComparison.OrdinalIgnoreCase))throw new Exception("Shortcut is not configured for silent background startup");
                Invoke(link,"Arguments",BindingFlags.SetProperty,"");Invoke(link,"Save",BindingFlags.InvokeMethod);
            }
            finally{Release(link);Release(shell);}
            if(!startup.IsRegistered() || startup.IsBackground())throw new Exception("Legacy shortcut detection failed");
            startup.Set(true);if(!startup.IsBackground())throw new Exception("Legacy shortcut migration failed");
            var foreign=new StartupRegistration(Path.Combine(directory,"AnotherInstallation.exe"),directory);bool blocked=false;
            try{foreign.Set(false);}catch(IOException){blocked=true;}
            if(!blocked || !startup.IsRegistered())throw new Exception("Another installation's registration was deleted");
            blocked=false;try{foreign.Set(true);}catch(IOException){blocked=true;}
            if(!blocked || !startup.IsRegistered())throw new Exception("Another installation's registration was overwritten");
            startup.Set(false);startup.Set(false);if(startup.IsRegistered())throw new Exception("Registration removal failed");
            File.WriteAllText(Path.Combine(directory,"startup-test.txt"),"PASS: actual Windows .lnk creation and reload; EXE target; --background; working directory; enable/disable idempotence; legacy shortcut migration; other installation preserved; tests did not touch the real Startup folder",Encoding.UTF8);return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 70;}
    }
}

internal sealed class SettingsComboBox : ComboBox
{
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Native drop-down lists omit their selection in some WM_PRINT renderers.
        if((m.Msg==0x317 || m.Msg==0x318) && m.WParam!=IntPtr.Zero && SelectedIndex>=0)
        {
            using(var g=Graphics.FromHdc(m.WParam))
            {
                var area=new Rectangle(3,2,Math.Max(1,Width-24),Height-4);
                using(var brush=new SolidBrush(Enabled?SystemColors.Window:SystemColors.Control))g.FillRectangle(brush,area);
                TextRenderer.DrawText(g,Text,Font,area,Enabled?ForeColor:SystemColors.GrayText,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
            }
        }
    }
}

internal sealed class SettingsForm : Form
{
    readonly Color ink=Color.FromArgb(27,40,57),muted=Color.FromArgb(103,117,136),accent=Color.FromArgb(0,128,117);
    readonly ComboBox imageFormat=new SettingsComboBox(),audioRate=new SettingsComboBox(),audioMode=new SettingsComboBox(),encoder=new SettingsComboBox(),captureMethod=new SettingsComboBox(),qualityMode=new SettingsComboBox();
    readonly NumericUpDown imageScale=new NumericUpDown(),jpeg=new NumericUpDown(),videoScale=new NumericUpDown(),bitrate=new NumericUpDown(),volume=new NumericUpDown(),fps=new NumericUpDown(),quality=new NumericUpDown(),sharpen=new NumericUpDown();
    readonly CheckBox cursor=new CheckBox(),autoStart=new CheckBox(),autoBitrate=new CheckBox();
    readonly StartupRegistration startup=new StartupRegistration(Application.ExecutablePath);
    bool startupRegistered;
    readonly Label startupStatus=new Label();
    readonly Label dependencyStatus=new Label();
    Button dependencyButton;
    string observedFFmpeg,observedFFprobe;
    readonly TextBox eagle=new TextBox(),ffmpeg=new TextBox(),ffprobe=new TextBox();
    readonly Label imagePreview=new Label(),videoPreview=new Label(),estimate=new Label(),notice=new Label(),status=new Label();
    readonly LinkLabel recommendation=new LinkLabel();
    readonly Panel qualityOptions=new Panel(),bitrateOptions=new Panel();
    readonly ToolTip recommendationTip=new ToolTip();
    readonly TabControl tabs=new TabControl();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly Func<string> getStatus;bool populating,dirty;
    Size lastScreenSize;
    internal SettingsForm(Func<string> getStatus)
    {
        this.getStatus=getStatus;
        Text="EaglePrtSc · 设置";Font=new Font("Microsoft YaHei UI",10);ForeColor=ink;BackColor=Color.FromArgb(244,247,250);
        AutoScaleMode=AutoScaleMode.Font;ClientSize=new Size(820,680);MinimumSize=Size;MaximumSize=Size;MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;Icon=SystemIcons.Application;
        var header=new Panel{Location=Point.Empty,Size=new Size(820,108),BackColor=ink};Controls.Add(header);
        LabelAt(header,"EaglePrtSc",28,20,760,36,21,Color.White);
        LabelAt(header,"双击 PrtSc 截图   /   长按 0.8 秒开始或停止录制",30,64,750,25,10,Color.FromArgb(190,211,221));
        status.SetBounds(30,122,760,25);status.ForeColor=accent;Controls.Add(status);
        tabs.SetBounds(26,162,768,422);tabs.Font=new Font(Font.FontFamily,10);tabs.ItemSize=new Size(160,36);tabs.SizeMode=TabSizeMode.Fixed;Controls.Add(tabs);
        var picture=Page("  图片  ");var movie=Page("  视频  ");var integration=Page("  连接与文件  ");
        LabelAt(picture,"图片默认设置",24,18,680,28,14,ink);
        LabelAt(picture,"使用 Windows PrtSc 获取完整桌面，再按以下设置保存。",24,51,700,26,10,muted);
        Combo(picture,"保存格式",imageFormat,new[]{"PNG","JPEG"},95);Hint(picture,"PNG 无损；JPEG 文件更小，适合照片。",126);
        Number(picture,"分辨率比例",imageScale,10,100,0,161,"%",5);
        imagePreview.SetBounds(270,194,438,24);imagePreview.ForeColor=accent;picture.Controls.Add(imagePreview);
        Number(picture,"JPEG 质量",jpeg,1,100,0,229,"/ 100",5);Hint(picture,"数值越高越清晰；仅在 JPEG 格式下生效。",263);
        LabelAt(picture,"双击后显示反馈，保存完成后自动排队导入 Eagle。",24,316,700,28,10,muted);
        LabelAt(movie,"视频默认设置",24,12,700,28,14,ink);
        Number(movie,"分辨率比例",videoScale,10,100,0,43,"%",5);
        videoPreview.SetBounds(422,48,300,24);videoPreview.ForeColor=accent;movie.Controls.Add(videoPreview);
        Number(movie,"帧率",fps,1,200,0,84,"fps",1);
        Combo(movie,"录制方式",qualityMode,new[]{"画质优先（推荐）","目标码率"},125,430);
        qualityOptions.SetBounds(0,166,738,49);movie.Controls.Add(qualityOptions);
        bitrateOptions.SetBounds(0,166,738,49);movie.Controls.Add(bitrateOptions);
        Number(qualityOptions,"画质等级",quality,16,28,0,0,"",1);
        LabelAt(qualityOptions,"数值越低越清晰，文件也越大。",442,0,278,21,8.5f,muted);
        LabelAt(qualityOptions,"码率随画面内容变化，无需单独设置。",442,21,278,21,8.5f,muted);
        Number(bitrateOptions,"视频码率",bitrate,0.5m,100,1,0,"Mbps",0.5m);
        autoBitrate.SetBounds(145,0,115,32);autoBitrate.Text="自动计算";autoBitrate.Font=new Font(Font.FontFamily,9);bitrateOptions.Controls.Add(autoBitrate);
        recommendation.SetBounds(442,0,278,21);recommendation.Font=new Font(Font.FontFamily,8.5f);recommendation.LinkColor=muted;recommendation.ActiveLinkColor=accent;recommendation.VisitedLinkColor=muted;recommendation.LinkBehavior=LinkBehavior.HoverUnderline;
        recommendation.LinkClicked+=delegate{ApplyRecommendedBitrate();};bitrateOptions.Controls.Add(recommendation);
        recommendationTip.SetToolTip(recommendation,"根据当前输出分辨率和 FPS 动态估算；点击启用自动计算。关闭自动计算后可手动设置码率。");
        estimate.SetBounds(442,21,278,21);estimate.Font=new Font(Font.FontFamily,8.5f);estimate.ForeColor=muted;bitrateOptions.Controls.Add(estimate);
        CompactCombo(movie,"编码器",encoder,new[]{"自动（优先 NVENC）","NVIDIA NVENC","CPU x264"},24,224,220);
        CompactCombo(movie,"桌面采集",captureMethod,new[]{"自动（优先 DXGI）","兼容模式（GDI）"},24,268,220);
        CompactNumber(movie,"锐化强度",sharpen,0,100,24,312,"%",5);
        LabelAt(movie,"默认关闭；可尝试 10–20%。缩放使用 Lanczos。",24,347,354,34,8.5f,muted);
        CompactCombo(movie,"声音来源",audioMode,new[]{"电脑声音（不录麦克风）","静音（不录声音）"},398,224,212);
        CompactNumber(movie,"电脑声音音量",volume,0,200,398,268,"%",10);
        CompactCombo(movie,"音频码率",audioRate,new[]{"96","128","192","256","320"},398,312,100);LabelAt(movie,"kbps · AAC",638,315,100,26,9,muted);
        cursor.SetBounds(398,354,340,28);cursor.Text="在视频中显示鼠标指针";cursor.AutoSize=true;movie.Controls.Add(cursor);
        LabelAt(integration,"连接与本地文件",24,18,700,28,14,ink);
        LabelAt(integration,"Eagle 使用当前打开的素材库。可选择程序的安装位置。",24,51,700,26,10,muted);
        PathField(integration,"Eagle",eagle,96);PathField(integration,"FFmpeg",ffmpeg,151);PathField(integration,"FFprobe",ffprobe,206);
        autoStart.SetBounds(24,257,650,28);autoStart.Text="登录 Windows 时自动启动（后台运行）";integration.Controls.Add(autoStart);
        startupStatus.SetBounds(24,286,700,24);startupStatus.ForeColor=muted;integration.Controls.Add(startupStatus);
        var folder=ButtonAt(integration,"打开本地图片与视频",24,316,234,34,delegate{System.Diagnostics.Process.Start("explorer.exe",Program.Pending);});folder.BackColor=Color.White;
        dependencyButton=ButtonAt(integration,"检查／安装录制依赖",278,316,234,34,delegate{DependencyInstaller.Start();RefreshDependencies();});
        dependencyStatus.SetBounds(24,353,704,25);dependencyStatus.Font=new Font(Font.FontFamily,9);dependencyStatus.ForeColor=muted;integration.Controls.Add(dependencyStatus);
        notice.SetBounds(30,595,760,27);notice.ForeColor=muted;Controls.Add(notice);
        ButtonAt(this,"恢复默认参数",28,632,146,34,delegate{var value=new CaptureSettings();value.EaglePath=eagle.Text;value.FFmpegPath=ffmpeg.Text;value.FFprobePath=ffprobe.Text;Populate(value);dirty=true;notice.Text="已恢复默认参数，点击保存后生效。";});
        ButtonAt(this,"关闭",544,628,106,40,delegate{Close();});
        var save=ButtonAt(this,"保存设置",666,628,126,40,delegate{SaveSettings();});save.BackColor=accent;save.ForeColor=Color.White;
        foreach(Control c in new Control[]{imageScale,jpeg,videoScale,bitrate,volume,fps,quality,sharpen})((NumericUpDown)c).ValueChanged+=Changed;
        foreach(var c in new[]{imageFormat,audioRate,audioMode,encoder,captureMethod,qualityMode})c.SelectedIndexChanged+=Changed;
        cursor.CheckedChanged+=Changed;foreach(var c in new[]{eagle,ffmpeg,ffprobe})c.TextChanged+=Changed;
        autoStart.CheckedChanged+=Changed;
        autoBitrate.CheckedChanged+=Changed;
        Populate(SettingsStore.Snapshot());notice.Text=string.IsNullOrEmpty(SettingsStore.Warning)?"保存后从下一次截图 / 录制生效；关闭窗口后仍在托盘运行。":SettingsStore.Warning;
        populating=true;
        try{startupRegistered=startup.IsRegistered();autoStart.Checked=startupRegistered;StartupStatus();}
        catch(Exception ex){startupStatus.Text="无法读取开机启动状态："+ex.Message;}
        populating=false;
        var initial=SettingsStore.Snapshot();observedFFmpeg=initial.FFmpegPath;observedFFprobe=initial.FFprobePath;
        timer.Interval=1000;timer.Tick+=delegate{RefreshStatus();RefreshDependencies();if(SystemInformation.VirtualScreen.Size!=lastScreenSize)UpdatePreview();};timer.Start();RefreshStatus();RefreshDependencies();
        FormClosing+=delegate(object sender,FormClosingEventArgs e){if(dirty && e.CloseReason==CloseReason.UserClosing){var result=MessageBox.Show(this,"保存本次修改？","EaglePrtSc",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);if(result==DialogResult.Cancel)e.Cancel=true;else if(result==DialogResult.Yes && !SaveSettings())e.Cancel=true;}};
    }
    TabPage Page(string name){var p=new TabPage(name){BackColor=Color.White,Padding=new Padding(0)};tabs.TabPages.Add(p);return p;}
    void RefreshDependencies()
    {
        dependencyStatus.Text=DependencyInstaller.Status;dependencyButton.Enabled=!DependencyInstaller.Busy;
        var value=SettingsStore.Snapshot();bool wasPopulating=populating;populating=true;
        if(ffmpeg.Text==observedFFmpeg)ffmpeg.Text=value.FFmpegPath;
        if(ffprobe.Text==observedFFprobe)ffprobe.Text=value.FFprobePath;
        observedFFmpeg=value.FFmpegPath;observedFFprobe=value.FFprobePath;populating=wasPopulating;
    }
    void LabelAt(Control parent,string text,int x,int y,int width,int height,float size,Color color){parent.Controls.Add(new Label{Text=text,Location=new Point(x,y),Size=new Size(width,height),Font=new Font(Font.FontFamily,size),ForeColor=color,TextAlign=ContentAlignment.MiddleLeft});}
    void Hint(Control p,string text,int y){LabelAt(p,text,270,y,460,27,9,muted);}
    void Combo(Control p,string caption,ComboBox control,string[] options,int y,int width=100)
    {
        LabelAt(p,caption,24,y,235,32,10,ink);control.SetBounds(270,y,width,32);control.DropDownStyle=ComboBoxStyle.DropDownList;control.Items.AddRange(options);
        control.DrawMode=DrawMode.OwnerDrawFixed;control.ItemHeight=24;
        control.DrawItem+=delegate(object sender,DrawItemEventArgs e){e.DrawBackground();if(e.Index>=0)TextRenderer.DrawText(e.Graphics,(string)control.Items[e.Index],control.Font,new Rectangle(e.Bounds.X+5,e.Bounds.Y,e.Bounds.Width-5,e.Bounds.Height),control.Enabled?ink:muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);e.DrawFocusRectangle();};
        p.Controls.Add(control);
    }
    void Number(Control p,string caption,NumericUpDown control,decimal min,decimal max,int decimals,int y,string suffix,decimal increment)
    {LabelAt(p,caption,24,y,235,32,10,ink);control.SetBounds(270,y,100,32);control.Minimum=min;control.Maximum=max;control.DecimalPlaces=decimals;control.Increment=increment;p.Controls.Add(control);LabelAt(p,suffix,382,y+3,100,26,10,muted);}
    void CompactCombo(Control p,string caption,ComboBox control,string[] options,int x,int y,int width)
    {
        Combo(p,caption,control,options,y,width);control.Left=x+128;
        foreach(Control label in p.Controls)if(label is Label && label.Text==caption){label.Left=x;label.Width=125;}
    }
    void CompactNumber(Control p,string caption,NumericUpDown control,int min,int max,int x,int y,string suffix,int increment)
    {
        Number(p,caption,control,min,max,0,y,suffix,increment);control.Left=x+128;
        foreach(Control label in p.Controls)if(label is Label){if(label.Text==caption){label.Left=x;label.Width=125;}else if(label.Text==suffix && label.Top==y+3){label.Left=x+240;label.Width=70;}}
    }
    Button ButtonAt(Control p,string caption,int x,int y,int width,int height,EventHandler action)
    {var b=new Button{Text=caption,Location=new Point(x,y),Size=new Size(width,height),FlatStyle=FlatStyle.Flat,BackColor=Color.White,Cursor=Cursors.Hand};b.FlatAppearance.BorderColor=Color.FromArgb(221,228,235);b.Click+=action;p.Controls.Add(b);return b;}
    void PathField(Control p,string caption,TextBox control,int y)
    {
        LabelAt(p,caption,24,y,115,32,10,ink);control.SetBounds(142,y,492,32);p.Controls.Add(control);
        ButtonAt(p,"浏览…",644,y-1,80,33,delegate{using(var dialog=new OpenFileDialog{Filter="应用程序 (*.exe)|*.exe",CheckFileExists=true,Title="选择 "+caption}){if(dialog.ShowDialog(this)==DialogResult.OK){control.Text=dialog.FileName;if(control==ffmpeg && string.IsNullOrWhiteSpace(ffprobe.Text)){string probe=Path.Combine(Path.GetDirectoryName(dialog.FileName),"ffprobe.exe");if(File.Exists(probe))ffprobe.Text=probe;}}}});
    }
    void Populate(CaptureSettings value)
    {
        populating=true;imageFormat.SelectedItem=value.ImageFormat;imageScale.Value=value.ImageScale;jpeg.Value=value.JpegQuality;videoScale.Value=value.VideoScale;
        fps.Value=value.FrameRate;bitrate.Value=value.VideoMbps;audioMode.SelectedIndex=value.SystemAudio?0:1;volume.Value=value.AudioVolume;
        autoBitrate.Checked=value.AutoVideoBitrate;
        encoder.SelectedIndex=value.VideoEncoder=="NVENC"?1:value.VideoEncoder=="x264"?2:0;captureMethod.SelectedIndex=value.CaptureMethod=="GDI"?1:0;
        qualityMode.SelectedIndex=value.PreferQuality?0:1;quality.Value=value.QualityLevel;sharpen.Value=value.SharpenPercent;
        audioRate.SelectedItem=value.AudioKbps.ToString();cursor.Checked=value.DrawMouse;eagle.Text=value.EaglePath;ffmpeg.Text=value.FFmpegPath;ffprobe.Text=value.FFprobePath;
        populating=false;dirty=false;UpdatePreview();
    }
    CaptureSettings Read(){return new CaptureSettings{ImageFormat=(string)imageFormat.SelectedItem,ImageScale=(int)imageScale.Value,JpegQuality=(int)jpeg.Value,VideoScale=(int)videoScale.Value,FrameRate=(int)fps.Value,VideoMbps=bitrate.Value,AutoVideoBitrate=autoBitrate.Checked,VideoEncoder=encoder.SelectedIndex==1?"NVENC":encoder.SelectedIndex==2?"x264":"Auto",CaptureMethod=captureMethod.SelectedIndex==1?"GDI":"Auto",PreferQuality=qualityMode.SelectedIndex==0,QualityLevel=(int)quality.Value,SharpenPercent=(int)sharpen.Value,SystemAudio=audioMode.SelectedIndex==0,AudioVolume=(int)volume.Value,AudioKbps=int.Parse((string)audioRate.SelectedItem),DrawMouse=cursor.Checked,EaglePath=eagle.Text.Trim(),FFmpegPath=ffmpeg.Text.Trim(),FFprobePath=ffprobe.Text.Trim()};}
    void Changed(object sender,EventArgs e){if(populating)return;dirty=true;UpdatePreview();notice.Text="有未保存的修改 · 点击保存设置。";notice.ForeColor=muted;}
    void StartupStatus(){startupStatus.Text=startupRegistered?"已注册开机启动 · 登录后自动运行，双击托盘图标打开设置。":"未注册开机启动 · 勾选后点击保存即可注册。";}
    void UpdatePreview()
    {
        var value=Read();var screen=SystemInformation.VirtualScreen.Size;
        lastScreenSize=screen;
        if(value.AutoVideoBitrate)
        {
            decimal computed=value.EffectiveVideoMbps(screen);bool wasPopulating=populating;populating=true;
            try{bitrate.Value=computed;}finally{populating=wasPopulating;}value.VideoMbps=computed;
        }
        bitrate.Enabled=!value.AutoVideoBitrate && !value.PreferQuality;quality.Enabled=value.PreferQuality;
        qualityOptions.Visible=value.PreferQuality;bitrateOptions.Visible=!value.PreferQuality;
        imagePreview.Text="输出 "+Math.Max(1,screen.Width*value.ImageScale/100)+" × "+Math.Max(1,screen.Height*value.ImageScale/100)+" 像素";
        var size=value.VideoSize(screen);videoPreview.Text=size.Width+" × "+size.Height+" 像素";
        recommendation.Text="推荐 "+value.RecommendedVideoMbps(screen).ToString("0.#")+" Mbps（"+size.Width+"×"+size.Height+" / "+value.FrameRate+" FPS）";
        decimal mb=(value.VideoMbps+(value.SystemAudio?value.AudioKbps/1000m:0))*60/8;
        estimate.Text=value.PreferQuality?"实际体积随画面复杂度变化":"估算约 "+mb.ToString("0.0")+" MB / 分钟";jpeg.Enabled=value.ImageFormat=="JPEG";volume.Enabled=audioRate.Enabled=value.SystemAudio;
    }
    void ApplyRecommendedBitrate(){autoBitrate.Checked=true;UpdatePreview();}
    internal void VerifyBitrateInteraction()
    {
        tabs.SelectedIndex=1;Show();Application.DoEvents();
        qualityMode.SelectedIndex=1;
        autoBitrate.Checked=false;bitrate.Value=7;fps.Value=45;videoScale.Value=50;
        var settings=Read();Size screen=SystemInformation.VirtualScreen.Size;
        if(bitrate.Value!=7 || !recommendation.Text.Contains(settings.RecommendedVideoMbps(screen).ToString("0.#")) || !recommendation.Text.Contains("45 FPS"))throw new Exception("Recommendation did not update independently of a manual bitrate");
        ApplyRecommendedBitrate();if(bitrate.Value!=settings.RecommendedVideoMbps(screen))throw new Exception("Recommendation action did not update bitrate");
        fps.Value=60;videoScale.Value=100;
        if(!autoBitrate.Checked || bitrate.Enabled || bitrate.Value!=Read().RecommendedVideoMbps(screen))throw new Exception("Automatic bitrate did not follow changed scale/FPS");
        autoBitrate.Checked=false;
        bitrate.Value=11;fps.Value=60;videoScale.Value=100;
        if(bitrate.Value!=11 || !recommendation.Text.Contains("60 FPS"))throw new Exception("Resolution/frame rate change overwrote a manual bitrate");
        qualityMode.SelectedIndex=0;
        if(!qualityOptions.Visible || bitrateOptions.Visible || Read().FrameRate!=60 || Read().VideoMbps!=11 || Read().AutoVideoBitrate)throw new Exception("Merged quality mode exposed inactive bitrate controls or changed existing settings");
        quality.Value=18;qualityMode.SelectedIndex=1;
        if(qualityOptions.Visible || !bitrateOptions.Visible || !bitrate.Enabled || Read().VideoMbps!=11 || Read().QualityLevel!=18 || Read().FrameRate!=60)throw new Exception("Mode switch lost manual bitrate, quality level or FPS");
    }
    bool SaveSettings()
    {
        try
        {
            var value=Read();foreach(string path in new[]{value.EaglePath,value.FFmpegPath,value.FFprobePath})if(path.Length>0 && (!File.Exists(path) || !string.Equals(Path.GetExtension(path),".exe",StringComparison.OrdinalIgnoreCase)))throw new IOException("请选择有效的 EXE 安装路径，或留空以自动查找。");
            // Also migrate an owned legacy shortcut so startup does not open the settings window.
            if(autoStart.Checked!=startupRegistered || (autoStart.Checked && !startup.IsBackground())){startup.Set(autoStart.Checked);startupRegistered=startup.IsRegistered();StartupStatus();}
            SettingsStore.Save(value);dirty=false;notice.Text="设置已保存 ✓  参数从下一次截图 / 录制生效。";notice.ForeColor=accent;return true;
        }
        catch(Exception ex){MessageBox.Show(this,"保存未完成："+ex.Message,"EaglePrtSc",MessageBoxButtons.OK,MessageBoxIcon.Error);return false;}
    }
    void RefreshStatus(){status.Text=getStatus==null?"● 就绪   ·   热键监听中":getStatus();}
    internal void PreviewTab(int index,bool targetBitrate=false){tabs.SelectedIndex=index;if(targetBitrate)qualityMode.SelectedIndex=1;dirty=false;}
    protected override void Dispose(bool disposing){if(disposing){timer.Dispose();recommendationTip.Dispose();}base.Dispose(disposing);}
}

internal static class BitrateTests
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            var defaults=new CaptureSettings();if(defaults.FrameRate!=60 || !defaults.AutoVideoBitrate || defaults.VideoMbps!=defaults.RecommendedVideoMbps(SystemInformation.VirtualScreen.Size))throw new Exception("Initial defaults do not use 60 fps and automatic bitrate");
            if(BitrateRecommendation.For(new Size(1920,1080),30)!=8 || BitrateRecommendation.For(new Size(1920,1080),60)!=12 || BitrateRecommendation.For(new Size(1920,1080),45)!=10 || BitrateRecommendation.For(new Size(2560,1440),30)!=16 || BitrateRecommendation.For(new Size(3840,2160),60)!=60)throw new Exception("Bitrate reference anchors/interpolation changed");
            if(BitrateRecommendation.For(new Size(3440,1440),45)!=27 || BitrateRecommendation.For(new Size(1720,720),45)!=8.5m || BitrateRecommendation.For(new Size(3440,1440),60)!=32.5m || BitrateRecommendation.For(new Size(1720,720),60)!=10.5m)throw new Exception("Ultrawide/scaled recommendations incorrect");
            if(BitrateRecommendation.For(new Size(2,2),1)!=0.5m || BitrateRecommendation.For(new Size(7680,4320),200)!=100)throw new Exception("Recommendation bounds incorrect");
            defaults.FrameRate=45;defaults.VideoMbps=17.5m;defaults.AutoVideoBitrate=false;string path=Path.Combine(directory,"manual-settings.json");SettingsStore.Write(path,defaults);var loaded=SettingsStore.Load(path);
            if(loaded.FrameRate!=45 || loaded.VideoMbps!=17.5m || loaded.AutoVideoBitrate || loaded.EffectiveVideoMbps(new Size(3840,2160))!=17.5m)throw new Exception("A saved manual bitrate was overwritten");
            defaults.FrameRate=60;defaults.AutoVideoBitrate=true;defaults.VideoMbps=99;
            SettingsStore.Write(Path.Combine(directory,"automatic-settings.json"),defaults);var automatic=SettingsStore.Load(Path.Combine(directory,"automatic-settings.json"));
            if(!automatic.AutoVideoBitrate || automatic.EffectiveVideoMbps(new Size(3440,1440))!=32.5m || automatic.EffectiveVideoMbps(new Size(1920,1080))!=12)throw new Exception("Automatic bitrate used a cached number instead of current dimensions");
            File.WriteAllText(Path.Combine(directory,"partial-settings.json"),"{\"VideoScale\":50,\"FrameRate\":45}");var partial=SettingsStore.Load(Path.Combine(directory,"partial-settings.json"));
            if(partial.VideoMbps!=partial.RecommendedVideoMbps(SystemInformation.VirtualScreen.Size))throw new Exception("Missing bitrate did not use the saved scale/frame rate");
            using(var form=new SettingsForm(null))form.VerifyBitrateInteraction();
            File.WriteAllText(Path.Combine(directory,"bitrate-test.txt"),"PASS: initial 60fps with automatic rate; reference anchors; ultrawide/scaled estimates; limits; persisted auto/manual modes; calculation uses current dimensions, not cached rate; UI auto rate follows scale/FPS; manual rate preserved; click enables automatic calculation",Encoding.UTF8);return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 80;}
    }
}

internal static class SettingsTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            string config=Path.Combine(directory,"config.json");
            var value=new CaptureSettings{ImageFormat="JPEG",ImageScale=50,JpegQuality=78,VideoScale=75,FrameRate=60,VideoMbps=3.5m,SystemAudio=false,DrawMouse=true,AudioVolume=45,AudioKbps=128};
            SettingsStore.Write(config,value);var loaded=SettingsStore.Load(config);
            Require(loaded.ImageScale==50 && loaded.FrameRate==60 && loaded.VideoMbps==3.5m && !loaded.SystemAudio && loaded.DrawMouse && loaded.AudioVolume==45,"Settings round trip failed");
            var performance=new CaptureSettings{VideoEncoder="x264",CaptureMethod="GDI",PreferQuality=false,QualityLevel=18,SharpenPercent=15};
            string performancePath=Path.Combine(directory,"performance.json");SettingsStore.Write(performancePath,performance);var restored=SettingsStore.Load(performancePath);
            Require(restored.VideoEncoder=="x264" && restored.CaptureMethod=="GDI" && !restored.PreferQuality && restored.QualityLevel==18 && restored.SharpenPercent==15,"Quality/performance settings were lost");
            Require(new CaptureSettings().PreferQuality && new CaptureSettings().QualityLevel==20 && new CaptureSettings().SharpenPercent==0,"Quality defaults incorrect");
            var invalidPerformance=new CaptureSettings{VideoEncoder="invalid",CaptureMethod="invalid",QualityLevel=999,SharpenPercent=-2};invalidPerformance.Normalize();
            Require(invalidPerformance.VideoEncoder=="Auto" && invalidPerformance.CaptureMethod=="Auto" && invalidPerformance.QualityLevel==28 && invalidPerformance.SharpenPercent==0,"Invalid performance settings accepted");
            string qualityArgs=VideoEncoding.Arguments(new CaptureSettings(),"h264_nvenc");
            Require(qualityArgs.Contains("-rc constqp -qp 20") && !qualityArgs.Contains("-maxrate") && !qualityArgs.Contains("-b:v"),"Quality mode is constrained by a bitrate cap");
            string manualArgs=VideoEncoding.Arguments(new CaptureSettings{PreferQuality=false,VideoMbps=12},"h264_nvenc");
            Require(manualArgs.Contains("-b:v 12.0M") && manualArgs.Contains("-maxrate 24.0M"),"Bitrate mode lost target/burst allowance");
            Require(VideoEncoding.Arguments(new CaptureSettings(),"libx264").Contains("-preset veryfast") && VideoEncoding.Arguments(new CaptureSettings(),"libx264").Contains("-crf 20"),"Software encoder quality fallback incorrect");
            Rectangle screen=new Rectangle(0,0,3440,1440);
            Require(VideoEncoding.CanDuplicate(screen,screen,1) && !VideoEncoding.CanDuplicate(screen,screen,2) && !VideoEncoding.CanDuplicate(new Rectangle(0,0,6880,1440),screen,1),"DXGI must not crop a multi-monitor desktop silently");
            Require(MediaTools.DesktopVideoFilter(new CaptureSettings()).Contains("flags=lanczos") && !MediaTools.DesktopVideoFilter(new CaptureSettings()).Contains("unsharp") && MediaTools.DesktopVideoFilter(performance).Contains("unsharp=5:5:0.15"),"Scaling/sharpening selection ignored");
            loaded.VideoScale=10;SettingsStore.Write(config,loaded);Require(SettingsStore.Load(config).VideoScale==10 && SettingsStore.Load(config+".bak").VideoScale==75,"Atomic replacement/backup failed");
            var invalid=new CaptureSettings{ImageScale=999,VideoScale=0,FrameRate=999,VideoMbps=-2,AudioVolume=555};invalid.Normalize();Require(invalid.ImageScale==100 && invalid.VideoScale==10 && invalid.FrameRate==200 && invalid.VideoMbps==0.5m && invalid.AudioVolume==200,"Invalid setting bounds failed");
            Require(new CaptureSettings{VideoScale=75}.VideoSize(new Size(3441,1441))==new Size(2580,1080),"Even video dimensions failed");
            using(var bitmap=new Bitmap(321,241))
            {
                using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.CornflowerBlue);g.FillRectangle(Brushes.Coral,20,20,70,70);}
                string png=Path.Combine(directory,"image.png"),jpg=Path.Combine(directory,"image.jpg");
                ScreenshotOutput.Save(bitmap,png,new CaptureSettings());ScreenshotOutput.Save(bitmap,jpg,value);
                using(var result=new Bitmap(png))Require(result.Size==bitmap.Size && result.GetPixel(40,40)==bitmap.GetPixel(40,40),"PNG original pixels changed");
                using(var result=new Bitmap(jpg))Require(result.Size==new Size(160,120) && result.RawFormat.Guid==ImageFormat.Jpeg.Guid,"JPEG resize/encoding failed");
            }
            for(int i=0;i<3;i++)
            {
                int expectedFps=i==0?15:i==1?60:200;
                var options=new CaptureSettings{VideoScale=i==1?75:50,FrameRate=expectedFps,VideoMbps=1.5m,AutoVideoBitrate=i==2,SystemAudio=i==1,AudioVolume=45,AudioKbps=128,DrawMouse=i==1,VideoEncoder=i==1?"x264":"Auto",PreferQuality=i!=0,SharpenPercent=i==1?15:0};
                string output=Path.Combine(directory,"case-"+i);Directory.CreateDirectory(output);
                using(var recording=new DesktopRecording(new Rectangle(0,0,320,240),output,Path.Combine(output,"work"),true,options))
                {
                    // Mutating later defaults must not change a session already constructed.
                    options.VideoScale=10;options.FrameRate=25;options.SystemAudio=!options.SystemAudio;
                    recording.Start();System.Threading.Thread.Sleep(1900);string path=recording.StopAndFinalize();
                    var probe=MediaTools.Probe(path);ColorEncodingTests.CheckTags(probe);bool video=false,audio=false;
                    foreach(object item in (System.Collections.IEnumerable)probe["streams"])
                    {
                        var stream=(System.Collections.Generic.Dictionary<string,object>)item;
                        if((string)stream["codec_type"]=="audio")audio=true;
                        if((string)stream["codec_type"]=="video")
                        {
                            video=true;Require(Convert.ToInt32(stream["width"])==(i==1?240:160) && Convert.ToInt32(stream["height"])==(i==1?180:120),"Video percentage ignored");
                            string rate=(string)stream["avg_frame_rate"];string[] parts=rate.Split('/');double fps=double.Parse(parts[0],CultureInfo.InvariantCulture)/double.Parse(parts[1],CultureInfo.InvariantCulture);
                            Require((string)stream["r_frame_rate"]==expectedFps+"/1" && Math.Abs(fps-expectedFps)<0.2,"Frame rate ignored");
                        }
                    }
                    Require(video && audio==(i==1),"Mute or system audio stream selection failed");
                    Require(MediaTools.Duration(probe)>1.5 && MediaTools.Duration(probe)<5,"Recording duration invalid");
                    var metadata=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(path+".recording.json"));
                    Require(!(bool)metadata["microphone"] && (bool)metadata["drawMouse"]==(i==1),"Recorded options metadata incorrect");
                    Require(Convert.ToDecimal(metadata["videoMbps"])==(i==2?BitrateRecommendation.For(new Size(160,120),200):1.5m) && (bool)metadata["autoVideoBitrate"]==(i==2),"Recording did not use the calculated auto/manual bitrate");
                    Require((string)metadata["capture"]=="Synthetic" && (bool)metadata["preferQuality"]==(i!=0) && Convert.ToInt32(metadata["sharpenPercent"])==(i==1?15:0),"Quality/capture diagnostics incorrect");
                    Require(Convert.ToInt32(metadata["inputFrames"])>expectedFps*1.4 && Convert.ToDouble(metadata["inputFrameRate"])>expectedFps*0.9,"CFR output concealed insufficient input frames");
                }
            }
            // Check actual audio gain after AAC encoding, using a deterministic source tone.
            string ffmpeg=MediaTools.Locate("ffmpeg"),source=Path.Combine(directory,"tone.wav"),videoPath=Path.Combine(directory,"tone-video.mkv");
            MediaTools.Run(ffmpeg,"-v error -y -f lavfi -i sine=frequency=440:sample_rate=48000:duration=2 -c:a pcm_s16le "+MediaTools.Quote(source),30000);
            MediaTools.Run(ffmpeg,"-v error -y -f lavfi -i testsrc2=size=160x120:rate=24 -t 2 -c:v libx264 -preset ultrafast "+MediaTools.Quote(videoPath),30000);
            double[] powers=new double[2];
            for(int i=0;i<2;i++)
            {
                string final=Path.Combine(directory,"gain-"+i+".mp4"),pcm=Path.Combine(directory,"gain-"+i+".pcm");
                MediaTools.FinalizeVideo(ffmpeg,videoPath,source,final+".partial",final,0,new CaptureSettings{AudioVolume=i==0?100:25});
                MediaTools.Run(ffmpeg,"-v error -y -i "+MediaTools.Quote(final)+" -map 0:a:0 -ac 1 -f s16le "+MediaTools.Quote(pcm),30000);
                byte[] bytes=File.ReadAllBytes(pcm);double sum=0;for(int n=0;n+1<bytes.Length;n+=2){double sample=BitConverter.ToInt16(bytes,n);sum+=sample*sample;}powers[i]=Math.Sqrt(sum/(bytes.Length/2));
            }
            Require(powers[1]/powers[0]>0.22 && powers[1]/powers[0]<0.28,"Output audio volume ignored");
            File.WriteAllText(Path.Combine(directory,"settings-test.txt"),"PASS: persistent settings and backup; invalid bounds; exact original PNG; scaled JPEG; 50%/15fps silent MP4 without audio endpoint; 75%/60fps system-audio MP4; 200fps MP4; session snapshot isolation; microphone disabled; 25% encoded audio gain verified",Encoding.UTF8);return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 50;}
    }
}
