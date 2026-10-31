using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MirakurunDesktop;

static class Tests {
 static int checks;
 static void Assert(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
 static void Reject(Action action,string name){try{action();}catch(ArgumentException){checks++;Console.WriteLine("PASS "+name);return;}catch(InvalidDataException){checks++;Console.WriteLine("PASS "+name);return;}throw new Exception("Expected rejection: "+name);}
 [STAThread] static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"MirakurunDesktopTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   Reject(()=>SetupEngine.ServerUri("http://user:secret@localhost:40772/"),"credentials rejected");
   Reject(()=>SetupEngine.ServerUri("http://localhost:40772/other?key=value"),"subpath/query rejected");
   Assert(SetupEngine.ServerUri("http://127.0.0.1:40772/").Port==40772,"valid server");
   string bad=Path.Combine(root,"bad.zip");using(var file=File.Create(bad))using(var zip=new ZipArchive(file,ZipArchiveMode.Create)){var entry=zip.CreateEntry("../escape.txt");using(var writer=new StreamWriter(entry.Open()))writer.Write("escape");}
   Reject(()=>SetupEngine.Extract(bad,"",Path.Combine(root,"extract")),"zip traversal rejected");Assert(!File.Exists(Path.Combine(root,"escape.txt")),"outside destination preserved");
   string ini=Path.Combine(root,"driver.ini");SetupEngine.WriteBonIni(ini,new Uri("http://127.0.0.1:40772/"),0);
   Assert(File.ReadAllBytes(ini).Take(8).All(b=>b!=0)&&File.ReadAllBytes(ini)[0]==(byte)'[',"BonDriver INI is ASCII without BOM");
   Assert(File.ReadAllText(ini).Contains("PRIORITY=0"),"scan priority accepted by Mirakurun 4.x");
   string stage=Path.Combine(root,"stage"),final=Path.Combine(root,"完成した環境");Directory.CreateDirectory(Path.Combine(stage,"EDCB"));Directory.CreateDirectory(Path.Combine(stage,"TVTest"));File.WriteAllText(Path.Combine(stage,"EDCB","EpgTimerPlugIn.tvtp"),"fixture");Directory.CreateDirectory(Path.Combine(stage,"EDCB","Setting"));
   var channel=new ChannelResult {channel=new Channel {name="局,Ａ",type="GR",services=new List<Service> {new Service {name="局,Ａ\t日本語",networkId=123,serviceId=456,type=1}}},probe=new ProbeChannel {space=0,index=2},capture=new Capture {onid=123,tsid=789}};
   SetupEngine.WriteConfiguration(stage,final,Path.Combine(final,"録画"),new List<ChannelResult>{channel},2);
   string tv=File.ReadAllText(Path.Combine(stage,"TVTest","BonDriver_Mirakurun.ch2"));Assert(tv.Contains("\"局,Ａ 日本語\",0,2,0,1,456,123,789,1"),"CSV quoting and actual TSID");
   string ch4=File.ReadAllText(Path.Combine(stage,"EDCB","Setting","BonDriver_Mirakurun().ChSet4.txt"));Assert(ch4.Trim().Split('\t').Length==12,"EDCB ChSet4 field count");
   Assert(File.ReadAllBytes(Path.Combine(stage,"EDCB","Setting","ChSet5.txt")).Take(3).SequenceEqual(new byte[]{239,187,191}),"EDCB channel files use UTF-8 BOM");
   Assert(File.ReadAllText(Path.Combine(stage,"EDCB","Common.ini")).Contains(final),"settings reference committed destination, not staging");
   Assert(File.ReadAllText(Path.Combine(stage,"EDCB","EpgTimerSrv.ini")).Contains("RecEndMode=0"),"no automatic PC shutdown after recording");
   Assert(File.ReadAllText(Path.Combine(stage,"TVTest","TVTest.ini")).Contains("Plugin0_Enable=1"),"EPGTimer plugin enabled");
   Console.WriteLine("ALL PASS: "+checks+" checks; fixtures retained at "+root);return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
 }
}
