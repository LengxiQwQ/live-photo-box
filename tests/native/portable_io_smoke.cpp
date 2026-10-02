#include "binary/portable_io.h"
#include "containers/isobmff.h"
#include "media/gainmap_math.h"
#include <algorithm>
#include <array>
#undef NDEBUG // Keep the smoke assertions active in Release builds.
#include <cassert>
#include <cstring>
#include <fstream>
#include <cmath>
#include <vector>

static bool file_read(void* user, uint64_t offset, std::span<uint8_t> bytes) noexcept {
    auto& file = *static_cast<std::ifstream*>(user);
    file.clear();
    file.seekg(static_cast<std::streamoff>(offset));
    file.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return file.gcount() == static_cast<std::streamsize>(bytes.size());
}

static bool virtual_read(void*, uint64_t offset, std::span<uint8_t> bytes) noexcept {
    constexpr std::array<uint8_t, 8> header{0, 0, 0, 0, 'm', 'd', 'a', 't'};
    if (offset > header.size() || bytes.size() > header.size() - offset) return false;
    std::memcpy(bytes.data(), header.data() + offset, bytes.size());
    return true;
}

static size_t partial_write(void* user, std::span<const uint8_t> bytes) noexcept {
    auto& output = *static_cast<std::vector<uint8_t>*>(user);
    const size_t count = std::min<size_t>(bytes.size(), 3);
    output.insert(output.end(), bytes.begin(), bytes.begin() + count);
    return count;
}

int main(int argc, char** argv) {
    assert(argc == 2);
    constexpr std::array<uint8_t, 16> fixture{
        0, 0, 0, 8, 'f', 't', 'y', 'p', 0, 0, 0, 8, 'm', 'o', 'o', 'v'
    };
    lpb::memory_random_access memory{fixture};
    auto from_memory = scan_top_level_boxes(memory.view());
    assert(from_memory.size() == 2 && from_memory[1].offset == 8);
    std::array<uint8_t, 1> beyond{};
    assert(!memory.view().read_exact(fixture.size(), beyond));

    { std::ofstream out(argv[1], std::ios::binary);
      out.write(reinterpret_cast<const char*>(fixture.data()), fixture.size()); }
    std::ifstream file(argv[1], std::ios::binary);
    lpb::random_access_reader from_file{fixture.size(), &file, file_read};
    auto boxes = scan_top_level_boxes(from_file);
    assert(boxes.size() == from_memory.size());
    assert(boxes[0].size == from_memory[0].size && boxes[1].offset == from_memory[1].offset);

    constexpr uint64_t large_size = (uint64_t{1} << 32) + 4096;
    lpb::random_access_reader sparse{large_size, nullptr, virtual_read};
    auto large = scan_top_level_boxes(sparse);
    assert(large.size() == 1 && large[0].size == large_size);

    std::vector<uint8_t> written;
    lpb::sequential_writer sink{&written, partial_write};
    assert(sink.write_all(fixture));
    assert(written == std::vector<uint8_t>(fixture.begin(), fixture.end()));

    double headroom{};
    assert(lpb::media::apple_gainmap_headroom(1.568873048, 0.005930147133, headroom));
    assert(std::abs(headroom - 6.0) < 0.001);
    double rejected_headroom = 123.0;
    assert(!lpb::media::apple_gainmap_headroom(2.0, 20.0, rejected_headroom));
    assert(rejected_headroom == 0.0);
    assert(!lpb::media::apple_gainmap_headroom(2.0, 2.303 / 0.303, rejected_headroom));
    assert(rejected_headroom == 0.0);
    lpb::media::iso_gainmap_metadata metadata{};
    double low_recovery{};
    double mid_recovery{};
    double peak_recovery{};
    assert(lpb::media::apple_gain_to_iso_recovery(0.0, 1.568873048, 0.005930147133,
        low_recovery, metadata));
    assert(lpb::media::apple_gain_to_iso_recovery(0.5, 1.568873048, 0.005930147133,
        mid_recovery, metadata));
    assert(lpb::media::apple_gain_to_iso_recovery(1.0, 1.568873048, 0.005930147133,
        peak_recovery, metadata));
    assert(low_recovery == 0.0 && mid_recovery > 0.0 && mid_recovery < 1.0 && peak_recovery == 1.0);
    assert(metadata.gain_map_min[0] == 0.0 && std::abs(metadata.gain_map_max[0] - std::log2(headroom)) < 1e-12);
    assert(metadata.hdr_capacity_min == 0.0 && std::abs(metadata.hdr_capacity_max - std::log2(headroom)) < 1e-12);
    assert(!metadata.base_rendition_is_hdr);
    double converted_mid_gain{};
    assert(lpb::media::decode_iso_gainmap_sample(mid_recovery, metadata.gain_map_min[0],
        metadata.gain_map_max[0], metadata.gamma[0], converted_mid_gain));
    const double apple_linear_mid = 0.5 < 0.081 ? 0.5 / 4.5 : std::pow((0.5 + 0.099) / 1.099, 1.0 / 0.45);
    const double apple_expected_gain = 1.0 + (headroom - 1.0) * apple_linear_mid;
    assert(std::abs(converted_mid_gain - apple_expected_gain) < 1e-12);
    const std::array<uint8_t, 9> apple_map{ 0, 0, 0, 128, 128, 128, 255, 255, 255 };
    std::array<uint8_t, 9> iso_map{};
    lpb::media::iso_gainmap_metadata converted_metadata{};
    assert(lpb::media::convert_apple_gainmap_rgb_to_iso(apple_map, 1.568873048,
        0.005930147133, iso_map, converted_metadata));
    assert(iso_map[0] == 0 && iso_map[3] > 0 && iso_map[3] < 255 && iso_map[6] == 255);
    const std::array<uint8_t, 3> chromatic_map{ 0, 1, 0 };
    std::array<uint8_t, 3> untouched{ 7, 8, 9 };
    assert(!lpb::media::convert_apple_gainmap_rgb_to_iso(chromatic_map, 1.568873048,
        0.005930147133, untouched, converted_metadata));
    assert((untouched == std::array<uint8_t, 3>{ 7, 8, 9 }));
    double decoded_gain{};
    assert(lpb::media::decode_iso_gainmap_sample(0.5, 0.0, std::log2(headroom), 1.0, decoded_gain));
    assert(std::abs(decoded_gain - std::sqrt(headroom)) < 1e-12);
    assert(lpb::media::decode_iso_gainmap_sample(0.25, 0.0, 2.0, 2.0, decoded_gain));
    assert(std::abs(decoded_gain - 2.0) < 1e-12);
    double offset_result{};
    assert(lpb::media::apply_iso_gainmap_channel(0.4, 0.25, 0.0, 2.0, 2.0,
        0.1, 0.2, 1.0, offset_result));
    assert(std::abs(offset_result - 0.8) < 1e-12);
    assert(!lpb::media::apply_iso_gainmap_channel(0.4, 0.25, 0.0, 2.0, 2.0,
        0.1, 0.2, 1.1, offset_result));
    assert(!lpb::media::apple_gain_to_iso_recovery(1.01, 1.568873048, 0.005930147133,
        decoded_gain, metadata));
}
