using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Private, portable tools: no machine PATH changes, registry writes or elevation.
internal static class DependencyInstaller
{
    internal const string ReleaseApi="https://api.github.com/repos/GyanD/codexffmpeg/releases/latest";
    internal const string PackageSource="https://github.com/GyanD/codexffmpeg/releases";
    static readonly object Gate=new object();
    static bool busy,available;
    static string status="首次启动会自动检查录制依赖。";
    internal static bool Busy {get{lock(Gate)return busy;}}
    internal static bool Available {get{lock(Gate)return available;}}
    internal static string Status {get{lock(Gate)return status;}}
    static void Update(string value){lock(Gate)status=value;}
    internal static string ManagedPath(string root,string name)
    {
        string marker=Path.Combine(root,"Tools","ffmpeg","current.json");
        if(!File.Exists(marker))return null;
        try
        {
            var receipt=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(marker,Encoding.UTF8));
            string hash=Convert.ToString(receipt["sha256"]);
            if(!Regex.IsMatch(hash??"",@"\A[0-9a-fA-F]{64}\z"))return null;
            string path=Path.Combine(root,"Tools","ffmpeg",hash.ToLowerInvariant(),"bin",name+".exe");
            return File.Exists(path)?path:null;
        }
        catch(IOException){return null;}catch(ArgumentException){return null;}catch(KeyNotFoundException){return null;}
    }
    internal static void Start()
    {
        lock(Gate){if(busy)return;busy=true;available=false;status="正在检查录制依赖…";}
        var worker=new Thread(delegate()
        {
            try
            {
                CaptureSettings previous=SettingsStore.Snapshot();string ffmpeg=MediaTools.Find("ffmpeg"),ffprobe=MediaTools.Find("ffprobe");bool usable=false;
                if(ffmpeg!=null && ffprobe!=null)try{ValidateVersions(ffmpeg,ffprobe);usable=true;}catch(Exception ex){Program.Log("DEPENDENCIES_EXISTING_INVALID "+ex.Message);}
                if(usable)
                {
                    Update("录制依赖已就绪 · 已复用现有 FFmpeg / ffprobe");
                }
                else
                {
                    InstallOnline(Program.Root,Update);
                    SettingsStore.ApplyDependencyPaths(ManagedPath(Program.Root,"ffmpeg"),ManagedPath(Program.Root,"ffprobe"),previous);
                    Update("录制依赖安装完成 · FFmpeg / ffprobe 已就绪");
                }
                lock(Gate)available=true;
                Program.Log("DEPENDENCIES_READY");
            }
            catch(Exception ex){Update("依赖安装未完成，可点击重试；截图仍可使用。");Program.Log("DEPENDENCIES_FAILED "+ex.Message);}
            finally{lock(Gate)busy=false;}
        });worker.IsBackground=true;worker.Name="EaglePrtSc dependency installer";worker.Start();
    }
    static void ValidateVersions(string ffmpeg,string ffprobe)
    {
        if(!MediaTools.Run(ffmpeg,"-version",15000).StartsWith("ffmpeg version",StringComparison.OrdinalIgnoreCase) || !MediaTools.Run(ffprobe,"-version",15000).StartsWith("ffprobe version",StringComparison.OrdinalIgnoreCase))throw new IOException("无法运行 FFmpeg / ffprobe，请检查安装位置。");
    }
    static void ValidatePackage(string ffmpeg,string ffprobe)
    {
        ValidateVersions(ffmpeg,ffprobe);
        string encoders=MediaTools.Run(ffmpeg,"-hide_banner -encoders",15000),devices=MediaTools.Run(ffmpeg,"-hide_banner -devices",15000),filters=MediaTools.Run(ffmpeg,"-hide_banner -filters",15000);
        if(!encoders.Contains("libx264") || !Regex.IsMatch(encoders,@"\baac\b") || !devices.Contains("gdigrab") || !filters.Contains("ddagrab"))throw new IOException("下载的 FFmpeg 不包含本程序需要的录制功能。");
    }
    static HttpWebRequest DownloadRequest(string url)
    {
        var request=(HttpWebRequest)WebRequest.Create(url);request.Timeout=30000;request.ReadWriteTimeout=30000;request.UserAgent="EaglePrtSc/5.11";return request;
    }
    internal static KeyValuePair<string,string> ResolvePackage(string json)
    {
        var release=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
        foreach(object value in (System.Collections.IEnumerable)release["assets"])
        {
            var asset=(Dictionary<string,object>)value;string name=Convert.ToString(asset["name"]);
            if(!Regex.IsMatch(name,@"\Affmpeg-[0-9]+\.[0-9]+(?:\.[0-9]+)?-essentials_build\.zip\z"))continue;
            string url=Convert.ToString(asset["browser_download_url"]),digest=asset.ContainsKey("digest")?Convert.ToString(asset["digest"]):"";
            var checksum=Regex.Match(digest,@"\Asha256:([0-9a-fA-F]{64})\z");
            Uri address;if(!checksum.Success || !Uri.TryCreate(url,UriKind.Absolute,out address) || address.Scheme!="https" || address.Host!="github.com" || !address.AbsolutePath.StartsWith("/GyanD/codexffmpeg/releases/download/",StringComparison.Ordinal))throw new IOException("官方下载信息缺少有效的 SHA-256 或下载地址。");
            return new KeyValuePair<string,string>(url,checksum.Groups[1].Value.ToLowerInvariant());
        }
        throw new IOException("官方镜像未提供配套的 Windows FFmpeg 安装包。");
    }
    static KeyValuePair<string,string> ReadPackage()
    {
        using(var response=DownloadRequest(ReleaseApi).GetResponse())using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))return ResolvePackage(reader.ReadToEnd());
    }
    internal static void InstallOnline(string root,Action<string> progress)
    {
        ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
        string tools=Path.Combine(root,"Tools","ffmpeg");Directory.CreateDirectory(tools);
        // A named mutex also serializes a command-line installer with the tray app.
        string identity;using(var sha=SHA256.Create())identity=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()))).Replace("-","");
        using(var mutex=new Mutex(false,"Local\\EaglePrtSc-FFmpeg-"+identity))
        {
            bool owned=false;
            try
            {
                try{owned=mutex.WaitOne(1000);}catch(AbandonedMutexException){owned=true;}
                if(!owned)throw new IOException("另一个安装任务正在运行，请稍后重试。");
                progress("正在获取 FFmpeg 下载信息…");var package=ReadPackage();string checksum=package.Value;
                string download=Path.Combine(tools,"download-"+Guid.NewGuid().ToString("N")+".zip.partial");
                try
                {
                    using(var response=DownloadRequest(package.Key).GetResponse())using(var input=response.GetResponseStream())using(var output=new FileStream(download,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {
                        long length=response.ContentLength,total=0;byte[] buffer=new byte[131072];int count,lastPercent=-1;var elapsed=Stopwatch.StartNew();
                        while((count=input.Read(buffer,0,buffer.Length))>0)
                        {
                            output.Write(buffer,0,count);total+=count;
                            if(total>512L*1024*1024 || elapsed.Elapsed>TimeSpan.FromMinutes(20))throw new IOException("依赖下载超出大小或时间限制，请重试。");
                            int percent=length>0?(int)Math.Min(100,total*100/length):(int)(total/(1024*1024));
                            if(percent!=lastPercent){lastPercent=percent;progress("正在下载 FFmpeg · "+(length>0?percent+"%":percent+" MB"));}
                        }
                        output.Flush(true);
                        if(length>0 && length!=total)throw new IOException("FFmpeg 下载不完整，请重试。");
                    }
                    progress("正在校验并安装 FFmpeg / ffprobe…");
                    InstallArchive(root,download,checksum,ValidatePackage);
                }
                finally{try{if(File.Exists(download))File.Delete(download);}catch(IOException ex){Program.Log("DEPENDENCIES_DOWNLOAD_CLEANUP_DEFERRED "+ex.Message);}catch(UnauthorizedAccessException ex){Program.Log("DEPENDENCIES_DOWNLOAD_CLEANUP_DEFERRED "+ex.Message);}}
            }
            finally{if(owned)mutex.ReleaseMutex();}
        }
    }
    static string HashFile(string path)
    {using(var sha=SHA256.Create())using(var input=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
    internal static void InstallArchive(string root,string archivePath,string expectedHash,Action<string,string> validate)
    {
        if(!Regex.IsMatch(expectedHash??"",@"\A[0-9a-fA-F]{64}\z") || !string.Equals(HashFile(archivePath),expectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException("FFmpeg 文件校验失败，未安装；请重试下载。");
        string tools=Path.Combine(Path.GetFullPath(root),"Tools","ffmpeg"),destination=Path.Combine(tools,expectedHash.ToLowerInvariant());Directory.CreateDirectory(tools);
        string staging=Path.Combine(tools,"install-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(staging,"bin"));
        try
        {
            using(var archive=ZipFile.OpenRead(archivePath))
            {
                ZipArchiveEntry ffmpeg=null,ffprobe=null;string prefix=null;
                foreach(var entry in archive.Entries)
                {
                    string[] segments=entry.FullName.Replace('\\','/').Split('/');
                    if(segments.Length!=3 || segments[0]=="." || segments[0]==".." || segments[1]!="bin")continue;
                    if(segments[2]=="ffmpeg.exe"){if(ffmpeg!=null)throw new IOException("FFmpeg 压缩包含有重复程序。");ffmpeg=entry;prefix=segments[0];}
                    if(segments[2]=="ffprobe.exe"){if(ffprobe!=null)throw new IOException("FFprobe 压缩包含有重复程序。");ffprobe=entry;}
                }
                if(ffmpeg==null || ffprobe==null || !ffprobe.FullName.Replace('\\','/').StartsWith(prefix+"/bin/",StringComparison.Ordinal) || ffmpeg.Length<=0 || ffprobe.Length<=0 || ffmpeg.Length>256L*1024*1024 || ffprobe.Length>256L*1024*1024)throw new IOException("FFmpeg 压缩包缺少配套的 ffmpeg.exe / ffprobe.exe。");
                ffmpeg.ExtractToFile(Path.Combine(staging,"bin","ffmpeg.exe"));ffprobe.ExtractToFile(Path.Combine(staging,"bin","ffprobe.exe"));
                foreach(string name in new[]{"LICENSE","LICENSE.txt","README.txt"})
                {var entry=archive.GetEntry(prefix+"/"+name);if(entry!=null && entry.Length<4*1024*1024)entry.ExtractToFile(Path.Combine(staging,name));}
            }
            validate(Path.Combine(staging,"bin","ffmpeg.exe"),Path.Combine(staging,"bin","ffprobe.exe"));
            File.WriteAllText(Path.Combine(staging,"SOURCE.txt"),"FFmpeg Windows builds by Gyan Doshi (GPLv3)\r\nhttps://www.gyan.dev/ffmpeg/builds/\r\nPackage source: "+PackageSource+"\r\nSHA-256: "+expectedHash+"\r\nSource code: https://github.com/FFmpeg/FFmpeg\r\n",Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(destination,"bin"));
            // Activation is the atomic receipt, so copying immutable files also works
            // on filesystems that cannot rename directories. Recover missing files after interruption.
            foreach(string name in new[]{"ffmpeg.exe","ffprobe.exe"})
            {
                string source=Path.Combine(staging,"bin",name),target=Path.Combine(destination,"bin",name);
                if(File.Exists(target) && HashFile(target)==HashFile(source))continue;
                string temporary=target+".tmp";File.Copy(source,temporary,true);
                if(File.Exists(target))File.Replace(temporary,target,null,true);else File.Move(temporary,target);
            }
            foreach(string name in new[]{"LICENSE","LICENSE.txt","README.txt","SOURCE.txt"})
            {string source=Path.Combine(staging,name),target=Path.Combine(destination,name);if(File.Exists(source) && !File.Exists(target))File.Copy(source,target);}
            // Reuse an interrupted installation only after validating both executables.
            validate(Path.Combine(destination,"bin","ffmpeg.exe"),Path.Combine(destination,"bin","ffprobe.exe"));
            CaptureContext.WriteMarker(Path.Combine(tools,"current.json"),new {sha256=expectedHash.ToLowerInvariant(),source=PackageSource,installedAt=DateTime.UtcNow.ToString("o")});
        }
        finally
        {
            // Antivirus or Windows executable validation may temporarily retain a handle.
            // Cleanup failure must not hide the real install error or invalidate a ready pair.
            try
            {
                string absolute=Path.GetFullPath(staging);
                if(Directory.Exists(absolute) && string.Equals(Path.GetDirectoryName(absolute),Path.GetFullPath(tools),StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(Path.GetFileName(absolute),@"\Ainstall-[0-9a-f]{32}\z"))Directory.Delete(absolute,true);
            }
            catch(IOException ex){Program.Log("DEPENDENCIES_TEMP_CLEANUP_DEFERRED "+ex.Message);}
            catch(UnauthorizedAccessException ex){Program.Log("DEPENDENCIES_TEMP_CLEANUP_DEFERRED "+ex.Message);}
        }
    }
    internal static int Test(string directory,bool online)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if(online){InstallOnline(directory,delegate(string value){Program.Log("DEPENDENCIES_TEST "+value);});File.WriteAllText(Path.Combine(directory,"dependency-test.txt"),"PASS: actual HTTPS download; provider SHA-256; archive extraction; ffmpeg/ffprobe execution; libx264/AAC/gdigrab/ddagrab capabilities; managed discovery",Encoding.UTF8);return 0;}
            string archivePath=Path.Combine(directory,"fixture.zip");
            using(var zip=ZipFile.Open(archivePath,ZipArchiveMode.Create))
            {foreach(string name in new[]{"build/bin/ffmpeg.exe","build/bin/ffprobe.exe","build/LICENSE","../../escape.exe"})using(var stream=new StreamWriter(zip.CreateEntry(name).Open()))stream.Write(name);}
            string hash=HashFile(archivePath);int validations=0;Action<string,string> validate=delegate(string ffmpeg,string ffprobe){if(File.ReadAllText(ffmpeg)!="build/bin/ffmpeg.exe" || File.ReadAllText(ffprobe)!="build/bin/ffprobe.exe")throw new Exception("Incorrect extracted executable");validations++;};
            var json=new JavaScriptSerializer();string url="https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
            var package=ResolvePackage(json.Serialize(new {assets=new[]{new {name="ffmpeg-9.0.2-essentials_build.zip",browser_download_url=url,digest="sha256:"+hash}}}));if(package.Key!=url || package.Value!=hash)throw new Exception("Release asset/hash selection failed");
            bool untrusted=false;try{ResolvePackage(json.Serialize(new {assets=new[]{new {name="ffmpeg-9.0.2-essentials_build.zip",browser_download_url="https://untrusted.invalid/payload.zip",digest="sha256:"+hash}}}));}catch(IOException){untrusted=true;}if(!untrusted)throw new Exception("Untrusted download origin accepted");
            bool rejected=false;try{InstallArchive(directory,archivePath,new string('0',64),validate);}catch(IOException){rejected=true;}
            if(!rejected || File.Exists(Path.Combine(directory,"Tools","ffmpeg","current.json")) || validations!=0)throw new Exception("Checksum failure ran or installed executables");
            InstallArchive(directory,archivePath,hash,validate);
            if(validations!=2 || ManagedPath(directory,"ffmpeg")==null || ManagedPath(directory,"ffprobe")==null || File.Exists(Path.Combine(directory,"escape.exe")))throw new Exception("Managed tools, validation or extraction isolation failed");
            string marker=Path.Combine(directory,"Tools","ffmpeg","current.json"),receipt=File.ReadAllText(marker);
            InstallArchive(directory,archivePath,hash,validate);if(validations!=4)throw new Exception("Repeated install did not reuse validated tools");
            File.WriteAllText(ManagedPath(directory,"ffprobe"),"interrupted copy");InstallArchive(directory,archivePath,hash,validate);if(validations!=6 || File.ReadAllText(ManagedPath(directory,"ffprobe"))!="build/bin/ffprobe.exe")throw new Exception("Interrupted executable copy did not recover");
            receipt=File.ReadAllText(marker);
            string invalid=Path.Combine(directory,"missing-probe.zip");using(var zip=ZipFile.Open(invalid,ZipArchiveMode.Create))using(var stream=new StreamWriter(zip.CreateEntry("build/bin/ffmpeg.exe").Open()))stream.Write("invalid");
            rejected=false;try{InstallArchive(directory,invalid,HashFile(invalid),validate);}catch(IOException){rejected=true;}if(!rejected || ManagedPath(directory,"ffprobe")==null)throw new Exception("Incomplete bundle replaced working tools");
            rejected=false;try{InstallArchive(directory,archivePath,hash,delegate(string a,string b){throw new IOException("Cannot run executable");});}catch(IOException){rejected=true;}if(!rejected || File.ReadAllText(marker)!=receipt)throw new Exception("Invalid executables accepted or replaced active receipt");
            CaptureContext.WriteMarker(marker,new {sha256="../../outside"});if(ManagedPath(directory,"ffmpeg")!=null)throw new Exception("Unsafe manifest path accepted");File.WriteAllText(marker,receipt);
            var previous=SettingsStore.Snapshot();
            try
            {
                var initial=previous.Copy();initial.FFmpegPath="missing-ffmpeg.exe";initial.FFprobePath="missing-ffprobe.exe";SettingsStore.Save(initial);
                var changed=initial.Copy();changed.FrameRate=120;changed.FFmpegPath="user-selected.exe";SettingsStore.Save(changed);
                SettingsStore.ApplyDependencyPaths("managed-ffmpeg.exe","managed-ffprobe.exe",initial);
                var actual=SettingsStore.Snapshot();if(actual.FrameRate!=120 || actual.FFmpegPath!="user-selected.exe" || actual.FFprobePath!="managed-ffprobe.exe")throw new Exception("Installer overwrote settings edited during download");
            }
            finally{SettingsStore.Save(previous);}
            File.WriteAllText(Path.Combine(directory,"dependency-test.txt"),"PASS: trusted release asset/digest; SHA-256 before execution; paired tools; safe extraction; runtime validation; atomic activation; repeated installation reuse; interrupted copy recovery; old tools retained on failure; unsafe manifest rejected; concurrent settings edits preserved",Encoding.UTF8);return 0;
        }
        catch(Exception ex){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 71;}
    }
    internal static int TestRealArchive(string directory,string archivePath,string checksum)
    {
        try{Directory.CreateDirectory(directory);InstallArchive(directory,archivePath,checksum,ValidatePackage);if(ManagedPath(directory,"ffmpeg")==null || ManagedPath(directory,"ffprobe")==null)throw new Exception("Managed discovery failed");File.WriteAllText(Path.Combine(directory,"dependency-test.txt"),"PASS: real provider archive; SHA-256; paired executable extraction; ffmpeg/ffprobe execution; libx264/AAC/gdigrab/ddagrab; managed discovery",Encoding.UTF8);return 0;}
        catch(Exception ex){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"failure.txt"),ex.ToString(),Encoding.UTF8);return 72;}
    }
}
