#include "heif_cleaner.h"
#include "xmp_cleaner.h"
#include "media/media_cleaner.h"
#include "foundation/residue_fingerprint.h"
#include "containers/isobmff.h"
#include "foundation/internal.h"
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <vector>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

namespace fs = std::filesystem;

namespace lpb::protocols::clean {

static void add_fact(
    std::vector<lpb_removed_protocol_fact>& facts,
    const char* proto,
    const char* comp,
    const char* desc,
    const char* residue_id,
    lpb_media_artifact_kind role = LPB_ARTIFACT_PRIMARY_IMAGE,
    lpb_residue_structure_kind structure_kind = LPB_RESIDUE_ISOBMFF_BOX,
    const char* op = "Removed",
    const char* after = "Removed",
    const char* before_fp = "")
{
    lpb_removed_protocol_fact fact{};
    fact.struct_size = sizeof(fact);
    strncpy_s(fact.protocol_name, proto ? proto : "", _TRUNCATE);
    strncpy_s(fact.component, comp ? comp : "", _TRUNCATE);
    strncpy_s(fact.description, desc ? desc : "", _TRUNCATE);
    strncpy_s(fact.residue_id, residue_id ? residue_id : "", _TRUNCATE);
    fact.artifact_role = role;
    fact.structure_kind = structure_kind;
    strncpy_s(fact.operation, op ? op : "Removed", _TRUNCATE);
    strncpy_s(fact.after_status, after ? after : "Removed", _TRUNCATE);
    if (before_fp) strncpy_s(fact.before_fingerprint, before_fp, _TRUNCATE);
    facts.push_back(fact);
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
    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        fs::remove(temp, ec);
        return false;
    }
    return true;
}

static uint32_t be32(const uint8_t* p) noexcept
{
    return (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) |
        (static_cast<uint32_t>(p[2]) << 8) | p[3];
}

static uint16_t le16(const uint8_t* p) noexcept
{
    return static_cast<uint16_t>(p[0]) | (static_cast<uint16_t>(p[1]) << 8);
}

static uint32_t le32(const uint8_t* p) noexcept
{
    return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
        (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
}

static bool validate_samsung_sefd(
    const std::vector<uint8_t>& data,
    const isobmff_box_header& sefd,
    uint64_t video_offset,
    uint64_t video_length) noexcept
{
    if (sefd.header_size != 8 || sefd.size < 16 ||
        sefd.start > data.size() || sefd.size > data.size() - sefd.start) return false;
    const size_t sefd_end = sefd.start + sefd.size;
    const size_t footer_pos = sefd_end - 8;
    if (std::memcmp(data.data() + footer_pos + 4, "SEFT", 4) != 0) return false;
    const uint32_t total_size = le32(data.data() + footer_pos);
    if (total_size < 12 || static_cast<uint64_t>(total_size) > sefd.size - 8) return false;
    const size_t sefh = footer_pos - static_cast<size_t>(total_size);
    if (sefh < sefd.start + sefd.header_size || std::memcmp(data.data() + sefh, "SEFH", 4) != 0 ||
        sefh + 12 > footer_pos) return false;
    const uint32_t count = le32(data.data() + sefh + 8);
    if (count > (footer_pos - (sefh + 12)) / 12 ||
        sefh + 12 + static_cast<size_t>(count) * 12 != footer_pos) return false;

    bool found_motion = false;
    std::vector<std::pair<size_t, size_t>> payload_ranges;
    for (uint32_t i = 0; i < count; ++i) {
        const size_t entry = sefh + 12 + static_cast<size_t>(i) * 12;
        const uint16_t prefix = le16(data.data() + entry);
        const uint16_t marker = le16(data.data() + entry + 2);
        const uint32_t offset = le32(data.data() + entry + 4);
        const uint32_t size = le32(data.data() + entry + 8);
        if (size < 8 || static_cast<uint64_t>(offset) > sefh || size > offset) return false;
        const size_t payload = sefh - static_cast<size_t>(offset);
        const size_t payload_end = payload + static_cast<size_t>(size);
        if (payload < sefd.start + sefd.header_size || payload_end > sefh ||
            le16(data.data() + payload) != prefix || le16(data.data() + payload + 2) != marker) return false;
        for (const auto& range : payload_ranges) {
            if (payload < range.second && range.first < payload_end) return false;
        }
        payload_ranges.emplace_back(payload, payload_end);
        const uint32_t name_size = le32(data.data() + payload + 4);
        if (name_size > size - 8) return false;
        if (marker == 0x0A30) {
            if (found_motion || prefix != 0 || name_size != 16 || size != 36 ||
                std::memcmp(data.data() + payload + 8, "MotionPhoto_Data", 16) != 0 ||
                std::memcmp(data.data() + payload + 24, "mpv2", 4) != 0) return false;
            if (be32(data.data() + payload + 28) != video_offset ||
                be32(data.data() + payload + 32) != video_length) return false;
            found_motion = true;
        }
    }
    return found_motion;
}

static bool rewrite_samsung_xmp(
    lpb_context* context,
    std::vector<uint8_t>& data,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    uint64_t xmp_offset = 0;
    uint64_t xmp_length = 0;
    if (lpb_heif_locate_xmp_item(context, data.data(), data.size(), &xmp_offset, &xmp_length) != LPB_RESULT_OK) {
        // HEIF files without an XMP item can still be cleaned structurally.
        return true;
    }
    if (xmp_offset > data.size() || xmp_length > data.size() - static_cast<size_t>(xmp_offset)) {
        set_error(context, "Samsung HEIF XMP item is out of bounds.");
        return false;
    }
    const std::string_view whole(
        reinterpret_cast<const char*>(data.data() + static_cast<size_t>(xmp_offset)),
        static_cast<size_t>(xmp_length));
    const size_t local_xml_start = whole.find("<x:xmpmeta");
    const std::string_view xml_end_tag = "</x:xmpmeta>";
    if (local_xml_start == std::string_view::npos)
    {
        // Preserve vendor wrappers or non-XML XMP item contents we do not understand.
        return true;
    }
    const size_t xml_end = whole.find(xml_end_tag, local_xml_start);
    if (xml_end == std::string_view::npos)
    {
        set_error(context, "Samsung HEIF XMP item is truncated.");
        return false;
    }

    const size_t old_xml_length = xml_end + xml_end_tag.size() - local_xml_start;
    const size_t xml_start = static_cast<size_t>(xmp_offset) + local_xml_start;
    const std::string xmp(whole.substr(local_xml_start, old_xml_length));
    std::string cleaned_xmp;
    if (!clean_xmp_metadata_with_plan(
            xmp, LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC, actions, action_count, cleaned_xmp, out_facts))
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
    const std::vector<uint8_t>& input_bytes,
    const std::string& output_path,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    if (!actions || action_count == 0) {
        set_error(context, "Destructive cleaning requires a non-empty cleanup plan.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (input_bytes.size() < 16 || std::memcmp(input_bytes.data() + 4, "ftyp", 4) != 0) {
        set_error(context, "Input is not a structurally valid HEIF container.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    std::vector<uint8_t> input = input_bytes;

    if (!rewrite_samsung_xmp(context, input, actions, action_count, out_facts)) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> output;
    output.reserve(input.size());
    size_t offset = 0;
    bool removed_mpvd = false;
    bool removed_sefd = false;
    isobmff_box_header sefd_box{};
    uint64_t video_offset = 0;
    uint64_t video_length = 0;
    const auto* act_mpvd = lpb::media::find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC, "samsung-heic-box-mpvd",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_ISOBMFF_BOX, "mpvd", "mpvd", LPB_REMOVAL_DELETE);
    const auto* act_sefd = lpb::media::find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC, "samsung-heic-box-sefd-motion",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_ISOBMFF_BOX, "sefd", "sefd", LPB_REMOVAL_DELETE);
    const bool should_remove_mpvd = (act_mpvd != nullptr);
    const bool should_remove_sefd = (act_sefd != nullptr);
    std::string fp_mpvd_matched, fp_sefd_matched;

    while (offset < input.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(input.data(), offset, input.size(), box)) {
            set_error(context, "Malformed top-level HEIF box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const uint32_t type = be32(input.data() + offset + 4);
        if (type == 0x6D707664U) { // mpvd
            if (!should_remove_mpvd) {
                output.insert(output.end(), input.begin() + offset, input.begin() + offset + box.size);
            } else {
                if (removed_mpvd || box.header_size != 8) {
                    set_error(context, "Samsung HEIF mpvd payload is not a single valid ISO-BMFF media range.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (act_mpvd) {
                    std::string fp = lpb::crypto::compute_isobmff_box_fingerprint(
                        "mpvd", box.size, input.data() + offset, box.size);
                    if (act_mpvd->expected_fingerprint[0] == '\0' || fp != act_mpvd->expected_fingerprint) {
                        set_error(context, "Residue fingerprint missing or mismatch for samsung-heic-box-mpvd.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                    fp_mpvd_matched = std::move(fp);
                }
                const size_t candidate_video_start = offset + box.header_size;
                size_t candidate_video_end = offset + box.size;
                size_t nested_pos = candidate_video_start;
                while (nested_pos < candidate_video_end) {
                    isobmff_box_header nested{};
                    if (!try_read_box_header(input.data(), nested_pos, candidate_video_end, nested)) {
                        set_error(context, "Samsung HEIC mpvd contains a malformed child box.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                    if (std::memcmp(input.data() + nested_pos + 4, "sefd", 4) == 0) {
                        if (removed_sefd) {
                            set_error(context, "Duplicate Samsung HEIC sefd boxes.");
                            return LPB_RESULT_INVALID_ARGUMENT;
                        }
                        if (nested_pos + nested.size != offset + box.size) {
                            set_error(context, "Samsung HEIC mpvd contains data after its sefd trailer.");
                            return LPB_RESULT_INVALID_ARGUMENT;
                        }
                        if (should_remove_sefd) {
                            if (act_sefd) {
                                std::string fp = lpb::crypto::compute_isobmff_box_fingerprint(
                                    "sefd", nested.size, input.data() + nested_pos, nested.size);
                                if (act_sefd->expected_fingerprint[0] == '\0' || fp != act_sefd->expected_fingerprint) {
                                    set_error(context, "Residue fingerprint missing or mismatch for samsung-heic-box-sefd-motion.");
                                    return LPB_RESULT_INVALID_ARGUMENT;
                                }
                                fp_sefd_matched = std::move(fp);
                            }
                            removed_sefd = true;
                            sefd_box = nested;
                        }
                        candidate_video_end = nested_pos;
                        break;
                    }
                    nested_pos += nested.size;
                }
                if (candidate_video_end <= candidate_video_start ||
                    !is_valid_isobmff_media_range(input.data(), input.size(), candidate_video_start,
                        candidate_video_end - candidate_video_start)) {
                    set_error(context, "Samsung HEIF mpvd payload is not a single valid ISO-BMFF media range.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                removed_mpvd = true;
                video_offset = candidate_video_start;
                video_length = candidate_video_end - candidate_video_start;
            }
        } else if (type == 0x73656664U) { // sefd
            if (!should_remove_sefd) {
                output.insert(output.end(), input.begin() + offset, input.begin() + offset + box.size);
            } else {
                if (removed_sefd) {
                    set_error(context, "Duplicate Samsung HEIF sefd boxes.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (act_sefd) {
                    std::string fp = lpb::crypto::compute_isobmff_box_fingerprint(
                        "sefd", box.size, input.data() + offset, box.size);
                    if (act_sefd->expected_fingerprint[0] == '\0' || fp != act_sefd->expected_fingerprint) {
                        set_error(context, "Residue fingerprint missing or mismatch for samsung-heic-box-sefd-motion.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                    fp_sefd_matched = std::move(fp);
                }
                removed_sefd = true;
                sefd_box = box;
            }
        } else {
            output.insert(output.end(), input.begin() + offset,
                input.begin() + offset + box.size);
        }
        offset += box.size;
    }
    if ((should_remove_mpvd && !removed_mpvd) || (should_remove_sefd && !removed_sefd) ||
        ((should_remove_mpvd || should_remove_sefd) && !validate_samsung_sefd(input, sefd_box, video_offset, video_length))) {
        set_error(context, "Validated Samsung HEIC mpvd/sefd structure was not found or was malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (!write_atomic(utf8_to_path(output_path.c_str()), output)) {
        set_error(context, "Failed to publish cleaned Samsung HEIC.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (removed_mpvd) {
        add_fact(out_facts, "Samsung", "HEIF mpvd Box", "Removed the validated Samsung Motion Photo mpvd payload box",
            "samsung-heic-box-mpvd", LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_ISOBMFF_BOX, "Removed", "Removed", fp_mpvd_matched.c_str());
    }
    if (removed_sefd) {
        add_fact(out_facts, "Samsung", "HEIF sefd Box", "Removed the validated Samsung Motion Photo sefd metadata box",
            "samsung-heic-box-sefd-motion", LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_ISOBMFF_BOX, "Removed", "Removed", fp_sefd_matched.c_str());
    }
    return LPB_RESULT_OK;
}

} // namespace lpb::protocols::clean
