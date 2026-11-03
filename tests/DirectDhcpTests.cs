using System;
using System.Collections.Generic;
using System.Linq;
using MirakurunDesktop;

static partial class Tests {
 static byte[] DhcpRequest(byte type,string mac="001122334455",byte[] requested=null,byte[] selected=null,byte[] ci=null){
  var p=new List<byte>(new byte[240]);p[0]=1;p[1]=1;p[2]=6;p[4]=0x12;p[5]=0x34;p[6]=0x56;p[7]=0x78;
  var bytes=DirectDhcpConfig.Mac(mac);for(int i=0;i<6;i++)p[28+i]=bytes[i];if(ci!=null)for(int i=0;i<4;i++)p[12+i]=ci[i];
  p[236]=99;p[237]=130;p[238]=83;p[239]=99;p.AddRange(new byte[]{53,1,type});
  if(requested!=null){p.Add(50);p.Add(4);p.AddRange(requested);}if(selected!=null){p.Add(54);p.Add(4);p.AddRange(selected);}
  p.AddRange(new byte[]{61,3,9,8,7,255});return p.ToArray();
 }
 static void DirectDhcpTests(){
  var c=new DirectDhcpConfig {InterfaceId=Guid.NewGuid().ToString(),AdapterMac="001122334466",TvMac="001122334455",Server="192.168.250.1",Address="192.168.250.2",Mask="255.255.255.252"};var dhcp=new DirectDhcpProtocol(c);
  Func<byte[],int,int,DhcpReply> receive=(p,iface,port)=>dhcp.Process(p,p.Length,iface,10,port);
  var discover=DhcpRequest(1);var offer=receive(discover,10,68);
  Assert(offer!=null&&offer.MessageType==2&&offer.Bytes.Skip(16).Take(4).SequenceEqual(new byte[]{192,168,250,2}),"DHCP offers the reserved TV address");
  Assert(offer.Bytes.Skip(4).Take(4).SequenceEqual(discover.Skip(4).Take(4)),"DHCP transaction ID retained");
  Assert(receive(discover,20,68)==null,"DHCP ignores Wi-Fi and other ingress interfaces");
  Assert(receive(discover,10,67)==null,"DHCP ignores relay/server source ports");
  Assert(receive(DhcpRequest(1,"001122334477"),10,68)==null,"DHCP never offers to another MAC");
  var relayed=DhcpRequest(1);relayed[24]=1;Assert(receive(relayed,10,68)==null,"DHCP relays rejected");
  var other=DhcpRequest(3,requested:new byte[]{192,168,250,2},selected:new byte[]{192,168,250,9});Assert(receive(other,10,68)==null,"DHCP selection of another server ignored");
  var ack=receive(DhcpRequest(3,requested:new byte[]{192,168,250,2},selected:new byte[]{192,168,250,1}),10,68);
  Assert(ack!=null&&ack.MessageType==5,"DHCP request acknowledged");
  var renew=receive(DhcpRequest(3,ci:new byte[]{192,168,250,2}),10,68);Assert(renew.MessageType==5&&renew.Destination==c.Address,"DHCP renewal unicasts to configured TV");
  Assert(renew.Bytes.Skip(12).Take(4).SequenceEqual(new byte[]{192,168,250,2})&&renew.Bytes[10]==0,"DHCP renewal preserves ciaddr and client flags");
  Assert(receive(DhcpRequest(3,requested:new byte[]{192,168,250,3}),10,68).MessageType==6,"DHCP wrong requested address gets NAK");
  Assert(receive(DhcpRequest(1,requested:new byte[]{192,168,250,3}),10,68).MessageType==2,"DHCP old address hint still receives reservation offer");
  var malformed=discover.Take(discover.Length-1).ToArray();Assert(receive(malformed,10,68)==null,"DHCP unterminated options ignored");
  var duplicate=discover.Take(discover.Length-1).Concat(new byte[]{53,1,1,255}).ToArray();Assert(receive(duplicate,10,68)==null,"DHCP duplicate options ignored");
  Assert(dhcp.Process(new byte[2],2,10,10,68)==null,"DHCP truncated packets ignored");
  var optionCodes=new List<byte>();for(int i=240;i<ack.Bytes.Length&&ack.Bytes[i]!=255;){byte code=ack.Bytes[i++];if(code==0)continue;optionCodes.Add(code);i+=1+ack.Bytes[i];}
  Assert(!optionCodes.Contains(3)&&!optionCodes.Contains(6),"DHCP does not advertise gateway or DNS");
  Assert(optionCodes.Contains(61),"DHCP echoes client identifier");
  receive(DhcpRequest(4,requested:new byte[]{192,168,250,2},selected:new byte[]{192,168,250,1}),10,68);
  Assert(receive(discover,10,68)==null,"DHCP stops offers after TV reports a conflict");
 }
}
