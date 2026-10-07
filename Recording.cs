using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Only the default RENDER endpoint is opened. No capture/microphone device is used.
internal static class AudioInterop
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class Enumerator {}
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevices
    {
        [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object service);
        [PreserveSig] int OpenPropertyStore(uint access,out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClient
    {
        [PreserveSig] int Initialize(int mode,uint flags,long duration,long period,IntPtr format,IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int mode,IntPtr format,out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal,out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICapture
    {
        [PreserveSig] int GetBuffer(out IntPtr data,out uint frames,out uint flags,out ulong position,out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
    internal static void Check(int result){if(result<0)Marshal.ThrowExceptionForHR(result);}
    internal static void Release(object value){if(value!=null && Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    internal static double Qpc(){return Stopwatch.GetTimestamp()*(10000000.0/Stopwatch.Frequency);}
    internal static double UnixNow(){return (DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;}
}

// Timestamped packets preserve silence before/between sounds instead of concatenating packets.
internal sealed class TimelineWave : IDisposable
{
    readonly FileStream file;
    readonly BinaryWriter writer;
    readonly long sizePosition,dataPosition;
    readonly byte[] silence;
    internal readonly int Rate,Align;
    internal long Frames {get;private set;}
    internal TimelineWave(string path,byte[] format)
    {
        Rate=BitConverter.ToInt32(format,4);Align=BitConverter.ToUInt16(format,12);
        if(Rate<8000 || Align<1 || Align>256)throw new IOException("电脑声音的格式无法识别。");
        silence=new byte[Align*4096];
        if(BitConverter.ToUInt16(format,0)==1 && BitConverter.ToUInt16(format,14)==8) for(int i=0;i<silence.Length;i++)silence[i]=128;
        file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        writer=new BinaryWriter(file,Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(0u);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(format.Length);writer.Write(format);if((format.Length&1)!=0)writer.Write((byte)0);
        writer.Write(Encoding.ASCII.GetBytes("data"));sizePosition=file.Position;writer.Write(0u);dataPosition=file.Position;
    }
    internal void Pad(long target)
    {
        while(Frames<target){int count=(int)Math.Min(4096,target-Frames);Write(silence,0,count);}
    }
    void Write(byte[] data,int offset,int frames)
    {
        if((Frames+(long)frames)*Align>uint.MaxValue-256)throw new IOException("录音临时文件已接近 4 GB，请停止录制后分段录制。");
        writer.Write(data,offset,frames*Align);Frames+=frames;
    }
    internal void Packet(long target,byte[] data,int frames)
    {
        target=Math.Max(0,target);Pad(target);
        int skipped=(int)Math.Min(frames,Math.Max(0,Frames-target));int count=frames-skipped;
        if(count<=0)return;
        if(data==null)Pad(Frames+count);else Write(data,skipped*Align,count);
    }
    public void Dispose()
    {
        long bytes=Frames*Align;if((bytes&1)!=0)writer.Write((byte)0);
        long length=file.Length;file.Position=4;writer.Write((uint)(length-8));file.Position=sizePosition;writer.Write((uint)bytes);
        writer.Flush();writer.Dispose();
    }
}

internal sealed class LoopbackAudio : IDisposable
{
    readonly string path;
    readonly ManualResetEvent ready=new ManualResetEvent(false),stop=new ManualResetEvent(false);
    readonly Thread worker;
    internal Exception Failure {get;private set;}
    internal double OriginUnix {get;private set;}
    internal string DeviceId {get;private set;}
    internal int Rate {get;private set;}
    internal long DataFrames {get;private set;}
    internal LoopbackAudio(string path)
    {
        this.path=path;worker=new Thread(Run);worker.IsBackground=true;worker.Name="System playback loopback";worker.SetApartmentState(ApartmentState.MTA);
    }
    internal void Start()
    {
        worker.Start();if(!ready.WaitOne(8000)){stop.Set();throw new IOException("系统声音初始化超时，请检查播放设备。");}
        if(Failure!=null)throw new IOException("无法录制电脑声音，请检查默认播放设备。",Failure);
    }
    void Run()
    {
        AudioInterop.IDevices devices=null;AudioInterop.IDevice device=null;AudioInterop.IClient client=null;AudioInterop.ICapture capture=null;IntPtr format=IntPtr.Zero;
        try
        {
            devices=(AudioInterop.IDevices)new AudioInterop.Enumerator();
            AudioInterop.Check(devices.GetDefaultAudioEndpoint(0,1,out device)); // eRender, eMultimedia
            string id;AudioInterop.Check(device.GetId(out id));DeviceId=id;
            Guid clientId=typeof(AudioInterop.IClient).GUID;object service;
            AudioInterop.Check(device.Activate(ref clientId,23,IntPtr.Zero,out service));client=(AudioInterop.IClient)service;
            AudioInterop.Check(client.GetMixFormat(out format));int length=18+(ushort)Marshal.ReadInt16(format,16);byte[] bytes=new byte[length];Marshal.Copy(format,bytes,0,length);
            AudioInterop.Check(client.Initialize(0,0x00020000,1000000,0,format,IntPtr.Zero)); // shared, LOOPBACK
            Guid captureId=typeof(AudioInterop.ICapture).GUID;AudioInterop.Check(client.GetService(ref captureId,out service));capture=(AudioInterop.ICapture)service;
            using(var wave=new TimelineWave(path,bytes))
            {
                Rate=wave.Rate;double origin=AudioInterop.Qpc();OriginUnix=AudioInterop.UnixNow();AudioInterop.Check(client.Start());ready.Set();
                while(true)
                {
                    uint packet;AudioInterop.Check(capture.GetNextPacketSize(out packet));
                    while(packet>0)
                    {
                        IntPtr data;uint frames,flags;ulong position,qpc;AudioInterop.Check(capture.GetBuffer(out data,out frames,out flags,out position,out qpc));
                        try
                        {
                            byte[] buffer=null;if((flags&2)==0){buffer=new byte[checked((int)frames*wave.Align)];Marshal.Copy(data,buffer,0,buffer.Length);DataFrames+=frames;}
                            long target=(flags&4)==0?(long)Math.Round(((double)qpc-origin)*Rate/10000000.0):Math.Max(wave.Frames,(long)((AudioInterop.Qpc()-origin)*Rate/10000000.0)-frames);
                            // Guard bad device timestamps without manufacturing hours of silence.
                            long now=(long)((AudioInterop.Qpc()-origin)*Rate/10000000.0);
                            if(target>now+Rate || target< -Rate)target=wave.Frames;
                            wave.Packet(target,buffer,(int)frames);
                        }
                        finally{AudioInterop.Check(capture.ReleaseBuffer(frames));}
                        AudioInterop.Check(capture.GetNextPacketSize(out packet));
                    }
                    if(stop.WaitOne(5)){wave.Pad((long)Math.Round((AudioInterop.Qpc()-origin)*Rate/10000000.0));break;}
                    // Keep a little headroom for packets arriving behind the wall clock.
                    wave.Pad(Math.Max(0,(long)((AudioInterop.Qpc()-origin)*Rate/10000000.0)-Rate/4));
                }
                AudioInterop.Check(client.Stop());
            }
        }
        catch(Exception ex){Failure=ex;Program.Log("AUDIO_FAILED "+ex.ToString());}
        finally
        {
            if(client!=null)try{client.Stop();}catch{}
            if(format!=IntPtr.Zero)Marshal.FreeCoTaskMem(format);
            AudioInterop.Release(capture);AudioInterop.Release(client);AudioInterop.Release(device);AudioInterop.Release(devices);ready.Set();
        }
    }
    internal void Stop(){stop.Set();if(worker.IsAlive && !worker.Join(10000))throw new IOException("系统录音没有及时停止，临时文件已保留。");if(Failure!=null)throw new IOException("系统声音录制中断，临时文件已保留。",Failure);}
    public void Dispose(){stop.Set();if(worker.IsAlive)worker.Join(10000);if(!worker.IsAlive){ready.Dispose();stop.Dispose();}}
}

internal static class MediaTools
{
    // GDI captures full-range desktop RGB. Convert the samples and tag the result
    // consistently; missing tags let HD players guess BT.709 for BT.601 samples.
    internal const string DesktopColorArguments="-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc iec61966-2-1";
    internal static string DesktopVideoFilter(CaptureSettings settings)
    {
        string filter="scale=w=max(2\\,trunc(iw*"+settings.VideoScale+"/100/2)*2):h=max(2\\,trunc(ih*"+settings.VideoScale+"/100/2)*2):flags=lanczos:in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuv420p";
        if(settings.SharpenPercent>0)filter+=",unsharp=5:5:"+(settings.SharpenPercent/100.0).ToString("0.00",CultureInfo.InvariantCulture)+":5:5:0";
        return filter;
    }
    internal static string Locate(string name)
    {
        string located=Find(name);if(located!=null)return located;
        if(DependencyInstaller.Busy)throw new ImportPendingException(30000);
        throw new FileNotFoundException("录制依赖尚未就绪，请在“连接与文件”页点击检查／安装录制依赖。");
    }
    internal static string Find(string name)
    {
        CaptureSettings settings=SettingsStore.Snapshot();string configured=name=="ffmpeg"?settings.FFmpegPath:settings.FFprobePath;
        if(!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))return configured;
        string config=Path.Combine(Program.Root,name+"-path.txt");
        if(File.Exists(config)){string path=File.ReadAllText(config,Encoding.UTF8).Trim();if(File.Exists(path))return path;}
        if(name=="ffprobe")
        {string encoder=Find("ffmpeg");if(encoder!=null){string sibling=Path.Combine(Path.GetDirectoryName(encoder),"ffprobe.exe");if(File.Exists(sibling))return sibling;}}
        string managed=DependencyInstaller.ManagedPath(Program.Root,name);if(managed!=null)return managed;
        foreach(string dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(';'))
        {try{string path=Path.Combine(dir.Trim().Trim('"'),name+".exe");if(File.Exists(path))return path;}catch(ArgumentException){}}
        return null;
    }
    internal static string Quote(string value){if(value.IndexOf('"')>=0)throw new ArgumentException("路径包含不支持的引号。");return "\""+value+"\"";}
    internal static string Run(string executable,string args,int timeout)
    {
        var info=new ProcessStartInfo(executable,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        using(var p=new Process()){p.StartInfo=info;var errors=new StringBuilder();p.ErrorDataReceived+=delegate(object s,DataReceivedEventArgs e){if(e.Data!=null)lock(errors){if(errors.Length>16000)errors.Remove(0,8000);errors.AppendLine(e.Data);}};
            var output=new StringBuilder();p.OutputDataReceived+=delegate(object s,DataReceivedEventArgs e){if(e.Data!=null)lock(output)output.AppendLine(e.Data);};
            p.Start();p.BeginErrorReadLine();p.BeginOutputReadLine();if(!p.WaitForExit(timeout)){p.Kill();p.WaitForExit();throw new IOException("视频处理超时，临时文件已保留。");}p.WaitForExit();
            if(p.ExitCode!=0)throw new IOException("视频处理失败："+errors.ToString());return output.ToString();}
    }
    internal static Dictionary<string,object> Probe(string path)
    {
        string json=Run(Locate("ffprobe"),"-v error -show_entries format=duration:stream=codec_type,codec_name,width,height,r_frame_rate,avg_frame_rate,duration,pix_fmt,color_range,color_space,color_transfer,color_primaries -of json "+Quote(path),30000);
        return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
    }
    internal static double Duration(Dictionary<string,object> probe)
    {return double.Parse((string)((Dictionary<string,object>)probe["format"])["duration"],CultureInfo.InvariantCulture);}
    internal static void FinalizeVideo(string ffmpeg,string video,string audio,string partial,string final,double offset)
    {FinalizeVideo(ffmpeg,video,audio,partial,final,offset,new CaptureSettings());}
    internal static void FinalizeVideo(string ffmpeg,string video,string audio,string partial,string final,double offset,CaptureSettings settings)
    {
        double duration=Duration(Probe(video));if(duration<=0)throw new IOException("录制中没有生成有效画面。");
        string audioOffset=offset>=0?"-ss "+offset.ToString("0.000000",CultureInfo.InvariantCulture):"-itsoffset "+(-offset).ToString("0.000000",CultureInfo.InvariantCulture);
        string audioArgs=settings.SystemAudio?" "+audioOffset+" -i "+Quote(audio)+" -map 0:v:0 -map 1:a:0 -c:a aac -b:a "+settings.AudioKbps+"k -ac 2 -af \"volume="+(settings.AudioVolume/100.0).ToString("0.00",CultureInfo.InvariantCulture)+",apad\"":" -map 0:v:0 -an";
        Run(ffmpeg,"-hide_banner -loglevel error -y -i "+Quote(video)+audioArgs+" -c:v copy -t "+duration.ToString("0.000000",CultureInfo.InvariantCulture)+" -movflags +faststart -f mp4 "+Quote(partial),1800000);
        var probe=Probe(partial);bool hasVideo=false,hasAudio=false;
        foreach(object stream in (System.Collections.IEnumerable)probe["streams"]){var item=(Dictionary<string,object>)stream;if((string)item["codec_type"]=="video")hasVideo=(string)item["codec_name"]=="h264";if((string)item["codec_type"]=="audio")hasAudio=(string)item["codec_name"]=="aac";}
        if(!hasVideo || hasAudio!=settings.SystemAudio || Duration(probe)<=0)throw new IOException("视频验证失败，临时文件已保留。");
        VideoValidation.Ensure(partial);
        File.Move(partial,final);
        File.Move(partial+".validated.json",final+".validated.json");
    }
}

// Probe checks the container; a complete decode also catches truncated/corrupt packets.
internal static class VideoValidation
{
    internal static bool IsValidated(string path,string hash)
    {
        string marker=path+".validated.json";
        if(!File.Exists(marker))return false;
        try{var old=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(marker,Encoding.UTF8));return old.ContainsKey("sha256") && (string)old["sha256"]==hash && old.ContainsKey("fullDecode") && (bool)old["fullDecode"];}catch(ArgumentException){return false;}
    }
    internal static void Ensure(string path)
    {
        string hash=CaptureContext.Hash(path),marker=path+".validated.json";
        if(IsValidated(path,hash))return;
        var probe=MediaTools.Probe(path);double duration=MediaTools.Duration(probe);bool video=false;
        if(double.IsNaN(duration) || double.IsInfinity(duration) || duration<=0)throw new IOException("视频时长无效，已阻止上传。");
        foreach(object obj in (System.Collections.IEnumerable)probe["streams"])
        {
            var stream=(Dictionary<string,object>)obj;
            if((string)stream["codec_type"]=="video")video=(string)stream["codec_name"]=="h264" && stream.ContainsKey("pix_fmt") && (string)stream["pix_fmt"]=="yuv420p" && Convert.ToInt32(stream["width"])>0 && Convert.ToInt32(stream["height"])>0;
            if((string)stream["codec_type"]=="audio" && (string)stream["codec_name"]!="aac")throw new IOException("音轨格式不兼容，已阻止上传。");
        }
        if(!video)throw new IOException("视频不是兼容的 H.264 画面，已阻止上传。");
        int timeout=(int)Math.Min(1800000,Math.Max(60000,duration*4000+30000));
        MediaTools.Run(MediaTools.Locate("ffmpeg"),"-v error -xerror -err_detect explode -threads 2 -i "+MediaTools.Quote(path)+" -map 0:v:0 -map 0:a? -f null NUL",timeout);
        if(CaptureContext.Hash(path)!=hash)throw new IOException("验证期间视频被修改，已阻止上传。");
        CaptureContext.WriteMarker(marker,new {sha256=hash,fullDecode=true,duration=duration,validatedAt=DateTime.UtcNow.ToString("o")});
        Program.Log("VIDEO_VALIDATED "+Path.GetFileName(path)+" fullDecode=true");
    }
}

internal static class VideoEncoding
{
    static readonly object gate=new object();
    static readonly Dictionary<string,bool> nvenc=new Dictionary<string,bool>(),duplication=new Dictionary<string,bool>();
    internal static bool SupportsNvenc(string ffmpeg)
    {
        lock(gate)
        {
            bool supported;if(nvenc.TryGetValue(ffmpeg,out supported))return supported;
            try{MediaTools.Run(ffmpeg,"-hide_banner -loglevel error -f lavfi -i testsrc2=size=160x120:rate=30 -frames:v 1 -an -c:v h264_nvenc -preset p5 -tune hq -rc constqp -qp 20 -f null -",8000);supported=true;}
            catch(Exception ex){supported=false;Program.Log("NVENC_UNAVAILABLE "+ex.Message);}
            nvenc[ffmpeg]=supported;return supported;
        }
    }
    internal static bool SupportsDuplication(string ffmpeg)
    {
        lock(gate)
        {
            bool supported;if(duplication.TryGetValue(ffmpeg,out supported))return supported;
            try{supported=MediaTools.Run(ffmpeg,"-hide_banner -filters",5000).Contains("ddagrab");}catch{supported=false;}
            duplication[ffmpeg]=supported;return supported;
        }
    }
    internal static bool CanDuplicate(Rectangle bounds,Rectangle primary,int screens){return screens==1 && bounds==primary;}
    internal static string Select(string ffmpeg,CaptureSettings settings)
    {return settings.VideoEncoder=="NVENC" || (settings.VideoEncoder=="Auto" && SupportsNvenc(ffmpeg))?"h264_nvenc":"libx264";}
    internal static string Arguments(CaptureSettings settings,string encoder)
    {
        string rate=settings.VideoMbps.ToString("0.0",CultureInfo.InvariantCulture)+"M",peak=(settings.VideoMbps*2).ToString("0.0",CultureInfo.InvariantCulture)+"M",buffer=(settings.VideoMbps*4).ToString("0.0",CultureInfo.InvariantCulture)+"M";
        string shared=" -profile:v high -g "+(settings.FrameRate*2)+" -bf 2 "+MediaTools.DesktopColorArguments;
        if(encoder=="h264_nvenc")return "-c:v h264_nvenc -preset p5 -tune hq -rc-lookahead 0 -spatial-aq 1 -temporal-aq 1 -multipass qres"+shared+(settings.PreferQuality?" -rc constqp -qp "+settings.QualityLevel:" -rc vbr -cq "+settings.QualityLevel+" -b:v "+rate+" -maxrate "+peak+" -bufsize "+buffer);
        return "-c:v libx264 -preset veryfast"+shared+(settings.PreferQuality?" -crf "+settings.QualityLevel:" -b:v "+rate+" -maxrate "+peak+" -bufsize "+buffer);
    }
}

internal sealed class DesktopRecording : IDisposable
{
    readonly string ffmpeg;
    readonly Rectangle bounds;
    readonly bool synthetic;
    readonly CaptureSettings settings;
    readonly string rawVideo,rawAudio,partial;
    readonly ManualResetEvent firstFrame=new ManualResetEvent(false);
    readonly Queue<string> errors=new Queue<string>();
    Process video;LoopbackAudio audio;
    double firstUnix,timeBase=0.000001;
    double firstInputSeconds,lastInputSeconds,maxInputGap;
    int capturedFrames,duplicateFrames,droppedFrames;
    string captureBackend,videoEncoder;
    internal readonly string FinalPath;
    internal string WorkPath {get;private set;}
    internal DateTime StartedAt {get;private set;}
    internal bool Healthy {get{return video!=null && !video.HasExited && (!settings.SystemAudio || (audio!=null && audio.Failure==null));}}
    internal DesktopRecording(Rectangle bounds,string pending,string workRoot,bool synthetic=false,CaptureSettings options=null)
    {
        this.bounds=bounds;this.synthetic=synthetic;settings=(options??SettingsStore.Snapshot()).Copy();settings.Normalize();settings.VideoMbps=settings.EffectiveVideoMbps(synthetic?new Size(320,240):bounds.Size);ffmpeg=MediaTools.Locate("ffmpeg");MediaTools.Locate("ffprobe");
        string name="Recording-"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6);
        WorkPath=Path.Combine(workRoot,name);Directory.CreateDirectory(WorkPath);Directory.CreateDirectory(pending);
        rawVideo=Path.Combine(WorkPath,"desktop.mkv");rawAudio=Path.Combine(WorkPath,"system.wav");partial=Path.Combine(WorkPath,"video.partial.mp4");FinalPath=Path.Combine(pending,name+".mp4");
    }
    internal void Start()
    {
        videoEncoder=VideoEncoding.Select(ffmpeg,settings);
        if(settings.SystemAudio){audio=new LoopbackAudio(rawAudio);audio.Start();}
        bool useDuplication=!synthetic && settings.CaptureMethod=="Auto" && VideoEncoding.CanDuplicate(bounds,Screen.PrimaryScreen.Bounds,Screen.AllScreens.Length) && VideoEncoding.SupportsDuplication(ffmpeg);
        Exception failure=null;
        var attempts=new List<string>();if(useDuplication)attempts.Add("DXGI");attempts.Add(synthetic?"Synthetic":"GDI");
        foreach(string backend in attempts)
        {
            try{StartVideo(backend,videoEncoder);failure=null;break;}
            catch(Exception ex){failure=ex;Program.Log("RECORD_BACKEND_FAILED "+backend+" "+videoEncoder+" "+ex.Message);CloseVideo();}
        }
        if(failure!=null && settings.VideoEncoder=="Auto" && videoEncoder!="libx264")
        {
            videoEncoder="libx264";try{StartVideo(synthetic?"Synthetic":"GDI",videoEncoder);failure=null;}catch(Exception ex){failure=ex;CloseVideo();}
        }
        if(failure!=null)throw failure;
        StartedAt=DateTime.Now;
        File.WriteAllText(Path.Combine(WorkPath,"session.json"),new JavaScriptSerializer().Serialize(new {finalPath=FinalPath,startedAt=StartedAt.ToString("o"),videoOriginUnix=firstUnix,audioOriginUnix=audio==null?0:audio.OriginUnix,drawMouse=settings.DrawMouse,microphone=false,audioDevice=audio==null?"":audio.DeviceId,videoScale=settings.VideoScale,frameRate=settings.FrameRate,videoMbps=settings.VideoMbps,autoVideoBitrate=settings.AutoVideoBitrate,encoder=videoEncoder,capture=captureBackend,preferQuality=settings.PreferQuality,qualityLevel=settings.QualityLevel,sharpenPercent=settings.SharpenPercent,systemAudio=settings.SystemAudio,audioVolume=settings.AudioVolume,audioKbps=settings.AudioKbps,screen=new {x=bounds.X,y=bounds.Y,width=bounds.Width,height=bounds.Height}}),Encoding.UTF8);
        Program.Log("RECORD_STARTED "+FinalPath+" capture="+captureBackend+" encoder="+videoEncoder+" qualityMode="+settings.PreferQuality+" quality="+settings.QualityLevel+" systemAudio="+settings.SystemAudio+" drawMouse="+settings.DrawMouse+" scale="+settings.VideoScale+" fps="+settings.FrameRate+" MbpsReference="+settings.VideoMbps);
    }
    void CloseVideo()
    {
        if(video==null)return;
        try{if(!video.HasExited){video.StandardInput.WriteLine("q");if(!video.WaitForExit(1000)){video.Kill();video.WaitForExit();}}}catch{}
        video.Dispose();video=null;
    }
    void StartVideo(string backend,string encoder)
    {
        captureBackend=backend;firstFrame.Reset();firstUnix=0;timeBase=0.000001;capturedFrames=duplicateFrames=droppedFrames=0;firstInputSeconds=lastInputSeconds=maxInputGap=0;
        // Give lavfi a microsecond time base before wall-clock timestamps are applied;
        // a 1/FPS time base would quantize capture timing and create extra CFR repeats.
        string input=synthetic?"-re -f lavfi -i testsrc2=size=320x240:rate="+settings.FrameRate+",format=bgra":backend=="DXGI"?"-f lavfi -use_wallclock_as_timestamps 1 -i ddagrab=output_idx=0:output_fmt=bgra:draw_mouse="+(settings.DrawMouse?"1":"0")+":framerate="+settings.FrameRate+":video_size="+bounds.Width+"x"+bounds.Height+",hwdownload,format=bgra,settb=1/1000000":"-f gdigrab -use_wallclock_as_timestamps 1 -draw_mouse "+(settings.DrawMouse?"1":"0")+" -framerate "+settings.FrameRate+" -offset_x "+bounds.X+" -offset_y "+bounds.Y+" -video_size "+bounds.Width+"x"+bounds.Height+" -i desktop";
        // Disable per-frame CRC: calculating it on full-resolution BGRA wastes CPU.
        string filter="showinfo=checksum=0,setpts=PTS-STARTPTS,"+MediaTools.DesktopVideoFilter(settings);
        string args="-hide_banner -loglevel info -y -copyts "+input+" -an -vf \""+filter+"\" "+VideoEncoding.Arguments(settings,encoder)+" -pix_fmt yuv420p -fps_mode cfr -r "+settings.FrameRate+" -progress pipe:1 -nostats -f matroska "+MediaTools.Quote(rawVideo);
        video=new Process();video.StartInfo=new ProcessStartInfo(ffmpeg,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true};
        video.StartInfo.RedirectStandardOutput=true;video.OutputDataReceived+=delegate(object s,DataReceivedEventArgs e){if(e.Data==null)return;string[] parts=e.Data.Split('=');int count;if(parts.Length==2 && int.TryParse(parts[1],out count)){if(parts[0]=="dup_frames")duplicateFrames=count;else if(parts[0]=="drop_frames")droppedFrames=count;}};
        video.ErrorDataReceived+=delegate(object s,DataReceivedEventArgs e)
        {
            if(e.Data==null)return;
            var tb=Regex.Match(e.Data,@"config in time_base:\s*(\d+)/(\d+)");
            if(tb.Success)timeBase=double.Parse(tb.Groups[1].Value,CultureInfo.InvariantCulture)/double.Parse(tb.Groups[2].Value,CultureInfo.InvariantCulture);
            var match=Regex.Match(e.Data,@"n:\s*(\d+)\s+pts:\s*(-?\d+)");
            if(match.Success){long stamp;if(long.TryParse(match.Groups[2].Value,out stamp)){double seconds=stamp*timeBase;if(capturedFrames==0){firstInputSeconds=seconds;firstUnix=synthetic?AudioInterop.UnixNow():seconds;firstFrame.Set();}else maxInputGap=Math.Max(maxInputGap,seconds-lastInputSeconds);lastInputSeconds=seconds;capturedFrames++;}}
            lock(errors){errors.Enqueue(e.Data);while(errors.Count>40)errors.Dequeue();}
        };
        Process attempt=video;video.EnableRaisingEvents=true;video.Exited+=delegate{if(video==attempt)firstFrame.Set();};
        video.Start();video.BeginErrorReadLine();video.BeginOutputReadLine();
        if(!firstFrame.WaitOne(10000) || firstUnix<=0 || !Healthy)throw new IOException("录屏未能启动，请检查 FFmpeg。\n"+RecentError());
        if(!synthetic && Math.Abs(firstUnix-AudioInterop.UnixNow())>30)throw new IOException("捕获时间戳与系统时间不一致，不能保证音画同步。");
    }
    string RecentError(){lock(errors)return string.Join("\n",errors.ToArray());}
    internal string StopAndFinalize()
    {
        Exception captureError=null;
        try
        {
            if(video==null)throw new IOException("录屏未启动。");
            if(!video.HasExited){video.StandardInput.WriteLine("q");video.StandardInput.Flush();if(!video.WaitForExit(20000)){video.Kill();video.WaitForExit();throw new IOException("录屏停止超时，临时文件已保留。");}}
            video.WaitForExit();if(video.ExitCode!=0)throw new IOException("录屏中断："+RecentError());
        }
        catch(Exception ex){captureError=ex;}
        try{if(audio!=null)audio.Stop();}catch(Exception ex){if(captureError==null)captureError=ex;}
        if(captureError!=null)throw captureError;
        double offset=audio==null?0:firstUnix-audio.OriginUnix;
        // Some builds do not retain wall-clock PTS through the input path; do not seek by an epoch-sized value.
        if(Math.Abs(offset)>30)throw new IOException("无法确认录屏音画时间基准，临时文件已保留。");
        MediaTools.FinalizeVideo(ffmpeg,rawVideo,rawAudio,partial,FinalPath,offset,settings);
        double inputFps=capturedFrames>1 && lastInputSeconds>firstInputSeconds?(capturedFrames-1)/(lastInputSeconds-firstInputSeconds):0;
        File.WriteAllText(FinalPath+".recording.json",new JavaScriptSerializer().Serialize(new {systemAudioOnly=settings.SystemAudio,microphone=false,drawMouse=settings.DrawMouse,frameRate=settings.FrameRate,videoScale=settings.VideoScale,videoMbps=settings.VideoMbps,autoVideoBitrate=settings.AutoVideoBitrate,encoder=videoEncoder,capture=captureBackend,preferQuality=settings.PreferQuality,qualityLevel=settings.QualityLevel,sharpenPercent=settings.SharpenPercent,inputFrames=capturedFrames,inputFrameRate=inputFps,maxInputGapSeconds=maxInputGap,duplicatedOutputFrames=duplicateFrames,droppedOutputFrames=droppedFrames,audioVolume=settings.AudioVolume,audioKbps=settings.AudioKbps,audioOffsetSeconds=offset,duration=MediaTools.Duration(MediaTools.Probe(FinalPath)),audioDataFrames=audio==null?0:audio.DataFrames}),Encoding.UTF8);
        Program.Log("RECORD_TIMING inputFrames="+capturedFrames+" inputFps="+inputFps.ToString("0.00",CultureInfo.InvariantCulture)+" maxGapMs="+(maxInputGap*1000).ToString("0.0",CultureInfo.InvariantCulture)+" outputDup="+duplicateFrames+" outputDrop="+droppedFrames);
        Program.Log("RECORD_FINALIZED "+FinalPath+" offset="+offset.ToString("0.000000",CultureInfo.InvariantCulture));
        // Delete only known intermediates from this session, after a validated MP4 exists locally.
        try{File.Delete(rawVideo);File.Delete(rawAudio);File.Delete(Path.Combine(WorkPath,"session.json"));Directory.Delete(WorkPath,false);}catch(IOException ex){Program.Log("RECORD_CLEANUP_DEFERRED "+ex.Message);}catch(UnauthorizedAccessException ex){Program.Log("RECORD_CLEANUP_DEFERRED "+ex.Message);}
        return FinalPath;
    }
    public void Dispose()
    {
        if(video!=null){try{if(!video.HasExited){video.StandardInput.WriteLine("q");if(!video.WaitForExit(3000)){video.Kill();video.WaitForExit();}}}catch{}video.Dispose();video=null;}
        if(audio!=null){audio.Dispose();audio=null;}firstFrame.Dispose();
    }
}

internal sealed class RecordingBadge : Form
{
    readonly Label label=new Label();bool excluded;
    internal RecordingBadge()
    {
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
        ClientSize=new Size(260,40);BackColor=Color.FromArgb(95,26,31);
        label.Dock=DockStyle.Fill;label.ForeColor=Color.White;label.Font=new Font("Microsoft YaHei UI",10);label.TextAlign=ContentAlignment.MiddleCenter;Controls.Add(label);
    }
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);excluded=Native.SetWindowDisplayAffinity(Handle,0x11);}
    internal void UpdateTime(DateTime started)
    {
        TimeSpan elapsed=DateTime.Now-started;label.Text="● 录制中 "+((int)elapsed.TotalMinutes).ToString("00")+":"+elapsed.Seconds.ToString("00")+" · 长按停止";
        if(!IsHandleCreated)CreateHandle();if(!excluded)return;
        Rectangle screen=Screen.PrimaryScreen.WorkingArea;Location=new Point(screen.Right-Width-24,screen.Top+24);if(!Visible)Show();
    }
}

internal static class ColorEncodingTests
{
    internal static void CheckTags(Dictionary<string,object> probe)
    {
        bool found=false;
        foreach(object value in (System.Collections.IEnumerable)probe["streams"])
        {
            var stream=(Dictionary<string,object>)value;if((string)stream["codec_type"]!="video")continue;found=true;
            foreach(var expected in new Dictionary<string,string>{{"pix_fmt","yuv420p"},{"color_range","tv"},{"color_space","bt709"},{"color_primaries","bt709"},{"color_transfer","iec61966-2-1"}})
                if(!stream.ContainsKey(expected.Key) || (string)stream[expected.Key]!=expected.Value)
                    throw new Exception("Incorrect/missing video color tag: "+expected.Key);
        }
        if(!found)throw new Exception("Missing video stream");
    }
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            var colors=new List<Color>();
            foreach(int gray in new[]{0,4,8,16,24,32,64,96,128,160,192,224,240,248,252,255})colors.Add(Color.FromArgb(gray,gray,gray));
            colors.AddRange(new[]{Color.Red,Color.Lime,Color.Blue,Color.Cyan,Color.Magenta,Color.Yellow,
                Color.FromArgb(128,0,0),Color.FromArgb(0,128,0),Color.FromArgb(0,0,128),Color.FromArgb(0,128,128),
                Color.FromArgb(128,0,128),Color.FromArgb(128,128,0),Color.FromArgb(220,120,70),Color.FromArgb(25,90,160),
                Color.FromArgb(80,170,105),Color.FromArgb(140,75,190)});
            string ffmpeg=MediaTools.Locate("ffmpeg");int worst=0;
            for(int test=0;test<3;test++)
            {
                Size size=test==0?new Size(320,240):new Size(1280,720);
                var settings=new CaptureSettings{SystemAudio=false,VideoScale=test==2?50:100,AutoVideoBitrate=false,VideoMbps=8};
                string prefix=Path.Combine(directory,"case-"+test),source=prefix+".bmp",raw=prefix+".mkv",final=prefix+".mp4",decoded=prefix+".png";
                using(var image=new Bitmap(size.Width,size.Height,System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using(var graphics=Graphics.FromImage(image))
                        for(int i=0;i<colors.Count;i++)using(var brush=new SolidBrush(colors[i]))graphics.FillRectangle(brush,(i%8)*size.Width/8,(i/8)*size.Height/4,size.Width/8,size.Height/4);
                    image.Save(source,System.Drawing.Imaging.ImageFormat.Bmp);
                }
                settings.VideoEncoder=test==0?"x264":"Auto";
                MediaTools.Run(ffmpeg,"-hide_banner -loglevel error -y -loop 1 -framerate 60 -i "+MediaTools.Quote(source)+" -t 0.5 -an -vf \""+MediaTools.DesktopVideoFilter(settings)+"\" "+VideoEncoding.Arguments(settings,VideoEncoding.Select(ffmpeg,settings))+" "+MediaTools.Quote(raw),30000);
                CheckTags(MediaTools.Probe(raw));
                MediaTools.FinalizeVideo(ffmpeg,raw,null,prefix+".partial.mp4",final,0,settings);
                var probe=MediaTools.Probe(final);CheckTags(probe);
                MediaTools.Run(ffmpeg,"-hide_banner -loglevel error -y -i "+MediaTools.Quote(final)+" -frames:v 1 -pix_fmt rgb24 "+MediaTools.Quote(decoded),30000);
                using(var image=new Bitmap(decoded))
                {
                    Size expected=settings.VideoSize(size);if(image.Size!=expected)throw new Exception("Color test scale was ignored");
                    for(int i=0;i<colors.Count;i++)
                    {
                        Color actual=image.GetPixel((i%8)*image.Width/8+image.Width/16,(i/8)*image.Height/4+image.Height/8),original=colors[i];
                        int error=Math.Max(Math.Abs(actual.R-original.R),Math.Max(Math.Abs(actual.G-original.G),Math.Abs(actual.B-original.B)));
                        worst=Math.Max(worst,error);if(error>6)throw new Exception("RGB roundtrip differs in case "+test+", patch "+i+": "+original+" -> "+actual+"; error="+error);
                    }
                }
            }
            File.WriteAllText(Path.Combine(directory,"color-test.txt"),"PASS: full-range desktop RGB -> limited BT.709 YUV -> RGB; black/white, 16 gray levels and 16 colors, SD/HD/scaled output; MKV and MP4 retain matrix/range/primaries/sRGB transfer; worst channel error="+worst+"/255",Encoding.UTF8);
            return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 31;}
    }
}

internal static class RecordingTests
{
    internal static int Run(string mode,string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            if(mode=="--test-loopback")
            {
                using(var audio=new LoopbackAudio(Path.Combine(directory,"loopback.wav"))){audio.Start();Thread.Sleep(2500);audio.Stop();File.WriteAllText(Path.Combine(directory,"loopback-test.txt"),"PASS: render endpoint only; rate="+audio.Rate+" packets="+audio.DataFrames,Encoding.UTF8);}return 0;
            }
            if(mode=="--test-recording" || mode=="--test-recording-pipeline")
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                using(var recording=new DesktopRecording(SystemInformation.VirtualScreen,directory,Path.Combine(directory,"sessions"),mode=="--test-recording-pipeline")){recording.Start();Thread.Sleep(2500);string path=recording.StopAndFinalize();double recordingDuration=MediaTools.Duration(MediaTools.Probe(path));if(recordingDuration<2.4 || recordingDuration>6)throw new Exception("Unexpected recording duration: "+recordingDuration);File.WriteAllText(Path.Combine(directory,"recording-test.txt"),"PASS: "+(mode=="--test-recording"?"desktop capture":"synthetic video")+", loopback, graceful stop, H264/AAC MP4, original retained; "+path,Encoding.UTF8);}return 0;
            }
            byte[] format=new byte[18];Buffer.BlockCopy(BitConverter.GetBytes((ushort)1),0,format,0,2);Buffer.BlockCopy(BitConverter.GetBytes((ushort)1),0,format,2,2);Buffer.BlockCopy(BitConverter.GetBytes(48000),0,format,4,4);Buffer.BlockCopy(BitConverter.GetBytes(96000),0,format,8,4);Buffer.BlockCopy(BitConverter.GetBytes((ushort)2),0,format,12,2);Buffer.BlockCopy(BitConverter.GetBytes((ushort)16),0,format,14,2);
            string wav=Path.Combine(directory,"timeline.wav");byte[] tone=new byte[48000];for(int i=0;i<24000;i++){short sample=(short)(Math.Sin(2*Math.PI*440*i/48000)*12000);tone[2*i]=(byte)sample;tone[2*i+1]=(byte)(sample>>8);}
            using(var wave=new TimelineWave(wav,format)){wave.Packet(48000,tone,24000);wave.Packet(60000,tone,24000);wave.Packet(96000,tone,24000);wave.Pad(144000);if(wave.Frames!=144000)throw new Exception("Audio timeline length incorrect");}
            byte[] data=File.ReadAllBytes(wav);int start=46;if(data.Length!=start+288000)throw new Exception("Wave header incorrect");for(int i=start;i<start+96000;i++)if(data[i]!=0)throw new Exception("Leading silence lost");
            for(int i=start+168000;i<start+192000;i++)if(data[i]!=0)throw new Exception("Silent gap lost");
            string video=Path.Combine(directory,"synthetic.mkv"),partial=Path.Combine(directory,"synthetic.partial.mp4"),final=Path.Combine(directory,"synthetic.mp4");
            MediaTools.Run(MediaTools.Locate("ffmpeg"),"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=320x240:rate=30 -t 2.5 -an -c:v libx264 -preset ultrafast "+MediaTools.Quote(video),30000);
            MediaTools.FinalizeVideo(MediaTools.Locate("ffmpeg"),video,wav,partial,final,0.5);
            double duration=MediaTools.Duration(MediaTools.Probe(final));if(Math.Abs(duration-2.5)>0.15)throw new Exception("Mux duration differs from video");
            File.WriteAllText(Path.Combine(directory,"media-test.txt"),"PASS: leading/middle/trailing silence, overlapping packets, timestamp alignment, H264/AAC finalization, retained MP4 and duration",Encoding.UTF8);return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 30;}
    }
}
