using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace MirakurunDesktop {
 public sealed class Package { public string name, url, sha256, prefix; public long maxBytes; }
 public sealed class Catalog { public List<Package> packages; public string sourceCommit; }
 public sealed class Service { public long id; public int serviceId,networkId,type; public string name; }
 public sealed class Channel { public string name,type,channel; public List<Service> services; }
 public sealed class ProbeChannel { public int space,index; public string type,name; }
 public sealed class Capture { public long bytes,packets; public int tsid,onid; }
 public sealed class ChannelResult { public Channel channel; public ProbeChannel probe; public Capture capture; public string error; }
 public sealed class Options {
  public string Server,Destination,RecordDirectory,CacheDirectory;
  public int TunerCount=2,CaptureSeconds=15;
  public bool Startup=true,Shortcuts=true,InstallRuntime=true;
 }
 public sealed class SetupResult { public string destination,server; public int channels,services,tuners; public List<string> warnings=new List<string>(); public List<string> shortcuts=new List<string>(); }

 public sealed class SetupEngine {
  public readonly Catalog Catalog;
  public readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=16*1024*1024, RecursionLimit=64 };
  readonly Action<string> log;
  public SetupEngine(Action<string> log) {
   this.log=log??(_=>{});
   using(var reader=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("catalog.json"))) Catalog=Json.Deserialize<Catalog>(reader.ReadToEnd());
  }
  public static Uri ServerUri(string text) {
   Uri u;
   if(!Uri.TryCreate(text,UriKind.Absolute,out u)||u.Scheme!="http"||u.UserInfo.Length!=0||u.Query.Length!=0||u.Fragment.Length!=0||u.AbsolutePath!="/"||u.HostNameType==UriHostNameType.IPv6)
    throw new ArgumentException("接続先は http://TVのIP:40772/ の形式で入力してください（認証情報・サブパスは非対応）。");
   if(u.Host.Any(c=>c>127||!(char.IsLetterOrDigit(c)||c=='.'||c=='-')))throw new ArgumentException("接続先のホスト名が不正です。IPアドレスまたはASCIIのホスト名を指定してください。");
   return u;
  }
  static HttpClient Client(int seconds=20,bool redirect=false) {
   ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
   var c=new HttpClient(new HttpClientHandler { AllowAutoRedirect=redirect }) { Timeout=TimeSpan.FromSeconds(seconds) };
   c.DefaultRequestHeaders.UserAgent.ParseAdd("Mirakurun-Desktop-Setup/0.1.1");return c;
  }
  public async Task<Tuple<List<Channel>,int>> InspectAsync(string server,CancellationToken token) {
   var uri=ServerUri(server);
   using(var client=Client()) {
    var status=await client.GetAsync(new Uri(uri,"api/status"),token);status.EnsureSuccessStatusCode();
    var text=await client.GetStringAsync(new Uri(uri,"api/channels"));
    var channels=Json.Deserialize<List<Channel>>(text);
    var tuners=Json.Deserialize<List<Dictionary<string,object>>>(await client.GetStringAsync(new Uri(uri,"api/tuners")));
    int count=tuners.Count(t=>!t.ContainsKey("isAvailable")||Convert.ToBoolean(t["isAvailable"]));
    if(count==0)throw new InvalidOperationException("利用できるチューナーがありません。");
    if(channels.Count==0||channels.All(c=>c.services==null||c.services.Count==0))throw new InvalidOperationException("Mirakurunの局・サービス一覧が空です。先にTV側の局設定とサービス検出を完了してください。");
    if(channels.Count>512)throw new InvalidOperationException("局数が対応上限512を超えています。");
    return Tuple.Create(channels,count);
   }
  }
  public static string Hash(string file) { using(var sha=SHA256.Create())using(var f=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
  public async Task<string> DownloadAsync(Package p,string cache,CancellationToken token) {
   Directory.CreateDirectory(cache);string file=Path.Combine(cache,p.name+"-"+p.sha256.Substring(0,12)+Path.GetExtension(new Uri(p.url).AbsolutePath));
   if(File.Exists(file)&&Hash(file)==p.sha256){log(p.name+"：検証済みキャッシュを使用");return file;}
   if(new Uri(p.url).Scheme!="https")throw new InvalidDataException("配布元がHTTPSではありません。");
   string part=file+"."+Guid.NewGuid().ToString("N")+".part";
   try {
    log(p.name+" を配布元からダウンロード中…");
    using(var c=Client(600,true))using(var res=await c.GetAsync(p.url,HttpCompletionOption.ResponseHeadersRead,token)) {
     res.EnsureSuccessStatusCode();if(res.Content.Headers.ContentLength>p.maxBytes)throw new InvalidDataException("配布物のサイズが上限を超えています。");
     using(var input=await res.Content.ReadAsStreamAsync())using(var output=File.Create(part)) {
      byte[] b=new byte[65536];long total=0;int n;
      while((n=await input.ReadAsync(b,0,b.Length,token))>0){total+=n;if(total>p.maxBytes)throw new InvalidDataException("ダウンロードサイズが上限を超えました。");await output.WriteAsync(b,0,n,token);}
     }
    }
    if(Hash(part)!=p.sha256)throw new InvalidDataException(p.name+"：SHA-256が一致しません。構築を中止します。");
    if(File.Exists(file))File.Delete(file);File.Move(part,file);log(p.name+"：SHA-256確認済み");return file;
   } finally { if(File.Exists(part))File.Delete(part); }
  }
  public static void Extract(string archive,string prefix,string destination) {
   string root=Path.GetFullPath(destination)+Path.DirectorySeparatorChar;Directory.CreateDirectory(destination);long expanded=0;
   using(var stream=File.OpenRead(archive))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read,false,Encoding.GetEncoding(932))) {
    foreach(var entry in zip.Entries) {
     string name=entry.FullName.Replace('\\','/');
     if(name.StartsWith("/")||name.Split('/').Any(x=>x=="..")||name.Contains(":"))throw new InvalidDataException("ZIP内に不正なパスがあります。");
     if((entry.ExternalAttributes>>16&0xf000)==0xa000)throw new InvalidDataException("ZIPのシンボリックリンクは展開しません。");
     if(!name.StartsWith(prefix,StringComparison.Ordinal))continue;
     string relative=name.Substring(prefix.Length);if(relative.Length==0)continue;
     string target=Path.GetFullPath(Path.Combine(destination,relative.Replace('/',Path.DirectorySeparatorChar)));
     if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("ZIPの展開先が範囲外です。");
     expanded+=entry.Length;if(expanded>1024L*1024*1024)throw new InvalidDataException("ZIP展開サイズが上限を超えました。");
     if(name.EndsWith("/")){Directory.CreateDirectory(target);continue;}
     Directory.CreateDirectory(Path.GetDirectoryName(target));using(var input=entry.Open())using(var output=File.Create(target))input.CopyTo(output);
    }
   }
  }
  public static string Quote(string value) {
   if(value.IndexOf('\0')>=0)throw new ArgumentException("不正な文字列");
   var b=new StringBuilder("\"");int slash=0;
   foreach(char ch in value){if(ch=='\\'){slash++;continue;}if(ch=='"')b.Append('\\',slash*2+1);else b.Append('\\',slash);slash=0;b.Append(ch);}
   b.Append('\\',slash*2);return b.Append('"').ToString();
  }
  public static async Task<int> RunAsync(string executable,IEnumerable<string> args,int timeout,CancellationToken token) {
   using(var p=new Process { StartInfo=new ProcessStartInfo(executable,string.Join(" ",args.Select(Quote))) { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(executable) } }) {
    p.Start();var watch=Stopwatch.StartNew();
    try { while(!p.HasExited){token.ThrowIfCancellationRequested();if(watch.Elapsed.TotalSeconds>timeout)throw new TimeoutException(Path.GetFileName(executable)+"の処理がタイムアウトしました。");await Task.Delay(100,token);}return p.ExitCode; }
    finally { if(!p.HasExited){p.Kill();p.WaitForExit(5000);} }
   }
  }
  void WriteJson(string path,object value) { File.WriteAllText(path,Json.Serialize(value),new UTF8Encoding(false)); }
  static void Write(string path,string text) { Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,text.Replace("\r\n","\n").Replace("\n","\r\n"),Encoding.Unicode); }
  static string Clean(string text) { return (text??"").Replace("\t"," ").Replace("\r"," ").Replace("\n"," "); }
  static string Csv(string text) { return "\""+Clean(text).Replace("\"","\"\"")+"\""; }
  public static void WriteBonIni(string file,Uri server,int priority) {
   // v2.0 reads the INI as ASCII/UTF-8 bytes, not Win32 UTF-16 profile text.
   File.WriteAllText(file,"[GLOBAL]\r\nSERVER_HOST="+server.Host+"\r\nSERVER_PORT="+server.Port+"\r\nDECODE_B25=1\r\nSERVICE_SPLIT=0\r\nPRIORITY="+priority+"\r\n",Encoding.ASCII);
  }
  static bool Video(Service s) { return s.type==1||s.type==0xA5||s.type==0xAD; }
  async Task EnsureRuntimeAsync(Options options,CancellationToken token) {
   if(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"vcruntime140.dll"))&&File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msvcp140.dll")))return;
   if(!options.InstallRuntime)throw new InvalidOperationException("Microsoft Visual C++ x64ランタイムが必要です。「必要なランタイムを導入」を有効にしてください。");
   string installer=await DownloadAsync(Catalog.packages.Single(p=>p.name=="vcruntime"),options.CacheDirectory,token);
   log("Microsoft Visual C++ランタイムを導入します。表示されたUACを確認してください。");
   using(var process=Process.Start(new ProcessStartInfo(installer,"/install /passive /norestart") { UseShellExecute=true,Verb="runas" })) {
    await Task.Run(()=>process.WaitForExit());if(process.ExitCode!=0&&process.ExitCode!=3010)throw new InvalidOperationException("ランタイムの導入に失敗しました: "+process.ExitCode);
   }
  }
  public async Task<SetupResult> InstallAsync(Options options,CancellationToken token) {
   if(!Environment.Is64BitOperatingSystem)throw new InvalidOperationException("Windows x64環境が必要です。");
   Uri server=ServerUri(options.Server);string final=Path.GetFullPath(options.Destination).TrimEnd(Path.DirectorySeparatorChar);
   if(final==Path.GetPathRoot(final).TrimEnd('\\')||final.Length>150)throw new ArgumentException("構築先はドライブ直下以外の、短い専用フォルダーを指定してください。");
   if(Directory.Exists(final)&&Directory.EnumerateFileSystemEntries(final).Any())throw new InvalidOperationException("構築先が空ではありません。既存の予約・設定を保護するため、空の専用フォルダーを指定してください。");
   if(string.IsNullOrWhiteSpace(options.CacheDirectory))options.CacheDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirakurunDesktopSetup","Cache");
   string recording=Path.GetFullPath(string.IsNullOrWhiteSpace(options.RecordDirectory)?Path.Combine(final,"Recordings"):options.RecordDirectory);
   if(options.CaptureSeconds<3||options.CaptureSeconds>120)throw new ArgumentException("EPG取得時間は3〜120秒です。");
   var info=await InspectAsync(server.AbsoluteUri,token);int tunerCount=Math.Max(1,Math.Min(options.TunerCount,info.Item2));
   await EnsureRuntimeAsync(options,token);
   var files=new Dictionary<string,string>();foreach(var p in Catalog.packages.Where(p=>p.name!="vcruntime"))files[p.name]=await DownloadAsync(p,options.CacheDirectory,token);
   Directory.CreateDirectory(Path.GetDirectoryName(final));string stage=final+".building-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(stage);
   var result=new SetupResult {destination=final,server=server.AbsoluteUri,tuners=tunerCount};
   try {
    foreach(string name in new[]{"tvtest","edcb","bondriver"}) {
     string target=Path.Combine(stage,name=="tvtest"?"TVTest":name=="edcb"?"EDCB":"DriverSource");
     log(name+"（x64）を展開中…");Extract(files[name],Catalog.packages.Single(p=>p.name==name).prefix,target);
    }
    string tv=Path.Combine(stage,"TVTest"),edcb=Path.Combine(stage,"EDCB"),setting=Path.Combine(edcb,"Setting"),driver=Path.Combine(edcb,"BonDriver","BonDriver_Mirakurun.dll");
    Directory.CreateDirectory(setting);Directory.CreateDirectory(Path.GetDirectoryName(driver));
    string source=Path.Combine(stage,"DriverSource","BonDriver_Mirakurun.dll");
    foreach(var exe in new[]{Path.Combine(tv,"TVTest.exe"),Path.Combine(edcb,"EpgDataCap_Bon.exe"),source})CheckX64(exe);
    File.Copy(source,driver);File.Copy(source,Path.Combine(tv,"BonDriver_Mirakurun.dll"));
    // Public stream requests require a non-negative priority on Mirakurun 4.x.
    WriteBonIni(Path.ChangeExtension(driver,".ini"),server,0);WriteBonIni(Path.Combine(tv,"BonDriver_Mirakurun.ini"),server,0);
    string helper=Path.Combine(stage,"BonProbe.exe");using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("BonProbe.exe"))using(var output=File.Create(helper))input.CopyTo(output);
    string work=Path.Combine(stage,"Probe");Directory.CreateDirectory(work);string mapFile=Path.Combine(work,"channels.json");
    int code=await RunAsync(helper,new[]{"enumerate",driver,mapFile},30,token);
    if(code!=0)throw new InvalidOperationException("BonDriverの読み込み・局列挙に失敗しました（コード"+code+"）。");
    var mapping=Json.Deserialize<List<ProbeChannel>>(File.ReadAllText(mapFile,Encoding.UTF8));
    if(mapping.Count!=info.Item1.Count||mapping.Where((m,i)=>m.name!=info.Item1[i].name).Any())throw new InvalidOperationException("BonDriverの局順とMirakurunの局一覧が一致しません。局更新中でないことを確認してください。");
    var results=new ChannelResult[mapping.Count];var gate=new SemaphoreSlim(Math.Min(2,tunerCount));
    await Task.WhenAll(mapping.Select(async (m,i)=> {
     await gate.WaitAsync(token);
     try {
      var item=results[i]=new ChannelResult {channel=info.Item1[i],probe=m};
      log("局・番組表を取得中："+m.name);
      string epg=Path.Combine(work,i+".dat"),report=Path.Combine(work,i+".json");
      int exit=await RunAsync(helper,new[]{"capture",driver,m.space.ToString(),m.index.ToString(),options.CaptureSeconds.ToString(),epg,report,"-"},options.CaptureSeconds+35,token);
      if(exit==0) {
       item.capture=Json.Deserialize<Capture>(File.ReadAllText(report));
       if(item.capture.onid==0||!item.channel.services.Any(s=>s.networkId==item.capture.onid)){item.error="受信TSと局情報のネットワークIDが一致しません";item.capture=null;}
      } else item.error="TS受信失敗（コード"+exit+"）";
     } finally {gate.Release();}
    }));
    // Retry once sequentially after all parallel captures release their tuners.
    foreach(var item in results.Where(r=>r.capture==null)) {
     token.ThrowIfCancellationRequested();
     int i=mapping.IndexOf(item.probe);string report=Path.Combine(work,i+".json");
     log("受信を再試行："+item.channel.name);
     int exit=await RunAsync(helper,new[]{"capture",driver,item.probe.space.ToString(),item.probe.index.ToString(),options.CaptureSeconds.ToString(),Path.Combine(work,i+".dat"),report,"-"},options.CaptureSeconds+35,token);
     if(exit==0) {
      var capture=Json.Deserialize<Capture>(File.ReadAllText(report));
      if(capture.onid!=0&&item.channel.services.Any(s=>s.networkId==capture.onid)){item.capture=capture;item.error=null;}
     }
    }
    var valid=results.Where(r=>r.capture!=null).ToList();
    if(valid.Count==0)throw new InvalidOperationException("どの局からも有効なTSを受信できませんでした。Mirakurun・TV受信状態・他クライアントの使用状況を確認してください。");
    foreach(var failed in results.Where(r=>r.capture==null)){string warning=failed.channel.name+"："+failed.error;result.warnings.Add(warning);log("注意："+warning);}
    WriteBonIni(Path.ChangeExtension(driver,".ini"),server,1);
    WriteConfiguration(stage,final,recording,valid,tunerCount);
    string epgFolder=Path.Combine(setting,"EpgData");Directory.CreateDirectory(epgFolder);
    foreach(var r in valid)File.Copy(Path.Combine(work,mapping.IndexOf(r.probe)+".dat"),Path.Combine(epgFolder,r.capture.onid.ToString("X4")+r.capture.tsid.ToString("X4")+"_epg.dat"),true);
    result.channels=valid.Count;result.services=valid.Sum(r=>r.channel.services.Count(Video));
    WriteJson(Path.Combine(stage,"setup-result.json"),result);
    WriteJson(Path.Combine(stage,"package-lock.json"),Catalog);
    WriteJson(Path.Combine(stage,"channel-map.json"),valid);
    File.WriteAllText(Path.Combine(stage,"README-セットアップ結果.txt"),"Mirakurun Desktop Setup\r\n接続先: "+server+"\r\n受信確認済み: "+result.channels+"多重 / "+result.services+"映像サービス\r\n録画にはWindowsの起動・EpgTimerSrvの常駐・TVとの接続が必要です。\r\nMirakurunのEPGをJSONコピーしたものではなく、受信TSから初回EPGを取得しています。\r\n未受信局:\r\n"+string.Join("\r\n",result.warnings),Encoding.UTF8);
    token.ThrowIfCancellationRequested();
    if(Directory.Exists(final))Directory.Delete(final,false);Directory.Move(stage,final);
    Directory.CreateDirectory(recording);
    MakeLink(Path.Combine(final,"TVTest.lnk"),Path.Combine(final,"TVTest","TVTest.exe"),"/d BonDriver_Mirakurun.dll");
    MakeLink(Path.Combine(final,"EPGTimer.lnk"),Path.Combine(final,"DesktopLauncher.exe"),"--epgtimer --destination "+Quote(final));
    if(options.Shortcuts)try { result.shortcuts=CreateShortcuts(final,options.Startup); }catch(Exception e){result.warnings.Add("ショートカット作成："+e.Message);}
    WriteJson(Path.Combine(final,"setup-result.json"),result);
    log("構築完了："+result.channels+"多重 / "+result.services+"サービス / "+tunerCount+"チューナー");
    return result;
   }catch {
    // Keep failed staging for diagnosis. Do not recursively delete arbitrary files.
    log("未完了の作業フォルダー："+stage);throw;
   }
  }
  public static void CheckX64(string file) {
   using(var f=File.OpenRead(file))using(var reader=new BinaryReader(f)){if(reader.ReadUInt16()!=0x5a4d)throw new InvalidDataException("実行ファイルではありません："+file);f.Position=0x3c;int offset=reader.ReadInt32();f.Position=offset;if(reader.ReadUInt32()!=0x4550||reader.ReadUInt16()!=0x8664)throw new InvalidDataException("x64の実行ファイルではありません："+file);}
  }
  public static void WriteConfiguration(string stage,string final,string recording,List<ChannelResult> channels,int tuners) {
   string tv=Path.Combine(stage,"TVTest"),edcb=Path.Combine(stage,"EDCB"),setting=Path.Combine(edcb,"Setting"),finalEdcb=Path.Combine(final,"EDCB"),finalTv=Path.Combine(final,"TVTest","TVTest.exe");
   var ch2=new StringBuilder("; TVTest チャンネル設定ファイル\n; 名称,空間,チャンネル,リモコン番号,タイプ,SID,ONID,TSID,有効\n");
   var ch4=new StringBuilder();var ch5=new StringBuilder();
   foreach(var group in channels.GroupBy(c=>c.probe.space)) {
    ch2.AppendLine(";#SPACE("+group.Key+","+group.First().channel.type+")");
    foreach(var r in group)foreach(var s in r.channel.services.Where(Video)) {
     ch2.AppendLine(string.Join(",",Csv(s.name),r.probe.space,r.probe.index,0,s.type,s.serviceId,s.networkId,r.capture.tsid,1));
     ch4.AppendLine(string.Join("\t",Clean(r.channel.name),Clean(s.name),r.channel.type,r.probe.space,r.probe.index,s.networkId,r.capture.tsid,s.serviceId,s.type,0,1,0));
     ch5.AppendLine(string.Join("\t",Clean(s.name),r.channel.type,s.networkId,r.capture.tsid,s.serviceId,s.type,0,1,1));
    }
   }
   Write(Path.Combine(tv,"BonDriver_Mirakurun.ch2"),ch2.ToString());
   // EDCB's native text parser accepts UTF-8 BOM / ACP, but not UTF-16.
   File.WriteAllText(Path.Combine(setting,"BonDriver_Mirakurun().ChSet4.txt"),ch4.ToString(),new UTF8Encoding(true));
   File.WriteAllText(Path.Combine(setting,"ChSet5.txt"),ch5.ToString(),new UTF8Encoding(true));
   Write(Path.Combine(edcb,"Common.ini"),"[SET]\nDataSavePath="+Path.Combine(finalEdcb,"Setting")+"\nRecExePath="+Path.Combine(finalEdcb,"EpgDataCap_Bon.exe")+"\nRecFolderNum=1\nRecFolderPath0="+recording+"\n");
   Write(Path.Combine(edcb,"EpgTimerSrv.ini"),"[SET]\nRecEndMode=0\nReboot=0\nEnableTCPSrv=0\nEnableHttpSrv=0\nResidentMode=2\n[BonDriver_Mirakurun.dll]\nCount="+tuners+"\nGetEpg=1\nEPGCount=1\nPriority=0\n[TVTEST]\nNum=1\n0=BonDriver_Mirakurun.dll\n[EPG_CAP]\nCount=1\n0=5:00\n0Select=1\n");
   Write(Path.Combine(edcb,"EpgDataCap_Bon.ini"),"[SET]\nScramble=0\nEpgCapLive=1\nEpgCapRec=1\nEpgCapBackStartWaitSec=15\n");
   Directory.CreateDirectory(Path.Combine(tv,"Plugins"));File.Copy(Path.Combine(edcb,"EpgTimerPlugIn.tvtp"),Path.Combine(tv,"Plugins","EpgTimerPlugIn.tvtp"),true);
   Write(Path.Combine(tv,"TVTest.ini"),"[Settings]\nDefaultDriverType=2\nDefaultDriver=BonDriver_Mirakurun.dll\nDriver=BonDriver_Mirakurun.dll\nMpeg2Decoder=TVTest DTV Video Decoder\nRenderer=EVR\n[PluginList]\nPluginCount=1\nPlugin0_Name=EpgTimerPlugIn.tvtp\nPlugin0_Enable=1\n");
   var xml=new XDocument(new XElement("Settings",new XElement("TvTestExe",finalTv),new XElement("TvTestCmd","/d BonDriver_Mirakurun.dll"),new XElement("NwTvMode",false),new XElement("FilePlay",false)));
   xml.Save(Path.Combine(edcb,"EpgTimer.exe.xml"));
   string self=Assembly.GetExecutingAssembly().Location;
   File.Copy(self,Path.Combine(stage,"DesktopLauncher.exe"),true);
   if(File.Exists(self+".config"))File.Copy(self+".config",Path.Combine(stage,"DesktopLauncher.exe.config"),true);
  }
  public static void MakeLink(string file,string target,string args) {
   dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(file);
   link.TargetPath=target;link.Arguments=args;link.WorkingDirectory=Path.GetDirectoryName(target);link.Save();
   System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
  }
  static List<string> CreateShortcuts(string final,bool startup) {
   string id=BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(final.ToLowerInvariant()))).Replace("-","").Substring(0,8);
   string menu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Mirakurun Desktop",id);Directory.CreateDirectory(menu);var paths=new List<string>();
   foreach(string app in new[]{"TVTest","EPGTimer"}){string path=Path.Combine(menu,app+".lnk");File.Copy(Path.Combine(final,app+".lnk"),path,true);paths.Add(path);}
   if(startup){string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"Mirakurun EPGTimerSrv "+id+".lnk");MakeLink(path,Path.Combine(final,"EDCB","EpgTimerSrv.exe"),"");paths.Add(path);}
   return paths;
  }
 }
}
