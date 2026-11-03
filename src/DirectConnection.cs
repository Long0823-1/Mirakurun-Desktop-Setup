using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MirakurunDesktop {
 static class DirectConnection {
  sealed class Adapter {public int Index;public string Name;public override string ToString()=>Name+"（番号 "+Index+"）";}
  public static async Task<string> Configure(string action,int index,string mac){
   string stage=Path.Combine(Path.GetTempPath(),"MirakurunDirect-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
   string script=Path.Combine(stage,"DirectConnection.ps1"),result=Path.Combine(stage,"result.txt");
   using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("DirectConnection.ps1"))using(var output=File.Create(script))input.CopyTo(output);
   string executable=Assembly.GetExecutingAssembly().Location;
   string arguments="-NoProfile -ExecutionPolicy Bypass -File "+Quote(script)+" -Action "+action+" -InterfaceIndex "+index+" -TvMac "+Quote(mac??"")+" -SourceExecutable "+Quote(executable)+" -ResultFile "+Quote(result);
   using(var process=Process.Start(new ProcessStartInfo("powershell.exe",arguments){Verb="runas",UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})){
    await Task.Run(()=>process.WaitForExit());string text=File.Exists(result)?File.ReadAllText(result):"管理者による処理の結果を取得できませんでした。";
    if(process.ExitCode!=0)throw new IOException(text);
    try{Directory.Delete(stage,true);}catch(IOException){}catch(UnauthorizedAccessException){}return text;
   }
  }
  static string Quote(string value){if(value.IndexOf('"')>=0||value.IndexOf('\r')>=0||value.IndexOf('\n')>=0)throw new ArgumentException("Invalid argument");return "\""+value+"\"";}
  public static void Show(Form owner,Action<string> log,Action<string> connected,string currentServer){
   using(var dialog=new Form {Text="WindowsとTVの直結設定",Width=690,Height=410,StartPosition=FormStartPosition.CenterParent,Font=owner.Font,AutoScaleMode=AutoScaleMode.Dpi}){
    var body=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=6};dialog.Controls.Add(body);
    body.Controls.Add(new Label {AutoSize=true,Text="TVはIP自動取得のまま、Windowsが同じIPを配布します。\nTV専用の有線ポートを選択してください。Wi-Fi・ルーター接続には使いません。"});
    var adapters=new ComboBox {Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};
    using(var query=new ManagementObjectSearcher("SELECT InterfaceIndex,NetConnectionID,Name FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True AND NetConnectionStatus=2 AND AdapterTypeID=0"))
     foreach(ManagementObject item in query.Get())adapters.Items.Add(new Adapter {Index=Convert.ToInt32(item["InterfaceIndex"]),Name=(string)(item["NetConnectionID"]??item["Name"])});
    if(adapters.Items.Count>0)adapters.SelectedIndex=0;body.Controls.Add(adapters);
    var mac=new TextBox {Dock=DockStyle.Top};mac.PlaceholderTextCompat("TVの有線MAC（例：00:11:22:33:44:55）");body.Controls.Add(mac);
    try {
     string host=new Uri(currentServer).DnsSafeHost;
     var ip=Dns.GetHostAddresses(host).FirstOrDefault(a=>a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&MirakurunDiscovery.IsPrivate(a.ToString()));
     if(ip!=null)using(var neighbors=new ManagementObjectSearcher("root\\StandardCimv2","SELECT LinkLayerAddress,InterfaceIndex FROM MSFT_NetNeighbor WHERE IPAddress='"+ip+"'"))foreach(ManagementObject item in neighbors.Get()){
      var match=adapters.Items.Cast<Adapter>().FirstOrDefault(a=>a.Index==Convert.ToInt32(item["InterfaceIndex"]));
      if(match!=null){string value=(string)item["LinkLayerAddress"];DirectDhcpConfig.Mac(value);mac.Text=value;adapters.SelectedItem=match;break;}
     }
    }catch(Exception e)when(e is ManagementException||e is SocketException||e is UriFormatException||e is ArgumentException){}
    var status=new Label {AutoSize=true,Text="PC側の専用有線だけに固定IPを設定します。DHCPサービスはWindows起動時に自動起動します。\nゲートウェイ・DNSは配布しません。インターネット共有は行いません。\n登録したTVのMAC以外には応答しません。適用・削除には管理者確認が必要です。"};body.Controls.Add(status);
    var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};body.Controls.Add(actions);
    Button install=new Button {Text="直結IPを固定する",AutoSize=true},remove=new Button {Text="解除して自動取得に戻す",AutoSize=true};actions.Controls.Add(install);actions.Controls.Add(remove);
    string config=Path.Combine(DirectDhcpService.DataDirectory,"config.json");
    if(File.Exists(config))try{var c=new JavaScriptSerializer().Deserialize<DirectDhcpConfig>(File.ReadAllText(config));mac.Text=c.TvMac;status.Text="登録済み：PC "+c.Server+" / TV "+c.Address+"\nWindowsサービスが配布を継続します。TV側の設定は自動取得です。";install.Enabled=false;}catch(Exception e){log(e.Message);}
    remove.Enabled=File.Exists(config);
    bool running=false;
    Func<string,Task> apply=async action=>{
     if(running)return;
     var adapter=adapters.SelectedItem as Adapter;
     if(action=="Install")try{if(adapter==null)throw new ArgumentException("接続中の物理有線ポートを選択してください。");DirectDhcpConfig.Mac(mac.Text);}catch(Exception e){MessageBox.Show(dialog,e.Message);return;}
     running=true;install.Enabled=remove.Enabled=false;status.Text="設定中です。Windowsの管理者確認を許可してください…";
     try{string message=await Configure(action,adapter?.Index??0,mac.Text);log(message);status.Text=message;
      if(action=="Install"){var c=new JavaScriptSerializer().Deserialize<DirectDhcpConfig>(File.ReadAllText(config));connected("http://"+c.Address+":40772/");log("TVの有線を一度抜き差しすると、新しいDHCP設定を取得します。");}
     }catch(Exception e){log("直結設定："+e.Message);status.Text=e.Message;}
     finally{running=false;install.Enabled=!File.Exists(config);remove.Enabled=File.Exists(config);}
    };
    install.Click+=async(s,e)=>await apply("Install");remove.Click+=async(s,e)=>await apply("Remove");dialog.FormClosing+=(s,e)=>{if(running)e.Cancel=true;};dialog.ShowDialog(owner);
   }
  }
 }
}
