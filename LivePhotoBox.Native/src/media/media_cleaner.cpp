#include "media/media_cleaner.h"
#include "media/media_inspector.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "metadata/jpeg.h"
#include "containers/isobmff.h"

#include <fstream>
#include <filesystem>
#include <regex>
#include <string>
#include <vector>
#include <cstring>
#include <algorithm>

namespace fs = std::filesystem;

namespace lpb::media {

static bool read_file_binary(const std::string& path, std::vector<uint8_t>& out_data) {
    auto p = utf8_to_path(path.c_str());
    std::ifstream ifs(p, std::ios::binary | std::ios::ate);
    if (!ifs.is_open()) return false;
    auto sz = ifs.tellg();
    if (sz < 0) return false;
    out_data.resize(static_cast<size_t>(sz));
    ifs.seekg(0, std::ios::beg);
    ifs.read(reinterpret_cast<char*>(out_data.data()), sz);
    return ifs.good();
}

static bool write_file_binary(const std::string& path, const std::vector<uint8_t>& data) {
    auto p = utf8_to_path(path.c_str());
    std::ofstream ofs(p, std::ios::binary | std::ios::trunc);
    if (!ofs.is_open()) return false;
    ofs.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
    return ofs.good();
}

static lpb_result fast_file_copy(lpb_context* context, const char* in_path, const char* out_path) {
    if (!in_path || !out_path) return LPB_RESULT_INVALID_ARGUMENT;
    auto p_in = utf8_to_path(in_path);
    auto p_out = utf8_to_path(out_path);
    std::error_code ec;
    fs::copy_file(p_in, p_out, fs::copy_options::overwrite_existing, ec);
    if (ec) {
        set_error(context, ("Failed to copy media file: " + ec.message()).c_str());
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static std::string extract_xmp_string(const std::vector<uint8_t>& data) {
    const std::string start_tag = "<x:xmpmeta";
    const std::string end_tag = "</x:xmpmeta>";

    std::string_view sv(reinterpret_cast<const char*>(data.data()), data.size());
    auto start_pos = sv.find(start_tag);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(end_tag, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + end_tag.length() - start_pos));
        }
    }

    const std::string rdf_start = "<rdf:RDF";
    const std::string rdf_end = "</rdf:RDF>";
    start_pos = sv.find(rdf_start);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(rdf_end, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + rdf_end.length() - start_pos));
        }
    }

    return {};
}

static bool strip_xmp_live_tags(
    const std::string& input_xmp,
    lpb_source_protocol protocol,
    std::string& output_xmp,
    std::vector<std::string>& out_facts)
{
    if (input_xmp.empty()) return false;
    std::string cleaned = input_xmp;
    bool modified = false;

    // 1. Strip MicroVideo attributes (Google V1)
    if (protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1 || protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2) {
        static const std::regex micro_re(R"(\s*(?:GCamera:)?MicroVideo(?:Version|Offset|PresentationTimestampUs)?="[^"]*")");
        if (std::regex_search(cleaned, micro_re)) {
            cleaned = std::regex_replace(cleaned, micro_re, "");
            out_facts.push_back("Google.MicroVideo.Xmp");
            modified = true;
        }
    }

    // 2. Strip MotionPhoto attributes & container items (Google V2 / Xiaomi / OPPO / vivo / Samsung)
    static const std::regex motion_attr_re(R"(\s*(?:GCamera:|VCamera:|OpCamera:)?(?:MotionPhoto|MotionPhotoVersion|MotionPhotoPresentationTimestampUs|MotionPhotoPrimaryPresentationTimestampUs|MotionPhotoOwner|OLivePhotoVersion|VideoLength|VMotionPhotoVersion|VMotionPhotoSource|VMotionPhotoFlags|VMediaKitVersion|MotionPhoto_Data|MotionPhoto_Version)="[^"]*")");
    if (std::regex_search(cleaned, motion_attr_re)) {
        cleaned = std::regex_replace(cleaned, motion_attr_re, "");
        out_facts.push_back("MotionPhoto.Attributes.Xmp");
        modified = true;
    }

    // 3. Strip Container Directory Items for video/mp4 (e.g. <Container:Item ... Item:Mime="video/mp4" ... />)
    static const std::regex item_re1(R"(<Container:Item[^>]*?(?:Item:Mime="video/mp4"|Item:Semantic="MotionPhoto")[^>]*?/>\s*)");
    if (std::regex_search(cleaned, item_re1)) {
        cleaned = std::regex_replace(cleaned, item_re1, "");
        out_facts.push_back("Container.Item.MotionPhoto");
        modified = true;
    }

    static const std::regex item_re2(R"(<rdf:li[^>]*?(?:Item:Mime="video/mp4"|Item:Semantic="MotionPhoto")[^>]*?/>\s*)");
    if (std::regex_search(cleaned, item_re2)) {
        cleaned = std::regex_replace(cleaned, item_re2, "");
        out_facts.push_back("Container.Item.MotionPhoto");
        modified = true;
    }

    static const std::regex item_re3(R"(<(?:Container:Item|rdf:li)[^>]*?>[\s\S]*?(?:video/mp4|MotionPhoto)[\s\S]*?</(?:Container:Item|rdf:li)>\s*)");
    if (std::regex_search(cleaned, item_re3)) {
        cleaned = std::regex_replace(cleaned, item_re3, "");
        out_facts.push_back("Container.Item.MotionPhoto");
        modified = true;
    }

    if (modified) {
        output_xmp = std::move(cleaned);
        return true;
    }
    return false;
}

static lpb_result clean_jpeg_xmp(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    lpb_source_protocol protocol,
    std::vector<std::string>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read input JPEG for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    std::string xmp = extract_xmp_string(data);
    if (xmp.empty()) {
        return fast_file_copy(context, in_path.c_str(), out_path.c_str());
    }

    std::string cleaned_xmp;
    if (strip_xmp_live_tags(xmp, protocol, cleaned_xmp, out_facts)) {
        std::vector<uint8_t> out_buf(data.size() + cleaned_xmp.size() + 4096);
        size_t written = 0;
        lpb_result res = lpb_jpeg_inject_xmp(
            context,
            data.data(), data.size(),
            reinterpret_cast<const uint8_t*>(cleaned_xmp.data()), cleaned_xmp.size(),
            out_buf.data(), out_buf.size(),
            &written);
        if (res == LPB_RESULT_OK && written > 0) {
            out_buf.resize(written);
            if (write_file_binary(out_path, out_buf)) {
                return LPB_RESULT_OK;
            }
        }
    }

    return fast_file_copy(context, in_path.c_str(), out_path.c_str());
}

static lpb_result clean_apple_image(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<std::string>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read Apple image for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    lpb_result res = lpb_apple_strip_live_photo_entries(context, data.data(), data.size());
    if (res == LPB_RESULT_OK) {
        out_facts.push_back("Apple.MakerNote.ContentIdentifier");
    }

    std::string xmp = extract_xmp_string(data);
    if (!xmp.empty()) {
        static const std::regex apple_cid_re(R"(\s*(?:apple-desktop:|apple-fi:)?(?:ContentIdentifier|PhotoIdentifier)="[^"]*")");
        if (std::regex_search(xmp, apple_cid_re)) {
            std::string cleaned_xmp = std::regex_replace(xmp, apple_cid_re, "");
            if (data.size() > 2 && data[0] == 0xFF && data[1] == 0xD8) {
                std::vector<uint8_t> out_buf(data.size() + cleaned_xmp.size() + 4096);
                size_t written = 0;
                if (lpb_jpeg_inject_xmp(context, data.data(), data.size(), reinterpret_cast<const uint8_t*>(cleaned_xmp.data()), cleaned_xmp.size(), out_buf.data(), out_buf.size(), &written) == LPB_RESULT_OK && written > 0) {
                    out_buf.resize(written);
                    data = std::move(out_buf);
                    out_facts.push_back("Apple.Xmp.ContentIdentifier");
                }
            }
        }
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned Apple image.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_apple_video(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<std::string>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read Apple video for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    const char* starts[] = {
        "com.apple.quicktime.content.identifier",
        "com.apple.quicktime.live-photo"
    };
    std::vector<uint8_t> out_a(data.size() + 4096);
    size_t written_a = 0;
    if (lpb_mp4_strip_mdta_keys(context, data.data(), data.size(), starts, 2, nullptr, 0, nullptr, 0, out_a.data(), out_a.size(), &written_a) == LPB_RESULT_OK && written_a > 0) {
        out_a.resize(written_a);
        data = std::move(out_a);
        out_facts.push_back("Apple.QuickTime.MdtaKeys");
    }

    const char* track_patterns[] = { "mebx", "still-image-time" };
    std::vector<uint8_t> out_b(data.size() + 4096);
    size_t written_b = 0;
    if (lpb_mp4_strip_stsd_tracks(context, data.data(), data.size(), track_patterns, 2, out_b.data(), out_b.size(), &written_b) == LPB_RESULT_OK && written_b > 0) {
        out_b.resize(written_b);
        data = std::move(out_b);
        out_facts.push_back("Apple.QuickTime.MebxTrack");
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned Apple video.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_vivo_legacy_image(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<std::string>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read vivo legacy image for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (data.size() > 100) {
        std::string_view sv(reinterpret_cast<const char*>(data.data()), data.size());
        size_t scan_start = data.size() > 1024 * 1024 ? data.size() - 1024 * 1024 : 0;
        std::string_view tail_sv = sv.substr(scan_start);

        size_t album_pos = tail_sv.find("cameralbum!");
        size_t json_pos = tail_sv.find("vivo{");

        size_t cut_pos = std::string::npos;
        if (album_pos != std::string_view::npos) cut_pos = scan_start + album_pos;
        if (json_pos != std::string_view::npos && (cut_pos == std::string::npos || scan_start + json_pos < cut_pos)) {
            cut_pos = scan_start + json_pos;
        }

        if (cut_pos != std::string::npos) {
            for (size_t p = cut_pos; p >= 2; --p) {
                if (data[p - 2] == 0xFF && data[p - 1] == 0xD9) {
                    data.resize(p);
                    out_facts.push_back("Vivo.Legacy.AlbumTail");
                    break;
                }
            }
        }
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned vivo legacy image.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_vivo_legacy_video(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<std::string>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read vivo legacy video for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    const char* starts[] = {
        "com.android.camera.livephoto",
        "com.android.camera.imageTime",
        "com.vivo.gallery.livePhoto",
        "bestTime"
    };
    std::vector<uint8_t> out_a(data.size() + 4096);
    size_t written_a = 0;
    if (lpb_mp4_strip_mdta_keys(context, data.data(), data.size(), starts, 4, nullptr, 0, nullptr, 0, out_a.data(), out_a.size(), &written_a) == LPB_RESULT_OK && written_a > 0) {
        out_a.resize(written_a);
        data = std::move(out_a);
        out_facts.push_back("Vivo.Legacy.MdtaKeys");
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned vivo legacy video.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

lpb_result clean_source_protocol(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const char* input_image_path,
    const char* input_video_path,
    const char* output_image_path,
    const char* output_video_path,
    char* out_removed_facts,
    size_t removed_facts_buffer_size)
{
    if (!context || !facts || !input_image_path || !output_image_path) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<std::string> removed_facts;
    lpb_result res = LPB_RESULT_OK;

    switch (facts->protocol) {
    case LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO:
        res = clean_apple_image(context, input_image_path, output_image_path, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = clean_apple_video(context, input_video_path, output_video_path, removed_facts);
        }
        break;

    case LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1:
        res = clean_jpeg_xmp(context, input_image_path, output_image_path, static_cast<lpb_source_protocol>(facts->protocol), removed_facts);
        removed_facts.push_back("Google.MicroVideo.Trailer");
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2:
        res = clean_jpeg_xmp(context, input_image_path, output_image_path, static_cast<lpb_source_protocol>(facts->protocol), removed_facts);
        removed_facts.push_back("Google.MotionPhoto.Trailer");
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO:
        res = clean_jpeg_xmp(context, input_image_path, output_image_path, static_cast<lpb_source_protocol>(facts->protocol), removed_facts);
        removed_facts.push_back("Oppo.OLivePhoto.Trailer");
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_VIVO_X300:
        res = clean_jpeg_xmp(context, input_image_path, output_image_path, static_cast<lpb_source_protocol>(facts->protocol), removed_facts);
        removed_facts.push_back("Vivo.X300.Trailer");
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL:
        res = clean_vivo_legacy_image(context, input_image_path, output_image_path, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = clean_vivo_legacy_video(context, input_video_path, output_video_path, removed_facts);
        }
        break;

    case LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG:
        res = clean_jpeg_xmp(context, input_image_path, output_image_path, static_cast<lpb_source_protocol>(facts->protocol), removed_facts);
        removed_facts.push_back("Samsung.SEF.Trailer");
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC:
        res = fast_file_copy(context, input_image_path, output_image_path);
        removed_facts.push_back("Samsung.Heic.MpvdBox");
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO:
    case LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO:
        res = fast_file_copy(context, input_image_path, output_image_path);
        removed_facts.push_back("Huawei.LiveTail");
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_NON_LIVE:
    default:
        res = fast_file_copy(context, input_image_path, output_image_path);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;
    }

    if (res == LPB_RESULT_OK && out_removed_facts && removed_facts_buffer_size > 0) {
        std::string joined;
        for (size_t i = 0; i < removed_facts.size(); ++i) {
            if (i > 0) joined += ",";
            joined += removed_facts[i];
        }
        strncpy_s(out_removed_facts, removed_facts_buffer_size, joined.c_str(), _TRUNCATE);
    }

    return res;
}

} // namespace lpb::media
