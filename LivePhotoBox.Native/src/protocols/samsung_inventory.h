#pragma once
#include "containers/isobmff.h"
#include <string>
#include <vector>

namespace lpb::protocols {
struct sef_tag {
 uint16_t marker{};
 uint64_t offset{},size{},data_offset{},data_size{};
 std::string name;
};
struct sef_inventory {
 uint64_t begin{},table{},end{};
 uint32_t version{};
 std::vector<sef_tag> tags;
 const sef_tag* find(uint16_t marker) const noexcept;
};
// Trusted read-only primitive. Sizes include the entire tag. Back offsets are
// unsigned distances BEFORE SEFH. The footer length excludes its own final8B.
// Every byte in [begin,table) must have exactly one tag owner.
bool observe_sef(const random_access_reader&,uint64_t lower,uint64_t end,
 sef_inventory&,std::string& error) noexcept;
bool read_sef_version(const random_access_reader&,const sef_inventory&,uint32_t&) noexcept;
bool ordinary_sef(const sef_inventory&) noexcept;
// Rebuild table coordinates, retaining each ordinary tag's exact raw bytes.
bool retain_ordinary_sef(std::span<const uint8_t>,const sef_inventory&,std::vector<uint8_t>&) noexcept;
}
