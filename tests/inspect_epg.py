"""Optional live verification using the installed EDCB parser (Windows x64)."""
import ctypes as c
import json
from pathlib import Path
import sys

root=Path(sys.argv[1]).resolve()
dll=c.WinDLL(str(root/'EDCB/EpgDataCap3.dll'))
dll.InitializeEP.argtypes=[c.c_int,c.POINTER(c.c_uint32)]
dll.AddTSPacketEP.argtypes=[c.c_uint32,c.c_void_p,c.c_uint32]
dll.GetEpgInfoListEP.argtypes=[c.c_uint32,c.c_uint16,c.c_uint16,c.c_uint16,c.POINTER(c.c_uint32),c.POINTER(c.c_void_p)]
instance=c.c_uint32()
assert dll.InitializeEP(0,c.byref(instance))==1
try:
    packets=0
    for path in sorted((root/'EDCB/Setting/EpgData').glob('*_epg.dat')):
        data=path.read_bytes()
        assert len(data)%188==0 and all(data[i]==0x47 for i in range(0,len(data),188)),path
        assert dll.AddTSPacketEP(instance,data,len(data))==1
        packets+=len(data)//188
    services=[]
    for line in (root/'EDCB/Setting/ChSet5.txt').read_text(encoding='utf-8-sig').splitlines():
        columns=line.split('\t')
        count=c.c_uint32();events=c.c_void_p()
        status=dll.GetEpgInfoListEP(instance,*map(int,columns[2:5]),c.byref(count),c.byref(events))
        services.append({'name':columns[0],'events':count.value,'status':status})
    report={'packets':packets,'services':len(services),'events':sum(s['events'] for s in services),'details':services}
    print(json.dumps(report,ensure_ascii=False,indent=2))
    assert report['events']>0,'EDCB parsed no EPG events'
finally:
    dll.UnInitializeEP(instance)
