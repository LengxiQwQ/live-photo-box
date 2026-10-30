#pragma once
#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <vector>
namespace lpb::media {
struct isobmff_track_reference {
 std::string kind;
 std::vector<uint32_t> targets;
};
struct isobmff_track_node {
 uint32_t id{};
 std::string handler;
 size_t offset{},length{};
 std::vector<isobmff_track_reference> references;
};
struct isobmff_track_graph {
 std::vector<isobmff_track_node> tracks;
 bool opaque_relationships{};
};
// Read-only actual track identity/relationship graph. Unknown reference kinds
// retain their bounded ID list without acquiring media or mutation semantics.
// Does not validate samples/codecs or grant destructive cleanup authority.
bool inspect_isobmff_track_graph(std::span<const uint8_t> moov,isobmff_track_graph&) noexcept;
}
