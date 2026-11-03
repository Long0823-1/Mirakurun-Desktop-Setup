using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using System.Web.Script.Serialization;

namespace MirakurunDesktop {
 public sealed class DirectDhcpConfig {
  public string InterfaceId {get;set;}
  public string AdapterMac {get;set;}
  public string TvMac {get;set;}
  public string Server {get;set;}
  public string Address {get;set;}
  public string Mask {get;set;}
  public static byte[] Mac(string text) {
   string value=(text??"").Replace(":","").Replace("-","");
   if(value.Length!=12||value.Any(c=>!Uri.IsHexDigit(c)))throw new ArgumentException("MACアドレスは6バイトで指定してください。");
   byte[] bytes=Enumerable.Range(0,6).Select(i=>Convert.ToByte(value.Substring(i*2,2),16)).ToArray();
   if((bytes[0]&1)!=0||bytes.All(b=>b==0))throw new ArgumentException("ユニキャストMACアドレスが必要です。");return bytes;
  }
  public void Validate() {
   Guid guid;if(!Guid.TryParse(InterfaceId,out guid))throw new ArgumentException("Invalid adapter ID");Mac(AdapterMac);Mac(TvMac);
   // The dedicated link has exactly two hosts. No router or DNS is advertised.
   var a=IPAddress.Parse(Server).GetAddressBytes();var b=IPAddress.Parse(Address).GetAddressBytes();
   if(a.Length!=4||b.Length!=4||Mask!="255.255.255.252"||!MirakurunDiscovery.IsPrivate(Server)||Server.StartsWith("169.254.")||
      a[0]!=b[0]||a[1]!=b[1]||a[2]!=b[2]||a[3]%4!=1||b[3]!=a[3]+1)throw new ArgumentException("Dedicated /30 subnet required");
  }
 }
 public sealed class DhcpReply {public byte[] Bytes;public string Destination;public byte MessageType;}
 public sealed class DirectDhcpProtocol {
  readonly DirectDhcpConfig config;readonly byte[] mac,server,address;bool declined;
  public DirectDhcpProtocol(DirectDhcpConfig c){c.Validate();config=c;mac=DirectDhcpConfig.Mac(c.TvMac);server=IPAddress.Parse(c.Server).GetAddressBytes();address=IPAddress.Parse(c.Address).GetAddressBytes();}
  static bool Zero(byte[] p,int offset,int n){for(int i=0;i<n;i++)if(p[offset+i]!=0)return false;return true;}
  public DhcpReply Process(byte[] p,int length,int incomingInterface,int allowedInterface,int sourcePort) {
   if(incomingInterface!=allowedInterface||sourcePort!=68||length<240||length>1500||length>p.Length||p[0]!=1||p[1]!=1||p[2]!=6||p[3]!=0||
      !Zero(p,24,4)||!p.Skip(28).Take(6).SequenceEqual(mac)||p[236]!=99||p[237]!=130||p[238]!=83||p[239]!=99)return null;
   var options=new Dictionary<byte,byte[]>();bool ended=false;
   for(int i=240;i<length;){byte key=p[i++];if(key==0)continue;if(key==255){ended=true;break;}if(i==length)return null;int n=p[i++];if(n>length-i||options.ContainsKey(key))return null;options[key]=p.Skip(i).Take(n).ToArray();i+=n;}
   byte[] type;if(!ended||options.ContainsKey(52)||!options.TryGetValue(53,out type)||type.Length!=1)return null;
   byte[] selected;if(options.TryGetValue(54,out selected)&&(!selected.SequenceEqual(server)))return null;
   if(type[0]==4){byte[] conflict;if(options.TryGetValue(50,out conflict)&&conflict.SequenceEqual(address)&&options.ContainsKey(54))declined=true;return null;} // Conflict reported by the TV: fail closed until checked.
   if(declined||(type[0]!=1&&type[0]!=3))return null;
   byte[] requested;bool nak=false;
   if(options.TryGetValue(50,out requested)){if(requested.Length!=4)return null;nak=!requested.SequenceEqual(address);}
   if(type[0]==3&&!Zero(p,12,4)&&!p.Skip(12).Take(4).SequenceEqual(address))nak=true;
   if(type[0]==3&&Zero(p,12,4)&&!options.ContainsKey(50))return null;
   // A DISCOVER may request a previous address; always offer our reserved address.
   byte response=type[0]==1?(byte)2:nak?(byte)6:(byte)5;
   var reply=new List<byte>(new byte[240]);reply[0]=2;reply[1]=1;reply[2]=6;
   for(int i=4;i<8;i++)reply[i]=p[i];
   reply[10]=0x80; // Broadcast initial responses: client may not yet own the address.
   if(response==5&&!Zero(p,12,4)){reply[10]=p[10];reply[11]=p[11];for(int i=12;i<16;i++)reply[i]=p[i];}
   for(int i=0;i<6;i++)reply[28+i]=mac[i];
   for(int i=0;i<4;i++)if(response!=6)reply[16+i]=address[i];
   reply[236]=99;reply[237]=130;reply[238]=83;reply[239]=99;
   Action<byte,byte[]> add=(key,value)=>{reply.Add(key);reply.Add((byte)value.Length);reply.AddRange(value);};
   add(53,new[]{response});add(54,server);
   byte[] clientId;if(options.TryGetValue(61,out clientId)&&clientId.Length>0)add(61,clientId);
   if(response!=6){add(1,IPAddress.Parse(config.Mask).GetAddressBytes());add(51,new byte[]{0,1,81,128});add(58,new byte[]{0,0,168,192});add(59,new byte[]{0,1,39,80});}
   reply.Add(255);while(reply.Count<300)reply.Add(0);
   string destination=type[0]==3&&!nak&&!Zero(p,12,4)?config.Address:"255.255.255.255";
   return new DhcpReply {Bytes=reply.ToArray(),Destination=destination,MessageType=response};
  }
 }
 sealed class DirectDhcpService:ServiceBase {
  public const string Name="MirakurunDirectDhcp";
  public static string DataDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"MirakurunDesktopSetup","DirectConnection");
  Thread worker;volatile bool stopping;Socket socket;
  public DirectDhcpService(){ServiceName=Name;CanStop=true;AutoLog=true;}
  protected override void OnStart(string[] args){stopping=false;worker=new Thread(Run){IsBackground=true,Name="TV dedicated DHCP"};worker.Start();}
  protected override void OnStop(){stopping=true;try{socket?.Close();}catch(SocketException){}worker?.Join(5000);}
  internal static void Log(string text){try{string path=Path.Combine(DataDirectory,"service.log");if(new FileInfo(path).Exists&&new FileInfo(path).Length>1024*1024)File.Move(path,path+"."+DateTime.UtcNow.Ticks);File.AppendAllText(path,DateTime.Now.ToString("s")+" "+text+Environment.NewLine);}catch(IOException){}catch(UnauthorizedAccessException){}}
  void Run(){
   while(!stopping){
    try {
     string file=Path.Combine(DataDirectory,"config.json");if(new FileInfo(file).Length>8192)throw new InvalidDataException("Configuration too large");
     var c=new JavaScriptSerializer().Deserialize<DirectDhcpConfig>(File.ReadAllText(file));c.Validate();
     var adapter=NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n=>string.Equals(n.Id,c.InterfaceId,StringComparison.OrdinalIgnoreCase));
     if(adapter==null||adapter.NetworkInterfaceType!=NetworkInterfaceType.Ethernet||adapter.OperationalStatus!=OperationalStatus.Up||
        !adapter.GetPhysicalAddress().GetAddressBytes().SequenceEqual(DirectDhcpConfig.Mac(c.AdapterMac)))throw new IOException("Waiting for selected physical Ethernet adapter");
     var properties=adapter.GetIPProperties();int index=properties.GetIPv4Properties().Index;
     if(!properties.UnicastAddresses.Any(a=>a.Address.Equals(IPAddress.Parse(c.Server))))throw new IOException("Waiting for dedicated PC address");
     var protocol=new DirectDhcpProtocol(c);
     using(var udp=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)) {
      socket=udp;udp.ExclusiveAddressUse=true;udp.EnableBroadcast=true;udp.ReceiveTimeout=1000;
      udp.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.PacketInformation,true);
      udp.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(index)); // IP_UNICAST_IF
      udp.Bind(new IPEndPoint(IPAddress.Any,67));Log("Listening; interface="+index+" reservation="+c.Address);
      var buffer=new byte[1501];
      while(!stopping){
       EndPoint remote=new IPEndPoint(IPAddress.Any,0);SocketFlags flags=SocketFlags.None;IPPacketInformation info;int n;
       try{n=udp.ReceiveMessageFrom(buffer,0,buffer.Length,ref flags,ref remote,out info);}catch(SocketException e)when(e.SocketErrorCode==SocketError.TimedOut){continue;}catch(SocketException e)when(e.SocketErrorCode==SocketError.MessageSize){continue;}
       if((flags&SocketFlags.Truncated)!=0)continue;
       var reply=protocol.Process(buffer,n,info.Interface,index,((IPEndPoint)remote).Port);if(reply==null)continue;
       udp.SendTo(reply.Bytes,new IPEndPoint(IPAddress.Parse(reply.Destination),68));Log("DHCP "+reply.MessageType+" -> "+c.Address);
      }
     }
    }catch(Exception e){if(!stopping)Log(e.GetType().Name+": "+e.Message);}finally{socket=null;}
    for(int i=0;i<50&&!stopping;i++)Thread.Sleep(100);
   }
  }
  internal static void StartServiceProcess(){ServiceBase.Run(new DirectDhcpService());}
 }
}
