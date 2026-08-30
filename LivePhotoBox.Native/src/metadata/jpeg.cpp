#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "jpeg.h"

using namespace lpb;

namespace {

const uint8_t XMP_HEADER[] = {
    'h', 't', 't', 'p', ':', '/', '/', 'n', 's', '.', 'a', 'd', 'o', 'b', 'e', '.', 'c', 'o', 'm', '/',
    'x', 'a', 'p', '/', '1', '.', '0', '/', 0
};
constexpr size_t XMP_HEADER_SIZE = sizeof(XMP_HEADER);

struct jpeg_segment {
    size_t start;
    size_t marker_size; // usually 2 (0xFFXX)
    size_t payload_size; // 2 (length) + length_value - 2
    uint8_t marker;
    bool is_xmp;
};

} // namespace

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

    binary_reader reader(input, input_size);
    uint16_t soi = 0;
    if (!reader.try_read_be16u(soi) || soi != 0xFFD8) {
        set_error(context, "Input is not a valid JPEG (missing SOI).");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<jpeg_segment> segments;
    size_t sos_pos = 0;

    while (reader.remaining() > 0) {
        uint8_t current_byte = 0;
        if (!reader.try_read_u8(current_byte) || current_byte != 0xFF) {
            break;
        }
        
        size_t marker_start = reader.position() - 1;
        while (reader.remaining() > 0 && reader.data()[reader.position()] == 0xFF) {
            reader.skip(1);
        }
        
        uint8_t marker = 0;
        if (!reader.try_read_u8(marker)) break;

        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (marker == 0xD9) break; // EOI
        
        if (marker == 0xDA) { // SOS
            sos_pos = marker_start;
            break;
        }

        uint16_t len = 0;
        if (!reader.try_read_be16u(len)) break;
        if (len < 2) break; // Invalid length
        
        size_t payload_size = len;
        if (reader.remaining() < static_cast<size_t>(payload_size - 2)) break;

        bool is_xmp = false;
        if (marker == 0xE1 && payload_size >= 2 + XMP_HEADER_SIZE) {
            if (std::memcmp(reader.current_ptr(), XMP_HEADER, XMP_HEADER_SIZE) == 0) {
                is_xmp = true;
            }
        }

        segments.push_back({ marker_start, reader.position() - 2 - marker_start, payload_size, marker, is_xmp });
        reader.skip(payload_size - 2);
    }

    if (sos_pos == 0) {
        // No SOS found, maybe just segments or truncated.
        sos_pos = reader.position();
    }

    // Prepare new APP1 XMP
    size_t new_xmp_len = 0;
    std::vector<uint8_t> new_xmp_segment;
    if (xmp_xml && xmp_xml_size > 0) {
        new_xmp_len = 2 + XMP_HEADER_SIZE + xmp_xml_size;
        if (new_xmp_len > 0xFFFF) {
            set_error(context, "XMP XML is too large for APP1 segment.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        
        new_xmp_segment.resize(2 + new_xmp_len);
        binary_writer writer(new_xmp_segment.data(), new_xmp_segment.size());
        writer.try_write_u8(0xFF);
        writer.try_write_u8(0xE1);
        writer.try_write_be16(static_cast<uint16_t>(new_xmp_len));
        writer.try_write_bytes(XMP_HEADER, XMP_HEADER_SIZE);
        writer.try_write_bytes(xmp_xml, xmp_xml_size);
    }

    // Determine insertion index for new XMP (after APP0 or EXIF if present)
    size_t insert_idx = 0;
    for (size_t i = 0; i < segments.size(); i++) {
        if (segments[i].marker == 0xE0) { // APP0
            insert_idx = i + 1;
        } else if (segments[i].marker == 0xE1 && !segments[i].is_xmp) { // Exif APP1
            insert_idx = i + 1;
        }
    }

    // Calculate total size
    size_t total_expected = 2; // SOI
    if (!new_xmp_segment.empty()) {
        total_expected += new_xmp_segment.size();
    }
    
    for (const auto& seg : segments) {
        if (true) {
            total_expected += seg.marker_size + seg.payload_size;
        }
    }
    total_expected += (input_size - sos_pos); // SOS to EOI

    if (output == nullptr || output_size < total_expected) {
        *out_written = total_expected;
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }

    binary_writer out_writer(output, output_size);
    out_writer.try_write_be16(0xFFD8);

    if (insert_idx == 0 && !new_xmp_segment.empty()) {
        out_writer.try_write_bytes(new_xmp_segment.data(), new_xmp_segment.size());
    }

    for (size_t i = 0; i < segments.size(); i++) {
        if (true) {
            out_writer.try_write_bytes(input + segments[i].start, segments[i].marker_size + segments[i].payload_size);
        }
        if (i + 1 == insert_idx && !new_xmp_segment.empty()) {
            out_writer.try_write_bytes(new_xmp_segment.data(), new_xmp_segment.size());
        }
    }

    out_writer.try_write_bytes(input + sos_pos, input_size - sos_pos);

    *out_written = out_writer.position();
    return LPB_RESULT_OK;
}
