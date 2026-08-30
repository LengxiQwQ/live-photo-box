#include "foundation/internal.h"
#include "binary/endian.h"
#include "jpeg.h"
#include <cstring>

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

    if (input_size < 2 || input[0] != 0xFF || input[1] != 0xD8) {
        set_error(context, "Input is not a valid JPEG (missing SOI).");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<jpeg_segment> segments;
    size_t pos = 2; // skip SOI
    size_t sos_pos = 0;

    while (pos < input_size) {
        if (input[pos] != 0xFF) {
            // Not a marker? Could be padding or corrupted.
            break;
        }
        
        // Skip padding FF
        size_t marker_start = pos;
        while (pos < input_size && input[pos] == 0xFF) {
            pos++;
        }
        
        if (pos >= input_size) break;
        uint8_t marker = input[pos];
        pos++;

        if (marker == 0x00 || marker >= 0xD0 && marker <= 0xD7) {
            // RSTn or escaped FF, not standalone segments with length.
            // But they shouldn't appear outside entropy-coded data.
            continue;
        }

        if (marker == 0xD9) { // EOI
            break;
        }

        if (marker == 0xDA) { // SOS
            sos_pos = marker_start;
            break;
        }

        if (pos + 1 >= input_size) break;
        size_t len = (static_cast<size_t>(input[pos]) << 8) | input[pos + 1];
        if (pos + len > input_size) break;

        bool is_xmp = false;
        if (marker == 0xE1 && len >= 2 + XMP_HEADER_SIZE) {
            if (std::memcmp(input + pos + 2, XMP_HEADER, XMP_HEADER_SIZE) == 0) {
                is_xmp = true;
            }
        }

        segments.push_back({ marker_start, pos - marker_start + 1, len, marker, is_xmp });
        pos += len;
    }

    if (sos_pos == 0) {
        // No SOS found, maybe just segments or truncated.
        sos_pos = pos;
    }

    // Prepare new APP1 XMP
    size_t new_xmp_len = 0;
    std::vector<uint8_t> new_xmp_segment;
    if (xmp_xml && xmp_xml_size > 0) {
        new_xmp_len = 2 + XMP_HEADER_SIZE + xmp_xml_size;
        if (new_xmp_len > 65535) {
            set_error(context, "XMP metadata exceeds JPEG segment size limit.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        new_xmp_segment.reserve(2 + new_xmp_len);
        new_xmp_segment.push_back(0xFF);
        new_xmp_segment.push_back(0xE1);
        new_xmp_segment.push_back(static_cast<uint8_t>(new_xmp_len >> 8));
        new_xmp_segment.push_back(static_cast<uint8_t>(new_xmp_len & 0xFF));
        new_xmp_segment.insert(new_xmp_segment.end(), XMP_HEADER, XMP_HEADER + XMP_HEADER_SIZE);
        new_xmp_segment.insert(new_xmp_segment.end(), xmp_xml, xmp_xml + xmp_xml_size);
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

    // Calculate total required size
    size_t total_size = 2; // SOI
    for (size_t i = 0; i < segments.size(); i++) {
        total_size += segments[i].marker_size + segments[i].payload_size;
        if (i + 1 == insert_idx) {
            total_size += new_xmp_segment.size();
        }
    }
    if (insert_idx == 0) {
        total_size += new_xmp_segment.size();
    }
    
    // Add rest of the file (SOS through EOF, including trailing data)
    size_t remaining = input_size - sos_pos;
    total_size += remaining;

    *out_written = total_size;
    if (!output || output_size < total_size) {
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }

    // Write output
    size_t out_pos = 0;
    output[out_pos++] = 0xFF;
    output[out_pos++] = 0xD8;

    if (insert_idx == 0 && !new_xmp_segment.empty()) {
        std::memcpy(output + out_pos, new_xmp_segment.data(), new_xmp_segment.size());
        out_pos += new_xmp_segment.size();
    }

    for (size_t i = 0; i < segments.size(); i++) {
        size_t seg_len = segments[i].marker_size + segments[i].payload_size;
        std::memcpy(output + out_pos, input + segments[i].start, seg_len);
        out_pos += seg_len;
        
        if (i + 1 == insert_idx && !new_xmp_segment.empty()) {
            std::memcpy(output + out_pos, new_xmp_segment.data(), new_xmp_segment.size());
            out_pos += new_xmp_segment.size();
        }
    }

    std::memcpy(output + out_pos, input + sos_pos, remaining);
    return LPB_RESULT_OK;
}
