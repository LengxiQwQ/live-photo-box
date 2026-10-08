#include "foundation/portable_internal.h"
#include "binary/binary_io.h"
#include "jpeg.h"

#include <algorithm>
#include <cstring>
#include <limits>
#include <span>
#include <string>
#include <vector>

using namespace lpb;

namespace {

const uint8_t XMP_HEADER[] = {
    'h', 't', 't', 'p', ':', '/', '/', 'n', 's', '.', 'a', 'd', 'o', 'b', 'e', '.', 'c', 'o', 'm', '/',
    'x', 'a', 'p', '/', '1', '.', '0', '/', 0
};
constexpr size_t XMP_HEADER_SIZE = sizeof(XMP_HEADER);

struct jpeg_segment {
    size_t start{};
    size_t marker_size{}; // usually 2 (0xFFXX)
    size_t payload_size{}; // 2 (length) + length_value - 2
    uint8_t marker{};
    bool is_xmp{};
};

static bool collect_jpeg_segments(
    std::span<const uint8_t> input,
    std::vector<jpeg_segment>& segments,
    size_t& sos_pos,
    std::string& out_error)
{
    segments.clear();
    sos_pos = 0;
    if (input.size() < 2 || input[0] != 0xFF || input[1] != 0xD8) {
        out_error = "Input is not a valid JPEG (missing SOI).";
        return false;
    }

    binary_reader reader(input.data(), input.size());
    uint16_t soi = 0;
    if (!reader.try_read_be16u(soi) || soi != 0xFFD8) {
        out_error = "Input is not a valid JPEG (missing SOI).";
        return false;
    }

    bool saw_eoi = false;
    while (reader.remaining() > 0) {
        uint8_t current_byte = 0;
        if (!reader.try_read_u8(current_byte) || current_byte != 0xFF) {
            out_error = "Input JPEG contains an invalid marker stream.";
            return false;
        }

        const size_t marker_start = reader.position() - 1;
        while (reader.remaining() > 0 && reader.data()[reader.position()] == 0xFF) {
            reader.skip(1);
        }

        uint8_t marker = 0;
        if (!reader.try_read_u8(marker)) {
            out_error = "Input JPEG ends inside a marker.";
            return false;
        }

        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (marker == 0xD9) {
            sos_pos = marker_start;
            saw_eoi = true;
            break;
        }

        if (marker == 0xDA) {
            sos_pos = marker_start;
            uint16_t length = 0;
            if (!reader.try_read_be16u(length) || length < 2 ||
                reader.remaining() < static_cast<size_t>(length - 2)) {
                out_error = "Input JPEG has a truncated SOS segment.";
                return false;
            }
            if (!reader.skip(static_cast<size_t>(length - 2))) {
                out_error = "Input JPEG has a truncated SOS payload.";
                return false;
            }
            size_t scan = reader.position();
            while (scan + 1 < input.size()) {
                if (input[scan] != 0xFF) {
                    ++scan;
                    continue;
                }
                const uint8_t scan_marker = input[scan + 1];
                if (scan_marker == 0x00 || (scan_marker >= 0xD0 && scan_marker <= 0xD7)) {
                    scan += 2;
                    continue;
                }
                if (scan_marker == 0xFF) {
                    ++scan;
                    continue;
                }
                if (scan_marker == 0xD9) {
                    saw_eoi = true;
                    break;
                }
                // A marker other than stuffed data/restart/EOI is allowed as
                // post-scan container data, but the scan must contain a real EOI.
                ++scan;
            }
            if (!saw_eoi) {
                out_error = "Input JPEG scan has no EOI marker.";
                return false;
            }
            break;
        }

        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (marker == 0x01) continue;

        uint16_t length = 0;
        if (!reader.try_read_be16u(length) || length < 2) {
            out_error = "Input JPEG has an invalid segment length.";
            return false;
        }
        const size_t payload_size = length;
        if (reader.remaining() < payload_size - 2) {
            out_error = "Input JPEG contains a truncated segment.";
            return false;
        }

        bool is_xmp = false;
        if (marker == 0xE1 && payload_size >= 2 + XMP_HEADER_SIZE &&
            std::memcmp(reader.current_ptr(), XMP_HEADER, XMP_HEADER_SIZE) == 0) {
            is_xmp = true;
        }
        segments.push_back({ marker_start, reader.position() - 2 - marker_start,
            payload_size, marker, is_xmp });
        if (!reader.skip(payload_size - 2)) {
            out_error = "Input JPEG segment exceeds the source buffer.";
            return false;
        }
    }

    if (sos_pos == 0 || !saw_eoi) {
        out_error = "Input JPEG has no complete scan or EOI marker.";
        return false;
    }
    return true;
}

static lpb_result build_jpeg_xmp_replacement(
    std::span<const uint8_t> input,
    std::span<const uint8_t> xmp_xml,
    std::vector<uint8_t>& out_bytes,
    std::string& out_error)
{
    std::vector<jpeg_segment> segments;
    size_t sos_pos = 0;
    if (!collect_jpeg_segments(input, segments, sos_pos, out_error)) return LPB_RESULT_INVALID_ARGUMENT;

    std::vector<uint8_t> new_xmp_segment;
    if (!xmp_xml.empty()) {
        if (xmp_xml.size() > std::numeric_limits<size_t>::max() - 2 - XMP_HEADER_SIZE) {
            out_error = "XMP payload size overflows the host size type.";
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const size_t new_xmp_len = 2 + XMP_HEADER_SIZE + xmp_xml.size();
        if (new_xmp_len > 0xFFFF) {
            out_error = "XMP XML is too large for APP1 segment.";
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        new_xmp_segment.resize(2 + new_xmp_len);
        binary_writer writer(new_xmp_segment.data(), new_xmp_segment.size());
        if (!writer.try_write_u8(0xFF) || !writer.try_write_u8(0xE1) ||
            !writer.try_write_be16(static_cast<uint16_t>(new_xmp_len)) ||
            !writer.try_write_bytes(XMP_HEADER, XMP_HEADER_SIZE) ||
            !writer.try_write_bytes(xmp_xml.data(), xmp_xml.size())) {
            out_error = "Failed to prepare the replacement XMP APP1 segment.";
            return LPB_RESULT_INTERNAL_ERROR;
        }
    }

    // Insert after APP0 or Exif APP1 when present, matching the public Cleaner
    // injection path's established segment ordering.
    size_t insert_idx = 0;
    for (size_t index = 0; index < segments.size(); ++index) {
        if (segments[index].marker == 0xE0 ||
            (segments[index].marker == 0xE1 && !segments[index].is_xmp)) {
            insert_idx = index + 1;
        }
    }

    size_t total_expected = 2;
    if (new_xmp_segment.size() > std::numeric_limits<size_t>::max() - total_expected) {
        out_error = "JPEG output size overflows the host size type.";
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    total_expected += new_xmp_segment.size();
    for (const auto& segment : segments) {
        if (segment.is_xmp) continue;
        if (segment.marker_size > std::numeric_limits<size_t>::max() - segment.payload_size ||
            total_expected > std::numeric_limits<size_t>::max() - (segment.marker_size + segment.payload_size)) {
            out_error = "JPEG output size overflows the host size type.";
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        total_expected += segment.marker_size + segment.payload_size;
    }
    if (sos_pos > input.size() || input.size() - sos_pos > std::numeric_limits<size_t>::max() - total_expected) {
        out_error = "JPEG output size overflows the host size type.";
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    total_expected += input.size() - sos_pos;

    out_bytes.resize(total_expected);
    binary_writer out_writer(out_bytes.data(), out_bytes.size());
    if (!out_writer.try_write_be16(0xFFD8)) {
        out_error = "Failed to write the JPEG SOI marker.";
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (insert_idx == 0 && !new_xmp_segment.empty() &&
        !out_writer.try_write_bytes(new_xmp_segment.data(), new_xmp_segment.size())) {
        out_error = "Failed to write the replacement XMP APP1 segment.";
        return LPB_RESULT_INTERNAL_ERROR;
    }

    for (size_t index = 0; index < segments.size(); ++index) {
        const auto& segment = segments[index];
        if (!segment.is_xmp &&
            !out_writer.try_write_bytes(input.data() + segment.start, segment.marker_size + segment.payload_size)) {
            out_error = "Failed to preserve an existing JPEG segment.";
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if (index + 1 == insert_idx && !new_xmp_segment.empty() &&
            !out_writer.try_write_bytes(new_xmp_segment.data(), new_xmp_segment.size())) {
            out_error = "Failed to write the replacement XMP APP1 segment.";
            return LPB_RESULT_INTERNAL_ERROR;
        }
    }
    if (!out_writer.try_write_bytes(input.data() + sos_pos, input.size() - sos_pos) ||
        out_writer.position() != total_expected) {
        out_error = "Failed to preserve the JPEG scan and trailing bytes.";
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

} // namespace

namespace lpb::metadata {

bool extract_standard_jpeg_xmp(std::span<const uint8_t> input, std::string& out_xml) noexcept
{
    out_xml.clear();
    try {
        std::vector<jpeg_segment> segments;
        size_t sos_pos = 0;
        std::string error;
        if (!collect_jpeg_segments(input, segments, sos_pos, error)) return false;
        const jpeg_segment* xmp_segment = nullptr;
        for (const auto& segment : segments) {
            if (!segment.is_xmp) continue;
            if (xmp_segment != nullptr) return false;
            xmp_segment = &segment;
        }
        if (xmp_segment == nullptr || xmp_segment->payload_size < 2 + XMP_HEADER_SIZE) return false;
        const size_t xml_offset = xmp_segment->start + xmp_segment->marker_size + 2 + XMP_HEADER_SIZE;
        const size_t xml_length = xmp_segment->payload_size - 2 - XMP_HEADER_SIZE;
        if (xml_offset > input.size() || xml_length > input.size() - xml_offset) return false;
        out_xml.assign(reinterpret_cast<const char*>(input.data() + xml_offset), xml_length);
        return !out_xml.empty();
    }
    catch (...) {
        out_xml.clear();
        return false;
    }
}

lpb_result replace_standard_jpeg_xmp_unchecked(
    std::span<const uint8_t> input,
    std::span<const uint8_t> replacement_xmp,
    std::vector<uint8_t>& out_bytes,
    std::string& out_error) noexcept
{
    out_bytes.clear();
    out_error.clear();
    try {
        return build_jpeg_xmp_replacement(input, replacement_xmp, out_bytes, out_error);
    }
    catch (...) {
        out_bytes.clear();
        out_error = "JPEG XMP replacement failed while preparing structural output.";
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

} // namespace lpb::metadata

extern "C" LPB_API lpb_result LPB_CALL lpb_jpeg_inject_xmp(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* xmp_xml,
    size_t xmp_xml_size,
    uint8_t* output,
    size_t output_size,
    size_t* out_written)
{
    if (!context || !input || !out_written) return LPB_RESULT_INVALID_ARGUMENT;
    if (xmp_xml_size > 0 && xmp_xml == nullptr) {
        set_error(context, "XMP payload pointer is null.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

#if !defined(LPB_NATIVE_TEST_HARNESS)
    // Production capability gate: in-place JPEG XMP injection rewrites file
    // bytes and requires an active Native cleanup-plan authority. The internal
    // Neutral reassembly caller uses the private structural helper directly;
    // external callers still cannot bypass this Cleaner trust chain.
    if (!lpb_has_clean_authority(context))
    {
        set_error(context, "[AuthorityViolation] In-place JPEG XMP injection requires an active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

    const std::span<const uint8_t> replacement_xmp = xmp_xml_size == 0
        ? std::span<const uint8_t>{}
        : std::span<const uint8_t>(xmp_xml, xmp_xml_size);
    std::vector<uint8_t> output_bytes;
    std::string error;
    const lpb_result result = lpb::metadata::replace_standard_jpeg_xmp_unchecked(
        std::span<const uint8_t>(input, input_size), replacement_xmp, output_bytes, error);
    if (result != LPB_RESULT_OK) {
        set_error(context, error.c_str());
        return result;
    }
    if (output == nullptr || output_size < output_bytes.size()) {
        *out_written = output_bytes.size();
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }
    std::copy(output_bytes.begin(), output_bytes.end(), output);
    *out_written = output_bytes.size();
    return LPB_RESULT_OK;
}
