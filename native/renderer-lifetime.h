#pragma once
#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <memory_resource>
#include <optional>
#include <unordered_set>
#include <vector>
#include <utility>

// CPU memory owned by one reusable lifetime. Addresses stay stable while the
// scope is live. Its owner destroys objects before reset; reset recycles pages.
class LifetimeArena final : public std::pmr::memory_resource {
    struct Page {std::unique_ptr<std::byte[]> data;size_t capacity,used=0;};
    static constexpr size_t PageBytes=16384;
    std::vector<Page> pages;
    size_t cursor=0,reserved=0;
    void* do_allocate(size_t bytes,size_t alignment) override {
        if(bytes>SIZE_MAX-alignment)throw std::bad_alloc();
        while(cursor<pages.size()) {
            auto& page=pages[cursor];void* start=page.data.get()+page.used;size_t space=page.capacity-page.used;
            if(auto result=std::align(alignment,bytes,start,space)){
                page.used=static_cast<std::byte*>(result)-page.data.get()+bytes;return result;
            }
            cursor++;
        }
        size_t capacity=std::max(PageBytes,bytes+alignment-1);
        Page page{std::make_unique_for_overwrite<std::byte[]>(capacity),capacity};
        pages.push_back(std::move(page));reserved+=capacity;
        return do_allocate(bytes,alignment);
    }
    void do_deallocate(void*,size_t,size_t) override {}
    bool do_is_equal(const std::pmr::memory_resource& other) const noexcept override {return this==&other;}
public:
    size_t bytes() const noexcept {return reserved;}
    size_t used() const noexcept {size_t total=0;for(const auto& page:pages)total+=page.used;return total;}
    void reset() noexcept {
        // Retain enough for a similar next scope; shed a previous large peak
        // after usage falls. Allocation remains lazy, without forced GC.
        size_t previous=used();size_t keep=std::max(PageBytes,previous>SIZE_MAX/2?SIZE_MAX:previous*2);
        while(pages.size()>1 && reserved-pages.back().capacity>=keep){reserved-=pages.back().capacity;pages.pop_back();}
        if(pages.size()==1 && pages[0].capacity>keep){pages.clear();reserved=0;}
        for(auto& page:pages)page.used=0;cursor=0;
    }
};

// One ownership record per object per submission, rather than one AddRef and
// several independent shared leases per draw. All bookkeeping uses the arena.
class SubmissionLifetime {
    struct Use {
        void* object;void (*release)(void*);
        bool operator==(const Use&) const=default;
    };
    struct Hash {
        size_t operator()(const Use& value) const {return std::hash<void*>{}(value.object)^(std::hash<decltype(value.release)>{}(value.release)<<1);}
    };
    struct Records {
        std::pmr::unordered_set<Use,Hash> uses;
        explicit Records(std::pmr::memory_resource* memory):uses(memory){}
        ~Records(){for(const auto& use:uses)use.release(use.object);}
    };
    LifetimeArena arena;
    std::optional<Records> records;
    uint64_t requests=0,pins=0;
public:
    SubmissionLifetime(){records.emplace(&arena);}
    SubmissionLifetime(const SubmissionLifetime&)=delete;
    SubmissionLifetime& operator=(const SubmissionLifetime&)=delete;
    template<class T> void retain(T* object) {
        if(!object)return;requests++;
        Use use{object,[](void* value){static_cast<T*>(value)->Release();}};
        if(records->uses.insert(use).second){object->AddRef();pins++;}
    }
    // Caller must have observed the submission fence, or have abandoned work
    // on a removed device. Object destruction precedes page recycling.
    void retire() {records.reset();arena.reset();records.emplace(&arena);}
    uint64_t pinRequests() const noexcept {return requests;}
    uint64_t uniquePins() const noexcept {return pins;}
    size_t livePins() const noexcept {return records->uses.size();}
    size_t arenaBytes() const noexcept {return arena.bytes();}
};

// An intrusive reference adds no separate control-block allocation. Native
// texture owners, active sessions and submissions share one resource lifetime.
template<class T> class LifetimeRef {
    T* value=nullptr;
public:
    LifetimeRef()=default;
    explicit LifetimeRef(T* object):value(object){if(value)value->AddRef();}
    LifetimeRef(const LifetimeRef& other):LifetimeRef(other.value){}
    LifetimeRef(LifetimeRef&& other) noexcept:value(std::exchange(other.value,nullptr)){}
    ~LifetimeRef(){if(value)value->Release();}
    LifetimeRef& operator=(T* object){if(value!=object){if(object)object->AddRef();auto old=std::exchange(value,object);if(old)old->Release();}return *this;}
    LifetimeRef& operator=(const LifetimeRef& other){return *this=other.value;}
    operator T*() const noexcept {return value;}
    T* operator->() const noexcept {return value;}
};

// Counts actual upstream blocks of an individual cache-entry arena, including
// growth slack. Cache eviction budgets use these bytes, not just vector sizes.
class CacheMemory final : public std::pmr::memory_resource {
    size_t allocated=0;
    void* do_allocate(size_t bytes,size_t alignment) override {auto result=std::pmr::new_delete_resource()->allocate(bytes,alignment);allocated+=bytes;return result;}
    void do_deallocate(void* object,size_t bytes,size_t alignment) override {std::pmr::new_delete_resource()->deallocate(object,bytes,alignment);allocated-=bytes;}
    bool do_is_equal(const std::pmr::memory_resource& other) const noexcept override {return this==&other;}
public:
    size_t bytes() const noexcept {return allocated;}
};
