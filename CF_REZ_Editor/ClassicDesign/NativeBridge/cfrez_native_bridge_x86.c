#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
typedef int (__cdecl *PACKFN)(const char*, const char*, const char*, int, const char*);
static const char* arg(int argc,char**argv,const char*key){for(int i=1;i+1<argc;i++)if(_stricmp(argv[i],key)==0)return argv[i+1];return NULL;}
int main(int argc,char**argv){const char*dll=arg(argc,argv,"--dll"),*rez=arg(argc,argv,"--rez"),*out=arg(argc,argv,"--out"),*off_s=arg(argc,argv,"--offset");uintptr_t off=off_s?(uintptr_t)strtoull(off_s,NULL,0):0xA730;if(!dll||!rez||!out){fprintf(stderr,"usage: cfrez_native_x86.exe --dll pack_cf_03.dll --rez RB001.REZ --out DIR [--offset 0xA730]\n");return 2;}char full[MAX_PATH];GetFullPathNameA(dll,MAX_PATH,full,NULL);char*slash=strrchr(full,'\\');if(slash){*slash=0;SetDllDirectoryA(full);}HMODULE h=LoadLibraryA(dll);if(!h){fprintf(stderr,"LoadLibrary failed: %lu\n",GetLastError());return 3;}PACKFN fn=(PACKFN)((uintptr_t)h+off);int rc=fn("xv",rez,out,1,"*.*");if(rc!=0){fprintf(stderr,"packer returned %d\n",rc);FreeLibrary(h);return 4;}FreeLibrary(h);return 0;}
