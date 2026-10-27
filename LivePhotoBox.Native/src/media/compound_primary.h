#pragma once
#include "foundation/internal.h"
#include "media/target_primary_observation.h"
#include <memory>
namespace lpb::media {
struct compound_primary_proof {
    uint64_t token{};
    lpb_file_identity identity{};
    std::array<uint8_t,32> sha256{}, opaque_sha256{};
    uint64_t byte_length{},known_length{},opaque_length{};
    lpb_source_media_facts facts{};
    std::atomic<bool> revoked{};
};
std::shared_ptr<compound_primary_proof> find_compound_primary(uint64_t token) noexcept;
bool validate_compound_primary(std::span<const uint8_t>,const compound_primary_proof&,
    target_primary_observation&,std::string&) noexcept;
}
