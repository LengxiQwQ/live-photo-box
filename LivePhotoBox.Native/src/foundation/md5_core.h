#pragma once
#include <array>
#include <bit>
#include <cstdint>
#include <span>
#include <string>

namespace lpb {
// Adobe Extended XMP transport identity only. This digest is not an authority,
// integrity/security boundary, artifact fingerprint or replacement for SHA-256.
inline std::string md5_hex_upper(std::span<const uint8_t> input) {
 constexpr std::array<uint32_t,64> k{
  0xd76aa478,0xe8c7b756,0x242070db,0xc1bdceee,0xf57c0faf,0x4787c62a,0xa8304613,0xfd469501,
  0x698098d8,0x8b44f7af,0xffff5bb1,0x895cd7be,0x6b901122,0xfd987193,0xa679438e,0x49b40821,
  0xf61e2562,0xc040b340,0x265e5a51,0xe9b6c7aa,0xd62f105d,0x02441453,0xd8a1e681,0xe7d3fbc8,
  0x21e1cde6,0xc33707d6,0xf4d50d87,0x455a14ed,0xa9e3e905,0xfcefa3f8,0x676f02d9,0x8d2a4c8a,
  0xfffa3942,0x8771f681,0x6d9d6122,0xfde5380c,0xa4beea44,0x4bdecfa9,0xf6bb4b60,0xbebfbc70,
  0x289b7ec6,0xeaa127fa,0xd4ef3085,0x04881d05,0xd9d4d039,0xe6db99e5,0x1fa27cf8,0xc4ac5665,
  0xf4292244,0x432aff97,0xab9423a7,0xfc93a039,0x655b59c3,0x8f0ccc92,0xffeff47d,0x85845dd1,
  0x6fa87e4f,0xfe2ce6e0,0xa3014314,0x4e0811a1,0xf7537e82,0xbd3af235,0x2ad7d2bb,0xeb86d391};
 constexpr std::array<int,64> shift{
  7,12,17,22,7,12,17,22,7,12,17,22,7,12,17,22,
  5,9,14,20,5,9,14,20,5,9,14,20,5,9,14,20,
  4,11,16,23,4,11,16,23,4,11,16,23,4,11,16,23,
  6,10,15,21,6,10,15,21,6,10,15,21,6,10,15,21};
 std::array<uint32_t,4> state{0x67452301,0xefcdab89,0x98badcfe,0x10325476};
 const auto block=[&](std::span<const uint8_t,64> bytes){
  std::array<uint32_t,16> words{};
  for(size_t i=0;i<16;++i)for(size_t j=0;j<4;++j)words[i]|=uint32_t(bytes[i*4+j])<<(j*8);
  auto a=state[0],b=state[1],c=state[2],d=state[3];
  for(unsigned i=0;i<64;++i){uint32_t f{},g{};
   if(i<16){f=(b&c)|(~b&d);g=i;}
   else if(i<32){f=(d&b)|(~d&c);g=(5*i+1)%16;}
   else if(i<48){f=b^c^d;g=(3*i+5)%16;}
   else{f=c^(b|~d);g=(7*i)%16;}
   const auto old_d=d;d=c;c=b;b+=std::rotl(a+f+k[i]+words[g],shift[i]);a=old_d;
  }
  state[0]+=a;state[1]+=b;state[2]+=c;state[3]+=d;
 };
 size_t offset=0;while(input.size()-offset>=64){block(std::span<const uint8_t,64>(input.data()+offset,64));offset+=64;}
 std::array<uint8_t,128> tail{};const size_t remain=input.size()-offset;
 for(size_t i=0;i<remain;++i)tail[i]=input[offset+i];tail[remain]=0x80;
 const size_t padded=remain<56?64:128;const uint64_t bits=static_cast<uint64_t>(input.size())*8;
 for(size_t i=0;i<8;++i)tail[padded-8+i]=static_cast<uint8_t>(bits>>(8*i));
 block(std::span<const uint8_t,64>(tail.data(),64));if(padded==128)block(std::span<const uint8_t,64>(tail.data()+64,64));
 constexpr char hex[]="0123456789ABCDEF";std::string result;result.reserve(32);
 for(const auto word:state)for(unsigned j=0;j<4;++j){const auto byte=static_cast<uint8_t>(word>>(j*8));result+=hex[byte>>4];result+=hex[byte&15];}
 return result;
}
}
