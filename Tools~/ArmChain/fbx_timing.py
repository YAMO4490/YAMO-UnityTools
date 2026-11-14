"""Read binary FBX key times without installing an FBX Python SDK."""
import struct,zlib,array
from pathlib import Path


def inspect(path,rate):
    data=Path(path).read_bytes()
    if not data.startswith(b'Kaydara FBX Binary'):raise ValueError('Expected a binary FBX')
    version=struct.unpack_from('<I',data,23)[0];wide=version>=7500
    header=27 if wide else 13;fmt='<QQQB' if wide else '<IIIB';steps=set();curves=0
    def node(pos):
        nonlocal curves
        end,count,plen,nlen=struct.unpack_from(fmt,data,pos)
        if not end:return pos+header,False
        pos+=header;name=data[pos:pos+nlen].decode('utf-8',errors='replace');pos+=nlen
        for _ in range(count):
            typ=chr(data[pos]);pos+=1
            if typ in 'YCIFDL':pos+={'Y':2,'C':1,'I':4,'F':4,'D':8,'L':8}[typ]
            elif typ in 'SR':size=struct.unpack_from('<I',data,pos)[0];pos+=4+size
            elif typ in 'fdlibc':
                size,encoding,compressed=struct.unpack_from('<III',data,pos);pos+=12
                raw=data[pos:pos+compressed];pos+=compressed
                if name=='KeyTime':
                    if encoding:raw=zlib.decompress(raw)
                    keys=array.array('q');keys.frombytes(raw)
                    if len(keys)!=size:raise ValueError('FBX key time length differs')
                    steps.update(b-a for a,b in zip(keys,keys[1:]));curves+=1
            else:raise ValueError('Unsupported FBX property '+typ)
        while pos<end-header:
            pos,valid=node(pos)
            if not valid:break
        return end,True
    pos=27
    while pos<len(data):
        pos,valid=node(pos)
        if not valid:break
    frame_steps=sorted(round(v/46186158000*rate,6) for v in steps)
    if not curves or frame_steps!=[1.0]:raise ValueError('FBX does not contain explicit one-frame keys: '+str(frame_steps))
    return dict(curveCount=curves,keyStepsFrames=frame_steps,version=version)
