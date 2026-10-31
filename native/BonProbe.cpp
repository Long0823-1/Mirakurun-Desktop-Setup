#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "IBonDriver2.h"
#include <fstream>
#include <string>
#include <vector>
#include <sstream>

static std::string utf8(const std::wstring& s) {
 int n=WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0,nullptr,nullptr);
 std::string r(n,0);if(n)WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),&r[0],n,nullptr,nullptr);return r;
}
static std::string json(const std::wstring& s) {
 std::string r="\"";for(unsigned char c:utf8(s)){if(c=='"'||c=='\\'){r+='\\';r+=c;}else if(c<32){char b[8];sprintf_s(b,"\\u%04x",c);r+=b;}else r+=c;}return r+'"';
}
int wmain(int argc,wchar_t** argv) {
 if(argc<4)return 2;
 SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
 HMODULE mod=LoadLibraryW(argv[2]);if(!mod)return 10;
 auto create=(IBonDriver*(WINAPI*)())GetProcAddress(mod,"CreateBonDriver");if(!create)return 11;
 IBonDriver* base=create();if(!base)return 12;
 IBonDriver2* driver=dynamic_cast<IBonDriver2*>(base);if(!driver){base->Release();return 13;}
 if(!driver->OpenTuner()){base->Release();return 14;}
 int result=0;
 if(wcscmp(argv[1],L"enumerate")==0) {
  std::ofstream out(argv[3],std::ios::binary);out<<"[";bool first=true;
  for(DWORD space=0;space<32;space++){
   const wchar_t* type=driver->EnumTuningSpace(space);if(!type)break;
   for(DWORD ch=0;ch<1024;ch++){
    const wchar_t* name=driver->EnumChannelName(space,ch);if(!name)break;
    if(!first)out<<",";first=false;
    out<<"{\"space\":"<<space<<",\"index\":"<<ch<<",\"type\":"<<json(type)<<",\"name\":"<<json(name)<<"}";
   }
  }out<<"]";if(first)result=15;
 }else if(wcscmp(argv[1],L"capture")==0 && argc==9){
  DWORD space=wcstoul(argv[3],nullptr,10),ch=wcstoul(argv[4],nullptr,10);
  int seconds=_wtoi(argv[5]);if(seconds<1||seconds>180)result=16;
  else if(!driver->SetChannel(space,ch))result=17;
  else{
   std::ofstream epg(argv[6],std::ios::binary);std::ofstream sample;
   if(wcscmp(argv[8],L"-")!=0)sample.open(argv[8],std::ios::binary);
   unsigned long long bytes=0,packets=0;unsigned tsid=0,onid=0;std::vector<BYTE> pending;
   ULONGLONG deadline=GetTickCount64()+(ULONGLONG)seconds*1000;
   while(GetTickCount64()<deadline){
    driver->WaitTsStream(100);BYTE* buf=nullptr;DWORD size=0,remain=0;
    if(!driver->GetTsStream(&buf,&size,&remain)||!buf||!size){Sleep(10);continue;}
    if(size>32*1024*1024){result=18;break;}
    bytes+=size;if(sample.is_open()&&bytes<8*1024*1024)sample.write((char*)buf,size);
    pending.insert(pending.end(),buf,buf+size);size_t i=0;
    for(;i+188<=pending.size();){
     BYTE* p=&pending[i];if(p[0]!=0x47||(i+376<=pending.size()&&pending[i+188]!=0x47)){i++;continue;}
     i+=188;if(p[1]&0x80)continue;unsigned pid=((p[1]&31)<<8)|p[2];
     if(pid<32){epg.write((char*)p,188);packets++;}
     int off=4;if(p[3]&0x20)off+=1+p[4];
     if(!(p[3]&0x10)||!(p[1]&0x40)||off>=188)continue;
     off+=1+p[off];if(off+11>=188)continue;
     if(pid==0&&p[off]==0)tsid=(p[off+3]<<8)|p[off+4];
     if(pid==17&&p[off]==0x42)onid=(p[off+8]<<8)|p[off+9];
    }
    pending.erase(pending.begin(),pending.begin()+i);
   }
   std::ofstream report(argv[7],std::ios::binary);
   report<<"{\"bytes\":"<<bytes<<",\"packets\":"<<packets<<",\"tsid\":"<<tsid<<",\"onid\":"<<onid<<"}";
   if(bytes<18800||!tsid)result=19;
  }
 }else result=2;
 driver->CloseTuner();base->Release();FreeLibrary(mod);return result;
}
