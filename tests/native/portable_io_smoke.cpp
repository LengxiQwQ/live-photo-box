#include "binary/portable_io.h"
#include "containers/isobmff.h"
#include <algorithm>
#include <array>
#undef NDEBUG // Keep the smoke assertions active in Release builds.
#include <cassert>
#include <cstring>
#include <fstream>
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
}
