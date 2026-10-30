#pragma once

#include "media/opaque_camera_records.h"
#include "foundation/sha256_core.h"
#include <array>

namespace lpb::media::ordinary_metadata {

// Closed ordinary-metadata envelope. These bytes never authorize a source
// live binding, external path, edit operation or detached auxiliary append.
inline constexpr std::array<uint8_t,16> name_space{
    'L','i','v','e','P','h','o','t','o','B','o','x','M','D','v','1'};
inline constexpr size_t header_size=80,maximum_payload=65533,chunk_bytes=maximum_payload-header_size;
inline constexpr uint64_t maximum_raw=32ULL*1024*1024;

inline void put32(std::span<uint8_t> b,size_t at,uint32_t value){for(size_t i=0;i<4;++i)b[at+i]=static_cast<uint8_t>(value>>(24-i*8));}
inline void put64(std::span<uint8_t> b,size_t at,uint64_t value){for(size_t i=0;i<8;++i)b[at+i]=static_cast<uint8_t>(value>>(56-i*8));}
inline uint32_t get32(std::span<const uint8_t> b,size_t at){uint32_t n=0;for(size_t i=0;i<4;++i)n=(n<<8)|b[at+i];return n;}
inline uint64_t get64(std::span<const uint8_t> b,size_t at){uint64_t n=0;for(size_t i=0;i<8;++i)n=(n<<8)|b[at+i];return n;}
inline std::array<uint8_t,32> digest(std::span<const uint8_t> b){std::array<uint8_t,32> hash{};lpb::crypto::sha256_ctx h;h.update(b.data(),b.size());h.finalize(hash.data());return hash;}
inline bool namespace_claim(std::span<const uint8_t> b) noexcept {return b.size()>=name_space.size()&&std::equal(name_space.begin(),name_space.end(),b.begin());}
inline bool complete_record_block(std::span<const uint8_t> b) noexcept {
    opaque_camera_record_inventory records;
    return !b.empty()&&b.size()<=maximum_raw&&observe_opaque_camera_records(b,records)&&
        records.block_offset==0&&records.block_length==b.size();
}

inline bool encode_chunks(std::span<const uint8_t> raw,std::vector<std::vector<uint8_t>>& output) noexcept {
    output.clear();
    try {
        if(!complete_record_block(raw))return false;
        const auto hash=digest(raw);const size_t count=(raw.size()+chunk_bytes-1)/chunk_bytes;
        std::vector<std::vector<uint8_t>> chunks;chunks.reserve(count);
        for(size_t i=0;i<count;++i) {
            const size_t offset=i*chunk_bytes,length=std::min(chunk_bytes,raw.size()-offset);
            std::vector<uint8_t> part(header_size+length,0);
            std::copy(name_space.begin(),name_space.end(),part.begin());
            put32(part,16,1);put32(part,20,1); // version1, self-relative ordinary records
            // flags@24 and primary owner index@28 are exactly zero.
            put64(part,32,raw.size());std::copy(hash.begin(),hash.end(),part.begin()+40);
            put32(part,72,static_cast<uint32_t>(i));put32(part,76,static_cast<uint32_t>(count));
            std::copy_n(raw.data()+offset,length,part.data()+header_size);chunks.push_back(std::move(part));
        }
        output=std::move(chunks);return true;
    }catch(...){output.clear();return false;}
}

inline bool decode_chunks(std::span<const std::span<const uint8_t>> chunks,std::vector<uint8_t>& output) noexcept {
    output.clear();
    try {
        if(chunks.empty()||chunks.size()>(maximum_raw+chunk_bytes-1)/chunk_bytes)return false;
        uint64_t length=0;std::array<uint8_t,32> expected{};std::vector<uint8_t> raw;
        for(size_t i=0;i<chunks.size();++i) {
            const auto b=chunks[i];
            if(b.size()<header_size||b.size()>maximum_payload||!namespace_claim(b)||
                get32(b,16)!=1||get32(b,20)!=1||get32(b,24)||get32(b,28)||
                get32(b,72)!=i||get32(b,76)!=chunks.size())return false;
            const uint64_t actual=get64(b,32);
            if(!actual||actual>maximum_raw||(actual+chunk_bytes-1)/chunk_bytes!=chunks.size())return false;
            if(i==0){length=actual;std::copy_n(b.data()+40,32,expected.data());raw.reserve(static_cast<size_t>(length));}
            if(actual!=length||!std::equal(expected.begin(),expected.end(),b.begin()+40))return false;
            const uint64_t offset=i*chunk_bytes;
            if(offset>=length||b.size()-header_size!=std::min<uint64_t>(chunk_bytes,length-offset))return false;
            raw.insert(raw.end(),b.begin()+header_size,b.end());
        }
        if(raw.size()!=length||digest(raw)!=expected||!complete_record_block(raw))return false;
        output=std::move(raw);return true;
    }catch(...){output.clear();return false;}
}
}
