using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MirakurunDesktop;
static partial class Tests {
 static byte[] DnsName(string name){using(var m=new MemoryStream()){foreach(string label in name.Split('.')){byte[] b=Encoding.ASCII.GetBytes(label);m.WriteByte((byte)b.Length);m.Write(b,0,b.Length);}m.WriteByte(0);return m.ToArray();}}
 static void Dns16(Stream m,int value){m.WriteByte((byte)(value>>8));m.WriteByte((byte)value);}
 static int DnsRecord(MemoryStream m,byte[] name,int type,byte[] value){m.Write(name,0,name.Length);Dns16(m,type);Dns16(m,1);m.Write(new byte[]{0,0,0,120},0,4);Dns16(m,value.Length);int at=(int)m.Position;m.Write(value,0,value.Length);return at;}
 static byte[] Packet(byte[] ip,int port=40772,string service="_mirakurun._tcp.local"){using(var m=new MemoryStream()){m.Write(new byte[]{0,0,132,0,0,0,0,3,0,0,0,0},0,12);string instance="TV."+service;DnsRecord(m,DnsName(service),12,DnsName(instance));byte[] srv;using(var d=new MemoryStream()){d.Write(new byte[4],0,4);Dns16(d,port);var host=DnsName("tv.local");d.Write(host,0,host.Length);srv=d.ToArray();}int ptr=DnsRecord(m,DnsName(instance),33,srv)+6;DnsRecord(m,new byte[]{(byte)(192|(ptr>>8)),(byte)ptr},1,ip);return m.ToArray();}}
 static void DiscoveryTests(){
  var records=new MirakurunDiscovery.Records();records.Add(Packet(new byte[]{192,168,1,160}));Assert(records.Endpoints().Single()=="http://192.168.1.160:40772/","DNS-SD PTR SRV and compressed A records resolve a private TV endpoint");Assert(records.Candidates().Single().Hostname=="http://tv.local:40772/","discovery retains stable local hostname for client configuration");
  records=new MirakurunDiscovery.Records();records.Add(Packet(new byte[]{8,8,8,8}));Assert(records.Endpoints().Count==0,"mDNS cannot redirect automatic connection to public internet addresses");
  records=new MirakurunDiscovery.Records();records.Add(Packet(new byte[]{169,254,95,200}));Assert(records.Endpoints().Count==1,"direct-link APIPA endpoint is discoverable");
  records=new MirakurunDiscovery.Records();records.Add(Packet(new byte[]{192,168,1,160},0));Assert(records.Endpoints().Count==0,"zero service port is rejected");
  records=new MirakurunDiscovery.Records();records.Add(Packet(new byte[]{192,168,1,160},40772,"_http._tcp.local"));Assert(records.Endpoints().Count==0,"unrelated HTTP services are not selected as Mirakurun");
  byte[] malformed=Packet(new byte[]{192,168,1,160});malformed[12]=192;malformed[13]=12;records=new MirakurunDiscovery.Records();records.Add(malformed);records.Add(new byte[]{0,1});Assert(records.Endpoints().Count==0,"compression loops and truncated packets are safely ignored");
  Assert(MirakurunDiscovery.Query().Skip(MirakurunDiscovery.Query().Length-4).SequenceEqual(new byte[]{0,12,128,1}),"DNS-SD query requests unicast PTR replies");
  string id=new string('a',32);var wifi=new MirakurunDiscovery.Candidate {Address="http://192.168.1.160:40772/",Hostname="http://tv-wifi.local:40772/",DeviceId=id,Transport="wifi"};var wired=new MirakurunDiscovery.Candidate {Address="http://169.254.95.200:40772/",Hostname="http://tv.local:40772/",DeviceId=id,Transport="wired"};
  Assert(MirakurunDiscovery.PreferredRoutes(new[]{wifi,wired}).Single()==wired,"wired route wins even when cached Wi-Fi route arrives first");Assert(MirakurunDiscovery.PreferredRoutes(new[]{wifi}).Single()==wifi,"Wi-Fi fallback is available when wired route is unreachable");
  var other=new MirakurunDiscovery.Candidate {Address="http://192.168.1.170:40772/",Hostname="http://other.local:40772/",DeviceId=new string('b',32),Transport="wired"};Assert(MirakurunDiscovery.PreferredRoutes(new[]{wifi,wired,other}).Count==2,"multiple TVs remain distinct while paths of the same TV are grouped");
 }
 static async Task DiscoveryLive(string[] args){int at=Array.IndexOf(args,"--discovery-live");string expected=args[at+1];var found=await MirakurunDiscovery.FindCandidatesAsync(CancellationToken.None);Assert(found.Any(c=>c.Address==expected),"real multicast discovery finds the advertised TV");Assert(await MirakurunDiscovery.CheckAsync(expected,CancellationToken.None),"discovered TV HTTP status is validated before selection");foreach(var entry in found)Console.WriteLine("DISCOVERED "+entry.Transport+" "+entry.Address+" "+entry.Hostname+" grouped="+(entry.DeviceId.Length==32));var candidate=found.First(c=>c.Address==expected);Console.WriteLine("OS .local resolution: "+await MirakurunDiscovery.CheckAsync(candidate.Hostname,CancellationToken.None));}
}
