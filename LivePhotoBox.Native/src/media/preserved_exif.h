#pragma once
#include "foundation/sha256_core.h"
#include <array>
#include <cstdint>
#include <string>
#include <vector>
namespace lpb::media {
inline constexpr const char* preserved_exif_namespace="urn:livephotobox:preserved-exif:1";
struct preserved_user_comment {
 bool present{};
 uint16_t tiff_type{};
 uint32_t count{};
 std::vector<uint8_t> raw;
 std::array<uint8_t,32> sha256{};
 bool operator==(const preserved_user_comment&) const = default;
};
// Private serializer evidence only: Mapped refers to a comparison with the
// pinned input tuple, not a public VerifiedPreserved result or restoration API.
struct user_comment_mapping {
 enum class verification_scope { none,formal_input_tuple_to_unique_output_xmp_record };
 bool mapped{},input_compared{},output_compared{};
 preserved_user_comment original;
 std::string destination;
 std::array<uint8_t,32> destination_packet_sha256{};
 verification_scope scope{verification_scope::none};
};
inline std::string upper_hex(const uint8_t* bytes,size_t size) {
 constexpr char digits[]="0123456789ABCDEF";std::string out;out.reserve(size*2);
 for(size_t i=0;i<size;++i){out+=digits[bytes[i]>>4];out+=digits[bytes[i]&15];}return out;
}
inline preserved_user_comment comment_tuple(uint16_t type,uint32_t count,const std::vector<uint8_t>& raw) {
 preserved_user_comment out;out.present=true;out.tiff_type=type;out.count=count;out.raw=raw;
 crypto::sha256_buffer(raw.data(),raw.size(),out.sha256.data());return out;
}
}
