from __future__
import csv, hashlib, json, lzma, math, re, os, sys, subprocess, tempfile, ctypes, shutil, struct
from dataclasses import dataclass, asdict
from pathlib import Path

HEADER_SIZE=168
MAX_DEPTH=128
# CrossFire/LithTech RezMgr v1 directory key stream.
KEYS=bytes([240,240,157,9,10,102,173,106,133,29,253,63,81,35,231,243,177,14,120,236,209,80,123,107,23,63,97,197,121,12,87,50,26,243,184,107,104,222,42,95,1,186,152,58,153,192,84,2,36,247,155,9,135,35,196,111,14,108,68,250,219,251,232,133,171,194,101,60,14,196,147,246,109,11,138,214,17,141,227,143,113,82,93,110,252,253,41,130,176,29,19,17,174,92,213,169,27,248,206,252,121,156,90,214,206,253,12,100,202,96,22,18,49,91,8,58,207,4,62,234,35,220,40,250,32,165,192,184,33,115,94,108,106,43,49,233,109,189,154,115,17,76,177,67,58,142,40,206,220,155,212,49,207,119,29,228,159,138,139,10,178,78,192,141,221,116,11,86,207,183,238,213,116,167,181,27,161,169,133,203,69,104,255,31,89,251,205,66,218,255,89,55,5,231,220,158,18,189,27,135,187,151,2,154,194,4,102,211,190,167,44,17,102,78,16,189,168,179,84,194,192,57,141,23,145,218,224,33,134,138,211,36,55,74,16,19,10,56,69,226,38,198,102,192,222,115,155,83,226,45,10,87,126,172,201,196,12,4,51,213,250,159,229,21,138,253,149,207,154,87,22,2,178,129,190,57,140,58,114,106,111,52,138,47,132,14,238,150,109,128,131,188,106,2,69,132,58,28,73,160,1,183,218,44,118,150,255,29,142,73,167,202,245,214,176,189,127,81,33,37,234,172,183,21,22,246,36,215,14,84,39,150,13,236,212,150,201,0,51,77,67,131,140,123,89,94,150,175,95,172,195,74,249,35,252,98,123,255,245,185,12,145,106,1,205,201,135,187,67,252,164,231,73,13,181,199,195,90,149,247,82,145,120,29,82,196,188,99,90,228,106,17,123,255,141,114,142,100,181,83,184,7,221,78,127,77,244,53,153,150,74,198,198,183,32,246,235,169,161,24,175,167,119,7,226,11,73,186,225,18,96,85,65,221,168,33,3,229,91,143,129,30,141,139,106,17,224,111,249,47,150,193,186,142,77,6,6,98,154,232,146,102,204,251,52,123,17,66,52,188,61,220,99,62,122,247,44,212,25,96,245,243,197,225,249,29,95,180,239,239,186,78,177,53,123,189,38,29,97,208,176,244,44,101,100,132,107,251,60,116,109,225,147,210,152,54,42,24,95,250,226,225,35,124,140,147,46,83,238,64,35,44,86,243,251,179,236,188,250,199,6,166,192,75,204,232,187,193,76,132,65,1,103,162,143,67,178,214,234,182,164,160,33,247,69,94,188,142,159,242,3,204,59,95,53,54,212,145,24,195,158,166,54,50,68,224,250,178,241,145,239,31,157,57,102,16,218,24,194,254,102,115,159,186,200,210,44,123,35,106,217,189,158,2,178,53,126,135,158,27,88,154,193,6,112,73,61,154,180,70,159,77,103,203,42,130,220,117,74,50,112,80,104,110,10,92,101,242,94,196,246,14,52,4,35,36,243,75,48,243,178,78,38,2,7,200,61,84,229,251,111,180,176,94,113,216,225,185,68,146,105,2,187,92,22,36,22,112,62,253,9,189,242,210,105,231,238,116,179,161,146,90,192,153,26,242,221,58,98,94,129,125,102,240,233,20,202,143,221,36,166,90,212,216,211,184,187,3,3,29,166,25,209,198,158,186,37,168,216,22,11,207,141,92,91,120,185,136,96,25,251,184,193,160,217,101,243,36,175,159,106,79,114,172,210,179,172,47,135,92,203,43,154,208,28,24,143,199,167,71,38,214,50,229,104,74,165,196,49,124,22,68,140,216,176,140,1,214,205,81,55,43,98,123,15,102,32,216,136,75,108,35,171,28,132,162,175,21,1,149,172,98,3,187,15,194,60,41,15,36,34,185,107,114,134,70,166,214,203,6,14,176,4,44,189,126,53,41,237,254,249,185,193,188,201,10,216,91,47,51,233,208,15,62,154,204,99,12,224,163,145,74,37,225,169,179,107,210,198,242,186,65,213,81,15,174,251,124,15,48,228,154,190,80,54,249,122,23,98,142,123,148,35,140,21,12,213,72,2,43,251,182,235,91,34,190,117,158,106,153,26,13,246,144,252,87,121,67,1,111,47,205,116,171,116,245,101,157,67,187,19,222,213,109,151,8,169,158,17,46,42,41,160,253,63,132,82,219,251,180,103,48,179,8,11,45,183,238,218,65,237,28,106,127,152,79,20,69,117,212,66,68,140,79,20,69,117,212,66,68,140,52,134,79,217,40,175,16,30,37,34,247,26,192,190,160,93,30,124,227,15,190,23,228,197,213,249,77,208,127,167])

@dataclass
class RezHeader:
    file_type:str; user_title:str; version:int; root_dir_pos:int; root_dir_size:int; root_dir_time:int; next_write_pos:int; time:int; largest_key_ary:int; largest_dir_name_size:int; largest_rez_name_size:int; largest_comment_size:int; is_sorted:int
@dataclass
class RezEntry:
    path:str; name:str; extension:str; data_offset:int; size:int; time:int; ident:int; md5:str; directory:str=""; valid_range:bool=True; issue:str=""; native_path:str=""
    @property
    def end(self): return self.data_offset+self.size
    def to_dict(self): d=asdict(self); d["end"]=self.end; return d
@dataclass
class RezArchive:
    path:Path; size:int; header:RezHeader; entries:list[RezEntry]; invalid:list[str]

def _decode(buf:bytearray,pos:int):
    for i in range(len(buf)):
        buf[i]=((KEYS[pos%len(KEYS)] ^ ((~buf[i])&255))+73)&255; pos+=1

def _fixed_ascii(b): return b.rstrip(b'\0').decode('ascii','replace').rstrip()
def read_header(raw):
    if len(raw)<HEADER_SIZE: raise ValueError('File is smaller than the REZ header.')
    vals=struct.unpack_from('<10iB',raw,127)
    return RezHeader(_fixed_ascii(raw[2:62]),_fixed_ascii(raw[64:124]),*vals)
def _read_i32(b,p):
    if p+4>len(b): raise EOFError
    return struct.unpack_from('<i',b,p)[0],p+4
def _read_name(b,p):
    n,p=_read_i32(b,p)
    if n<0 or p+n>len(b): raise EOFError
    return b[p:p+n].decode('ascii','replace'),p+n
def _decode_extension(b): return b.rstrip(b'\0 ').decode('ascii','replace')[::-1]
def _parse_range(raw,a,owner,offset,size,depth,visited):
    if depth>MAX_DEPTH or size<=0 or offset<HEADER_SIZE or offset>=len(raw): a.invalid.append(f'invalid directory table range at {offset}+{size}'); return
    readable=min(size,len(raw)-offset); key=(offset,readable)
    if key in visited:return
    visited.add(key); buf=bytearray(raw[offset:offset+readable]); _decode(buf,offset); p=0
    while p+4<=len(buf):
        start=p; typ,p=_read_i32(buf,p)
        try:
            if typ==0:
                if p+28>len(buf): raise EOFError
                data_off,p=_read_i32(buf,p); fsize,p=_read_i32(buf,p); tm,p=_read_i32(buf,p); ident,p=_read_i32(buf,p); ext=_decode_extension(bytes(buf[p:p+4])); p+=4; _,p=_read_i32(buf,p); name,p=_read_name(buf,p)
                if p+34>len(buf): raise EOFError
                p+=2; md5=bytes(buf[p:p+32]).decode('ascii','replace'); p+=32
                full=f'{owner}/{name}.{ext}' if owner else f'{name}.{ext}'; valid=bool(name and ext and fsize>=0 and data_off>=HEADER_SIZE and data_off+fsize<=len(raw)); issue='' if valid else f'resource range 0x{data_off:X}+{fsize:,} exceeds archive size {len(raw):,}'
                a.entries.append(RezEntry(full,name+'.'+ext,ext,data_off,fsize,tm,ident,md5,owner,valid,issue))
                if not valid:a.invalid.append(f'{full}: {issue}')
            elif typ==1:
                if p+16>len(buf): raise EOFError
                table_off,p=_read_i32(buf,p); table_size,p=_read_i32(buf,p); _,p=_read_i32(buf,p); name,p=_read_name(buf,p)
                if p>=len(buf): raise EOFError
                p+=1
                if table_off<HEADER_SIZE or table_size<=0 or table_off>=len(raw): a.invalid.append(f'invalid directory range {name} at {table_off}+{table_size}')
                else: _parse_range(raw,a,f'{owner}/{name}' if owner else name,table_off,table_size,depth+1,visited)
            else: a.invalid.append(f'unknown entry type {typ} at table {offset}+{start}'); break
        except EOFError: a.invalid.append(f'truncated entry at table {offset}+{start}'); break
        if p<=start: break

def read_rez(path):
    p=Path(path); raw=p.read_bytes(); h=read_header(raw)
    if h.root_dir_pos<HEADER_SIZE or h.root_dir_size<=0 or h.root_dir_pos>=len(raw): raise ValueError('Invalid REZ root directory range.')
    a=RezArchive(p,len(raw),h,[],[]); _parse_range(raw,a,'',h.root_dir_pos,h.root_dir_size,0,set())
    if not a.entries: raise ValueError('REZ archive contains no readable resources.')
    return a

def read_entry(path,e):
    if e.native_path:return Path(e.native_path).read_bytes()
    if not e.valid_range: raise ValueError(f'Cannot read invalid resource range: {e.path}\n{e.issue}')
    with open(path,'rb') as f:f.seek(e.data_offset); data=f.read(e.size)
    if len(data)!=e.size: raise ValueError(f'Short read for {e.path}: expected {e.size:,}, got {len(data):,}')
    return data

def md5_bytes(b):return hashlib.md5(b).hexdigest()
def sha256_bytes(b):return hashlib.sha256(b).hexdigest()
def decompress_lzma_alone(data):return lzma.decompress(data,format=lzma.FORMAT_ALONE)
def looks_lzma_alone(data):
    try:decompress_lzma_alone(data);return True
    except Exception:return False
def strings_from_bytes(data,min_len=4,limit=5000):
    out=[]
    for m in re.finditer(rb'[ -~]{%d,}'%min_len,data):
        out.append((m.start(),m.group().decode('ascii','replace')))
        if len(out)>=limit:break
    return out
def entropy(data):
    if not data:return 0.0
    c=[0]*256
    for b in data:c[b]+=1
    n=len(data);return -sum((x/n)*math.log2(x/n) for x in c if x)
def classify(path,data=None):
    ext=Path(path).suffix.lower(); m={'.cft':'CFT','.msz':'MSZ Messages','.scv':'SCV Resource','.dat':'DAT','.utc':'UTC','.png':'Image/Texture','.dtx':'Image/Texture','.ltc':'Model/Map','.ltb':'Model/Map','.lta':'Model/Map','.fnt':'Font','.lto':'LithTech Object'}
    if ext in m:return m[ext]
    if data:
        if data.startswith(b'\x89PNG'):return 'Image/Texture'
        if data.startswith(b'DDS '):return 'Image/Texture'
        if data.startswith(b'OggS') or data.startswith(b'RIFF'):return 'Audio'
        if looks_lzma_alone(data):return 'LZMA resource'
    return 'Binary/Unknown'
def analyze_blob(data,path=''):
    dec=data;codec='Raw'
    if looks_lzma_alone(data):
        try:dec=decompress_lzma_alone(data);codec='LZMA-Alone'
        except Exception:pass
    magic=[]
    for sig,name in [(b'MZ','PE/Windows executable'),(b'PK\x03\x04','ZIP'),(b'\x5d\x00\x00','possible LZMA-Alone'),(b'\x89PNG','PNG'),(b'DDS ','DDS texture'),(b'OggS','Ogg media'),(b'RIFF','RIFF container')]:
        if data.startswith(sig):magic.append(name)
    return {'path':path,'extension':Path(path).suffix.lower(),'stored_size':len(data),'decoded_size':len(dec),'codec':codec,'magic':magic,'entropy':round(entropy(dec),5),'strings':strings_from_bytes(dec,4,160),'md5':md5_bytes(data),'sha256':sha256_bytes(data),'type':classify(path,dec)}
def scan_to_json(rez_path,out_json=None):
    a=read_rez(rez_path); r={'file':str(a.path),'size':a.size,'header':asdict(a.header),'entry_count':len(a.entries),'invalid_count':len(a.invalid),'invalid':a.invalid,'entries':[e.to_dict() for e in a.entries]}
    if out_json:Path(out_json).write_text(json.dumps(r,indent=2),encoding='utf-8')
    return r

def _pe_machine(path):
    try:
        b=Path(path).read_bytes()[:0x1000]
        if len(b)<0x40 or b[:2]!=b'MZ':return None
        pe=struct.unpack_from('<I',b,0x3c)[0]
        if pe+6>len(b) or b[pe:pe+4]!=b'PE\0\0':return None
        return struct.unpack_from('<H',b,pe+4)[0]
    except Exception:return None
def _candidate_native_tools(rez_path):
    p=Path(rez_path).resolve(); roots=[]
    for q in [p.parent,p.parent.parent,p.parent.parent.parent,Path.cwd(),Path(sys.executable).resolve().parent,Path.home()/'Desktop']:
        if q and q not in roots:roots.append(q)
    for env in ('CROSSFIRE_ROOT','CF_ROOT','CROSSFIRE_HOME'):
        v=os.environ.get(env)
        if v:
            q=Path(v).expanduser()
            if q not in roots:roots.append(q)
    seen=set()
    for root in roots:
        for q in [root,root/'rez',root/'REZ',root/'tools',root/'Tools',root/'bin',root/'Bin']:
            if not q.exists():continue
            for f in list(q.glob('cfrez.exe'))+list(q.glob('CFREZ.EXE'))+list(q.glob('cfrezformat.dll'))+list(q.glob('pack_cf_*.dll')):
                key=str(f.resolve()).lower()
                if key not in seen:seen.add(key);yield f
def _native_bridge_candidates(dll_path):
    dll=Path(dll_path).resolve();roots=[Path(__file__).resolve().parent,Path(sys.executable).resolve().parent,Path.cwd(),dll.parent,dll.parent.parent];names=['cfrez_native_x86.exe','CF_REZ_NativeBridge_x86.exe','cfrez_bridge_x86.exe'];seen=set()
    for root in roots:
        for n in names:
            f=root/n
            if f.exists() and str(f).lower() not in seen:seen.add(str(f).lower());yield f
def _native_extract_with_exe(tool,rez_path,out_dir):
    p=subprocess.run([str(tool),'xv',str(rez_path),str(out_dir)],capture_output=True,text=True,timeout=180,cwd=str(tool.parent))
    if p.returncode!=0:raise RuntimeError(f'cfrez.exe failed ({p.returncode}): {(p.stderr or p.stdout).strip()}')
    return p.stdout or p.stderr or ''
def _native_extract_with_bridge(helper,dll_path,rez_path,out_dir):
    offset=os.environ.get('CFREZ_PACK_OFFSET','0xA730')
    p=subprocess.run([str(helper),'--dll',str(dll_path),'--rez',str(rez_path),'--out',str(out_dir),'--offset',offset],capture_output=True,text=True,timeout=180,cwd=str(helper.parent))
    msg=(p.stderr or p.stdout or '').strip()
    if p.returncode!=0:raise RuntimeError(f'x86 native bridge failed ({p.returncode}): {msg}')
    return msg or 'x86 native bridge completed'
def _native_extract_with_dll(dll_path,rez_path,out_dir):
    if os.name!='nt':raise RuntimeError('pack_cf_*.dll native extraction is only available on Windows.')
    machine=_pe_machine(dll_path)
    if machine==0x14c and struct.calcsize('P')==8:
        helpers=list(_native_bridge_candidates(dll_path))
        if helpers:return _native_extract_with_bridge(helpers[0],dll_path,rez_path,out_dir)
        raise RuntimeError(f'{dll_path.name} is x86 (32-bit), while this editor is x64 (64-bit). Place cfrez_native_x86.exe beside the editor or use a compatible cfrez.exe.')
    kernel=ctypes.windll.kernel32;kernel.SetDllDirectoryW(str(Path(dll_path).resolve().parent));base=kernel.LoadLibraryW(str(Path(dll_path).resolve()))
    if not base:raise RuntimeError(f'Unable to load {dll_path.name}. Win32 error {kernel.GetLastError()}')
    Fn=ctypes.WINFUNCTYPE(ctypes.c_int,ctypes.c_char_p,ctypes.c_char_p,ctypes.c_char_p,ctypes.c_bool,ctypes.c_char_p);fn=Fn(base+int(os.environ.get('CFREZ_PACK_OFFSET','0xA730'),0));result=fn(b'xv',os.fsencode(str(rez_path)),os.fsencode(str(out_dir)),True,b'*.*')
    if result!=0:raise RuntimeError(f'Native CrossFire packer returned {result}')
    return 'native packer completed'
def native_extract_rez(rez_path,out_dir=None):
    rez=Path(rez_path).resolve();owned=out_dir is None
    if out_dir is None:out_dir=Path(tempfile.mkdtemp(prefix='cfrez_native_'))
    else:out_dir=Path(out_dir);out_dir.mkdir(parents=True,exist_ok=True)
    errors=[];tools=list(_candidate_native_tools(rez))
    for tool in tools:
        try:
            if tool.name.lower()=='cfrez.exe':_native_extract_with_exe(tool,rez,out_dir);break
        except Exception as ex:errors.append(f'{tool}: {ex}')
    else:
        for tool in tools:
            if tool.suffix.lower()=='.dll':
                try:_native_extract_with_dll(tool,rez,out_dir);break
                except Exception as ex:errors.append(f'{tool}: {ex}')
        else:
            if owned:shutil.rmtree(out_dir,ignore_errors=True)
            raise RuntimeError('No compatible CrossFire native packer was found.\n\n'+'\n'.join(errors))
    files=[f for f in out_dir.rglob('*') if f.is_file()]
    if not files:
        if owned:shutil.rmtree(out_dir,ignore_errors=True)
        raise RuntimeError('The native CrossFire packer ran but produced no extracted files.\n'+'\n'.join(errors))
    entries=[]
    for f in files:
        rel=f.relative_to(out_dir).as_posix();raw=f.read_bytes();name=f.name;ext=f.suffix[1:] if f.suffix else '';directory=str(Path(rel).parent).replace('\\','/') if Path(rel).parent!=Path('.') else ''
        entries.append(RezEntry('/'+rel,name,ext,0,len(raw),0,0,hashlib.md5(raw).hexdigest(),directory,True,'',str(f)))
    entries.sort(key=lambda x:x.path.lower());h=read_header(rez.read_bytes());a=RezArchive(rez,rez.stat().st_size,h,entries,[]);a.native_root=out_dir;a.native_extractor='CrossFire native packer';a.native_owned=owned;return a
def rez_diagnostic(path):
    p=Path(path);raw=p.read_bytes();h=read_header(raw);root=h.root_dir_pos;size=h.root_dir_size;sample=raw[root:root+min(max(size,0),256)] if 0<=root<len(raw) else b''
    return {'file':p.name,'size':len(raw),'file_type':h.file_type,'user_title':h.user_title,'version':h.version,'root_dir_pos':root,'root_dir_size':size,'next_write_pos':h.next_write_pos,'root_sample_hex':sample.hex(),'protected_or_variant':True,'reason':'Valid LithTech-style header, but the directory table did not decode with the standard RezCrypto stream. This is likely a CrossFire-protected/variant REZ and needs its matching native packer/unlocker.'}

def extract_entry(path,e,out):Path(out).write_bytes(read_entry(path,e))
