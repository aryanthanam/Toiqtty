#include "renderer-lifetime.h"
#include <cstdio>
#include <stdexcept>

static int checks=0;
static void require(bool condition,const char* message){if(!condition)throw std::runtime_error(message);checks++;}
struct Resource {
    int refs=1,retains=0,releases=0;
    void AddRef() noexcept {refs++;retains++;}
    void Release() noexcept {refs--;releases++;}
};
int main() {
    try {
        LifetimeArena arena;
        auto first=static_cast<unsigned char*>(arena.allocate(8000,256));first[0]=17;first[7999]=41;
        require(reinterpret_cast<uintptr_t>(first)%256==0,"Arena alignment");
        (void)arena.allocate(64000,64);
        require(first[0]==17 && first[7999]==41,"Page growth preserves prior addresses and bytes");
        bool rejected=false;try{(void)arena.allocate(SIZE_MAX,256);}catch(const std::bad_alloc&){rejected=true;}
        require(rejected && first[0]==17,"Oversize allocation fails without disturbing the live scope");
        arena.reset();auto reused=arena.allocate(8000,256);
        require(reused==first,"Reset reuses the retained first page");
        LifetimeArena peak;(void)peak.allocate(1024*1024,16);peak.reset();(void)peak.allocate(8,8);peak.reset();
        require(peak.bytes()<=16384,"A small subsequent lifetime sheds a prior large page");
        Resource image,target;
        SubmissionLifetime submission;
        for(int i=0;i<100000;i++){submission.retain(&image);submission.retain(&target);}
        require(image.refs==2 && target.refs==2,"One pin per resource per submission");
        require(submission.pinRequests()==200000 && submission.uniquePins()==2 && submission.livePins()==2,"Deduplication counters");
        require(submission.arenaBytes()<=16384,"Repeated uses do not grow resource bookkeeping");
        submission.retire();require(image.refs==1 && target.refs==1 && submission.livePins()==0,"Retirement releases pins before recycling");
        auto retainedBytes=submission.arenaBytes();
        for(int i=0;i<1000;i++){submission.retain(&image);submission.retire();}
        require(submission.arenaBytes()==retainedBytes && image.refs==1,"Repeated lifetimes recycle arena storage without accumulating references");
        {SubmissionLifetime abandoned;abandoned.retain(&image);}
        require(image.refs==1,"Scope destruction releases outstanding pins");
        {
            LifetimeRef<Resource> active(&image);LifetimeRef<Resource> batch=active;
            active=&target;batch=&image;batch=&image;
            require(image.refs==2 && target.refs==2,"Active and pending resources have independent, idempotent leases");
        }
        require(image.refs==1 && target.refs==1,"Drawing scope leases release independently");
        CacheMemory blocks;
        {
            std::byte local[128];std::pmr::monotonic_buffer_resource entry(local,sizeof(local),&blocks);
            std::pmr::vector<int> glyphs(&entry);glyphs.resize(10000,7);
            require(blocks.bytes()>=glyphs.size()*sizeof(int) && glyphs.back()==7,"Cache arena accounts for real upstream blocks");
        }
        require(blocks.bytes()==0,"Eviction releases a complete cache-entry arena");
        std::printf("%d native lifetime checks passed.\n",checks);return 0;
    }catch(const std::exception& error){std::fprintf(stderr,"FAIL: %s\n",error.what());return 1;}
}
