#pragma once
#include <bit>
#include <cmath>
#include <cstdint>
#include <span>
namespace lpb::media {
struct lpcm_v2_layout {
 uint32_t rate{},channels{},bits{},bytes_per_packet{},frames_per_packet{},extension_offset{};
 bool floating{},big_endian{},signed_integer{};
};
// Read-only interleaved packed LPCM v2. No audio conversion and no fallback.
// Input is the sample-entry body; sizeOfStructOnly includes the 8-byte header.
inline bool inspect_lpcm_v2(std::span<const uint8_t> data,lpcm_v2_layout& out) noexcept {
 out={};if(data.size()<64)return false;
 const auto u16=[&](size_t at){return (uint32_t(data[at])<<8)|data[at+1];};
 const auto u32=[&](size_t at){return (uint32_t(data[at])<<24)|(uint32_t(data[at+1])<<16)|(uint32_t(data[at+2])<<8)|data[at+3];};
 for(size_t at=0;at<6;++at)if(data[at])return false;
 if(u16(8)!=2||u16(10)||u32(12)||u16(16)!=3||u16(18)!=16||u16(20)!=65534||u16(22)||u32(24)!=65536||u32(44)!=0x7f000000)return false;
 const uint32_t size=u32(28);if(size<72||size-8>data.size())return false;
 // Bytes between the published v2 structure and its extensions are not an
 // alternate field layout that this parser understands.
 if(size!=72)return false;
 const uint64_t rate_bits=(uint64_t(u32(32))<<32)|u32(36);const double rate=std::bit_cast<double>(rate_bits);
 if(!std::isfinite(rate)||rate<1||rate>UINT32_MAX||std::floor(rate)!=rate)return false;
 lpcm_v2_layout value;value.rate=static_cast<uint32_t>(rate);value.channels=u32(40);value.bits=u32(48);
 const uint32_t flags=u32(52);value.floating=(flags&1)!=0;value.big_endian=(flags&2)!=0;value.signed_integer=(flags&4)!=0;
 // Packed, interleaved samples only. Unknown/fractional/aligned-high layouts
 // need their own byte-layout proof; never reinterpret them as packed PCM.
 if((flags&~0x4fu)||!(flags&8)||(value.floating&&value.signed_integer)||!value.channels||!value.bits||value.bits%8)return false;
 if(value.floating?(value.bits!=32&&value.bits!=64):(value.bits!=8&&value.bits!=16&&value.bits!=24&&value.bits!=32))return false;
 value.bytes_per_packet=u32(56);value.frames_per_packet=u32(60);value.extension_offset=size-8;
 const uint64_t frame_bytes=uint64_t(value.channels)*(value.bits/8);
 if(!value.frames_per_packet||frame_bytes>UINT32_MAX||frame_bytes*value.frames_per_packet!=value.bytes_per_packet)return false;
 out=value;return true;
}
}
