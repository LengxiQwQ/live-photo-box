#include "heif_cleaner.h"
#include "xmp_cleaner.h"
#include "foundation/internal.h"
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <vector>

namespace fs = std::filesystem;

namespace lpb::protocols::clean {

static void add_fact(std::vector<lpb_removed_protocol_fact>& facts)
{
    lpb_removed_protocol_fact fact{};
    fact.struct_size = sizeof(fact);
    strncpy_s(fact.protocol_name, "Samsung", _TRUNCATE);
    strncpy_s(fact.component, "HEIF mpvd/sefd Boxes", _TRUNCATE);
    strncpy_s(fact.description, "Removed the validated Samsung Motion Photo mpvd payload and sefd metadata boxes", _TRUNCATE);
    facts.push_back(fact);
}

static bool read_file(const fs::path& path, std::vector<uint8_t>& data)
{
    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input.is_open()) return false;
    const auto size = input.tellg();
    if (size < 16 || static_cast<uint64_t>(size) > std::numeric_limits<size_t>::max()) return false;
    data.resize(static_cast<size_t>(size));
    input.seekg(0, std::ios::beg);
    input.read(reinterpret_cast<char*>(data.data()), static_cast<std::streamsize>(data.size()));
    return input.good() || input.gcount() == static_cast<std::streamsize>(data.size());
}

static bool write_atomic(const fs::path& path, const std::vector<uint8_t>& data)
{
    fs::path temp = path;
    temp += L".lpb-heif-cleaning-tmp";
    std::error_code ec;
    fs::remove(temp, ec);
    {
        std::ofstream output(temp, std::ios::binary | std::ios::trunc);
        if (!output.is_open()) return false;
        output.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
        output.flush();
        if (!output.good()) { output.close(); fs::remove(temp, ec); return false; }
    }
    fs::remove(path, ec);
    fs::rename(temp, path, ec);
    if (ec) { fs::remove(temp, ec); return false; }
    return true;
}

static uint32_t be32(const uint8_t* p) noexcept
{
    return (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) |
        (static_cast<uint32_t>(p[2]) << 8) | p[3];
}

static bool rewrite_samsung_xmp(
    lpb_context* context,
    std::vector<uint8_t>& data,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    const std::string_view whole(
        reinterpret_cast<const char*>(data.data()), data.size());
    const size_t xml_start = whole.find("<x:xmpmeta");
    const std::string_view xml_end_tag = "</x:xmpmeta>";
    if (xml_start == std::string_view::npos)
    {
        // Some HEIF files do not carry an XMP item. The structural Samsung
        // payload cleaner can still remove mpvd in that case.
        return true;
    }
    const size_t xml_end = whole.find(xml_end_tag, xml_start);
    if (xml_end == std::string_view::npos)
    {
        set_error(context, "Samsung HEIF XMP item is truncated.");
        return false;
    }

    const size_t old_xml_length = xml_end + xml_end_tag.size() - xml_start;
    const std::string xmp(whole.substr(xml_start, old_xml_length));
    std::string cleaned_xmp;
    if (!clean_xmp_metadata(
            xmp, LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC, cleaned_xmp, out_facts))
    {
        return true;
    }
    if (cleaned_xmp.size() > old_xml_length)
    {
        set_error(context, "Cleaned Samsung HEIF XMP is larger than the source item.");
        return false;
    }

    // Keep the HEIF item length and all iloc offsets stable. The removed
    // protocol fields only make the XML shorter; zero-fill the unused tail so
    // no stale bytes can be parsed as a second XML document.
    std::copy(cleaned_xmp.begin(), cleaned_xmp.end(), data.begin() + xml_start);
    std::fill(data.begin() + xml_start + cleaned_xmp.size(),
        data.begin() + xml_start + old_xml_length, static_cast<uint8_t>(0));
    return true;
}

lpb_result clean_samsung_heic(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> input;
    if (!read_file(utf8_to_path(input_path.c_str()), input)) {
        set_error(context, "Failed to read Samsung HEIC for cleaning.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (input.size() < 16 || std::memcmp(input.data() + 4, "ftyp", 4) != 0) {
        set_error(context, "Input is not a structurally valid HEIF container.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (!rewrite_samsung_xmp(context, input, out_facts)) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> output;
    output.reserve(input.size());
    size_t offset = 0;
    bool removed_mpvd = false;
    bool removed_sefd = false;
    while (offset < input.size()) {
        if (input.size() - offset < 8) {
            set_error(context, "Truncated HEIF box header.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const uint32_t size32 = be32(input.data() + offset);
        const uint32_t type = be32(input.data() + offset + 4);
        uint64_t box_size = size32;
        size_t header_size = 8;
        if (size32 == 1) {
            if (input.size() - offset < 16) {
                set_error(context, "Truncated HEIF extended box size.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            box_size = (static_cast<uint64_t>(be32(input.data() + offset + 8)) << 32) |
                be32(input.data() + offset + 12);
            header_size = 16;
        } else if (size32 == 0) {
            box_size = input.size() - offset;
        }
        if (box_size < header_size || box_size > input.size() - offset) {
            set_error(context, "HEIF box exceeds the source file.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (type == 0x6D707664U) { // mpvd
            removed_mpvd = true;
        } else if (type == 0x73656664U) { // sefd
            removed_sefd = true;
        } else {
            output.insert(output.end(), input.begin() + offset,
                input.begin() + offset + static_cast<size_t>(box_size));
        }
        offset += static_cast<size_t>(box_size);
    }
    if (!removed_mpvd) {
        set_error(context, "Validated Samsung HEIF mpvd box was not found.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (!write_atomic(utf8_to_path(output_path.c_str()), output)) {
        set_error(context, "Failed to publish cleaned Samsung HEIC.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (removed_sefd) add_fact(out_facts);
    return LPB_RESULT_OK;
}

} // namespace lpb::protocols::clean
